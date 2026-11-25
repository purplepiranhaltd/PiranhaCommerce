using System.ComponentModel.DataAnnotations;
using Microsoft.EntityFrameworkCore;
using PiranhaCommerce.Core.Domain;
using PiranhaCommerce.Core.Validation;
using PiranhaCommerce.Data;
using PiranhaCommerce.Core.Identity;

namespace PiranhaCommerce.Customers;

/// <summary>
/// Proof that a customer's personal data was erased, kept so a request can be shown to have been honoured. It holds NO personal data:
/// the customer's old id (a random GUID that no longer points at anything), when, by whom (staff), and how many records each part changed.
/// </summary>
public class CustomerErasure : Entity
{
    public Guid CustomerId { get; set; }
    public DateTimeOffset ErasedUtc { get; set; }
    public string? Actor { get; set; }
    /// <summary>For example "orders: 3, baskets: 1". Counts only.</summary>
    public string? Details { get; set; }
}

/// <summary>Who a request is about. Contributors match their own records by <see cref="CustomerId"/> and, where they keep guest records, by <see cref="Email"/>.</summary>
public sealed record CustomerSubject(Guid CustomerId, string Email);

/// <summary>
/// How a package that keeps personal data about customers (orders, loyalty, reviews...) takes part in export and erasure. Register one
/// with <c>services.AddSingleton&lt;ICustomerDataContributor, T&gt;()</c>. Customers itself owns the customer record, address book and login.
/// </summary>
public interface ICustomerDataContributor
{
    /// <summary>A short stable name for this package's part of an export and of the erasure record, e.g. "orders".</summary>
    string Name { get; }

    /// <summary>
    /// Reasons erasure must be refused for now (an order still open, a refund still owed). Empty when it can go ahead. Never put personal
    /// data in a reason: it is shown to staff and returned by the API. Runs inside the erasure transaction, so the answer cannot go stale.
    /// </summary>
    Task<IReadOnlyList<string>> CheckErasureAsync(CustomerSubject subject, CancellationToken ct = default);

    /// <summary>Everything held about the person, as plain data that serialises to JSON. Null when there is none.</summary>
    Task<object?> ExportAsync(CustomerSubject subject, CancellationToken ct = default);

    /// <summary>
    /// Erases or anonymises the person's data. Runs INSIDE the transaction the erasure service opened on the same context: do not open
    /// your own, and do not save outside it. Records that must be kept (for accounts) are anonymised, not deleted. Returns how many
    /// records changed.
    /// </summary>
    Task<int> EraseAsync(CustomerSubject subject, CancellationToken ct = default);
}

public sealed class EraseCustomerInput
{
    /// <summary>The customer's email address, typed to confirm. A mistyped id can then never erase the wrong person.</summary>
    [Required, StringLength(256)]
    public string ConfirmEmail { get; set; } = "";
}

public sealed record CustomerExportDto(
    DateTimeOffset ExportedUtc, CustomerDto Customer, IReadOnlyList<AddressDto> Addresses, bool HasSignIn, IReadOnlyList<SignInEventDto> SignInHistory,
    IReadOnlyDictionary<string, object?> Sections, IReadOnlyList<string>? Groups = null, bool TwoFactorEnabled = false);

public sealed record CustomerErasureResultDto(Guid CustomerId, DateTimeOffset ErasedUtc, IReadOnlyDictionary<string, int> Changed);

public interface ICustomerErasureService
{
    /// <summary>
    /// Everything held about the customer, for staff: the record (including staff-only notes), addresses, and each package's part.
    /// Null when the customer does not exist.
    /// </summary>
    [RequiresPermission(Permissions.CustomersManage)]
    Task<CustomerExportDto?> ExportAsync(Guid customerId, CancellationToken ct = default);

    /// <summary>
    /// Erases the customer: record, address book, sign-in and reset links are deleted, and every contributor anonymises its records, in
    /// one transaction. Null when the customer does not exist. Validation error when the confirmation email is wrong; conflict (with the
    /// reasons) when a contributor says it cannot be done yet.
    /// </summary>
    [RequiresPermission(Permissions.CustomersManage)]
    Task<CustomerErasureResultDto?> EraseAsync(Guid customerId, EraseCustomerInput input, string? actor = null, CancellationToken ct = default);
}

internal sealed class CustomerErasureService(PiranhaCommerceDbContext db, IEnumerable<ICustomerDataContributor> contributors) : ICustomerErasureService
{
    private sealed class MissingException : Exception;

    public async Task<CustomerExportDto?> ExportAsync(Guid customerId, CancellationToken ct = default)
    {
        var c = await db.Set<Customer>().AsNoTracking().Where(x => x.Id == customerId)
            .Select(x => new CustomerDto(x.Id, x.Email, x.FirstName, x.LastName, x.Phone, x.Notes, x.Active, x.CreatedUtc, x.UpdatedUtc)).FirstOrDefaultAsync(ct);
        if (c is null) return null;

        var addresses = await db.Set<CustomerAddress>().AsNoTracking().Where(a => a.CustomerId == customerId).OrderBy(a => a.Label).ThenBy(a => a.Id)
            .Select(a => new AddressDto(a.Id, a.CustomerId, a.Label, a.Recipient, a.Line1, a.Line2, a.City, a.Region, a.PostalCode, a.CountryCode)).ToListAsync(ct);
        var hasLogin = await db.Set<CustomerLogin>().AsNoTracking().AnyAsync(l => l.CustomerId == customerId, ct); // never the password hash

        var history = await db.Set<CustomerSignInEvent>().AsNoTracking().Where(e => e.CustomerId == customerId).OrderBy(e => e.OccurredUtc).ThenBy(e => e.Id)
            .Select(e => new SignInEventDto(e.OccurredUtc, e.Actor, e.Action, e.Detail)).ToListAsync(ct);

        var subject = new CustomerSubject(customerId, c.Email);
        var sections = new Dictionary<string, object?>();
        foreach (var contributor in contributors.OrderBy(x => x.Name, StringComparer.Ordinal))
            sections[contributor.Name] = await contributor.ExportAsync(subject, ct);
        var groups = await (from m in db.Set<CustomerGroupMember>().AsNoTracking() join g in db.Set<CustomerGroup>().AsNoTracking() on m.GroupId equals g.Id where m.CustomerId == customerId orderby g.NormalizedName select g.Name).ToListAsync(ct);
        var twoFactor = await db.Set<CustomerTwoFactor>().AsNoTracking().AnyAsync(t => t.CustomerId == customerId && t.EnabledUtc != null, ct); // never the key or the codes
        return new CustomerExportDto(DateTimeOffset.UtcNow, c, addresses, hasLogin, history, sections, groups, twoFactor);
    }

    public async Task<CustomerErasureResultDto?> EraseAsync(Guid customerId, EraseCustomerInput input, string? actor = null, CancellationToken ct = default)
    {
        ModelValidator.ValidateOrThrow(input);

        var email = await db.Set<Customer>().AsNoTracking().Where(c => c.Id == customerId).Select(c => c.Email).FirstOrDefaultAsync(ct);
        if (email is null) return null;
        if (!string.Equals(input.ConfirmEmail.Trim(), email, StringComparison.OrdinalIgnoreCase))
            throw new PiranhaCommerceValidationException(nameof(EraseCustomerInput.ConfirmEmail), "That is not this customer's email address.");

        var erasedUtc = DateTimeOffset.UtcNow;
        var changed = new Dictionary<string, int>();
        var strategy = db.Database.CreateExecutionStrategy();
        try
        {
            await strategy.ExecuteInTransactionAsync(
                async token =>
                {
                    db.ChangeTracker.Clear();
                    changed.Clear();

                    // Lock the customer row first. Two simultaneous erasures are then one after the other (the second finds nothing), and a
                    // second request for a customer that is already gone is simply "not found".
                    var locked = await db.Set<Customer>().Where(c => c.Id == customerId)
                        .ExecuteUpdateAsync(s => s.SetProperty(c => c.UpdatedUtc, (DateTimeOffset?)erasedUtc), token);
                    if (locked == 0) throw new MissingException();
                    var subject = new CustomerSubject(customerId, await db.Set<Customer>().AsNoTracking().Where(c => c.Id == customerId).Select(c => c.Email).FirstAsync(token));

                    var reasons = new List<string>();
                    foreach (var contributor in contributors) reasons.AddRange(await contributor.CheckErasureAsync(subject, token));
                    if (reasons.Count > 0)
                        throw new PiranhaCommerceConflictException("This customer's data cannot be erased yet: " + string.Join(" ", reasons));

                    foreach (var contributor in contributors.OrderBy(x => x.Name, StringComparer.Ordinal))
                        changed[contributor.Name] = await contributor.EraseAsync(subject, token);

                    // The record, address book, sign-in and reset links all go with the customer (cascade in the database).
                    await db.Set<Customer>().Where(c => c.Id == customerId).ExecuteDeleteAsync(token);

                    db.Add(new CustomerErasure
                    {
                        CustomerId = customerId, ErasedUtc = erasedUtc, Actor = actor,
                        Details = string.Join(", ", changed.Where(kv => kv.Value > 0).OrderBy(kv => kv.Key, StringComparer.Ordinal).Select(kv => $"{kv.Key}: {kv.Value}")), // only what held something, so adding a package does not change old wording
                    });
                    await db.SaveChangesAsync(token);
                    return true;
                },
                async token => await db.Set<CustomerErasure>().AsNoTracking().AnyAsync(e => e.CustomerId == customerId, token),
                ct);
        }
        catch (MissingException)
        {
            db.ChangeTracker.Clear();
            return null;
        }
        catch (DbUpdateException)
        {
            db.ChangeTracker.Clear();
            throw new PiranhaCommerceConflictException("The erasure could not be completed and nothing was changed. Please try again.");
        }

        return new CustomerErasureResultDto(customerId, erasedUtc, changed);
    }
}
