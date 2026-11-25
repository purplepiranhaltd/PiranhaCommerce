using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using PiranhaCommerce.Core.Validation;
using PiranhaCommerce.Data;

namespace PiranhaCommerce.Customers;

/// <summary>An existing customer found by email address, for a package that signs shoppers in some other way (social sign-in).</summary>
/// <param name="HasSignIn">They have a login (a password, or a placeholder made for a social account).</param>
/// <param name="EmailVerified">They have proved the address, so a stranger cannot have registered it first.</param>
public sealed record ExistingCustomer(Guid Id, bool HasSignIn, bool EmailVerified);

/// <summary>
/// What a package needs to sign shoppers in by some means other than a password, while keeping every rule that already protects them:
/// switched-off customers, lockout, two-factor and the verified-email rule all still apply. The package proves WHO the shopper is
/// (it is the one that trusts a provider); this decides whether they may come in and hands back what the token endpoint needs.
/// </summary>
public interface ICustomerExternalAccess
{
    /// <summary>The customer with this address, or null.</summary>
    Task<ExistingCustomer?> FindByEmailAsync(string email, CancellationToken ct = default);

    /// <summary>
    /// Makes a customer with no password the shopper knows (a random one nobody is told, so password sign-in is closed until they choose one
    /// with "forgot password"). Conflict when the address is already a customer. <paramref name="emailVerified"/> records that the provider vouched for it.
    /// </summary>
    Task<ProfileDto> CreateAsync(string email, string? firstName, string? lastName, bool emailVerified, CancellationToken ct = default);

    /// <summary>
    /// The provider has vouched for this address and the shop has chosen to trust that: makes the sign-in belong to the person who holds the
    /// address. A password set by whoever registered it first is wiped (they may have been a stranger who got there first), every live
    /// session ends, and the address counts as verified. Makes the sign-in if the customer had none.
    /// </summary>
    Task AdoptAsync(Guid customerId, CancellationToken ct = default);

    /// <summary>
    /// The shopper has proved who they are some other way. Applies the rest: the customer must be active and not locked out, then
    /// two-factor (a code is asked for, or setup is when the shop requires it). <paramref name="code"/> is the app or recovery code.
    /// </summary>
    Task<SignInResult> SignInAsync(Guid customerId, string? code, CancellationToken ct = default);

    /// <summary>Adds a line to the customer's sign-in history, with no actor (it was the shopper).</summary>
    Task RecordAsync(Guid customerId, string action, string? detail, CancellationToken ct = default);
}

internal sealed partial class CustomerAccountService : ICustomerExternalAccess
{
    public async Task<ExistingCustomer?> FindByEmailAsync(string email, CancellationToken ct = default)
    {
        var normalized = Normalize(email);
        var found = await (from c in db.Set<Customer>().AsNoTracking()
                           where c.NormalizedEmail == normalized
                           select new { c.Id, Login = db.Set<CustomerLogin>().Where(l => l.CustomerId == c.Id).Select(l => new { l.EmailVerifiedUtc }).FirstOrDefault() })
            .FirstOrDefaultAsync(ct);
        return found is null ? null : new ExistingCustomer(found.Id, found.Login is not null, found.Login?.EmailVerifiedUtc is not null);
    }

    public async Task<ProfileDto> CreateAsync(string email, string? firstName, string? lastName, bool emailVerified, CancellationToken ct = default)
    {
        var normalized = Normalize(email);
        if (await db.Set<Customer>().AnyAsync(c => c.NormalizedEmail == normalized, ct)) throw Taken();
        var customer = new Customer { Email = email.Trim(), NormalizedEmail = normalized, FirstName = Clean(firstName), LastName = Clean(lastName) };
        var now = DateTimeOffset.UtcNow;
        db.Add(customer);
        db.Add(NewPlaceholderLogin(customer.Id, now, emailVerified));
        try { await db.SaveChangesAsync(ct); }
        catch (DbUpdateException)
        {
            db.ChangeTracker.Clear();
            throw Taken();
        }
        return ToProfile(customer);
    }

    public async Task AdoptAsync(Guid customerId, CancellationToken ct = default)
    {
        var now = DateTimeOffset.UtcNow;
        var login = await db.Set<CustomerLogin>().FirstOrDefaultAsync(l => l.CustomerId == customerId, ct);
        var detail = login is null ? "no sign-in before" : login.EmailVerifiedUtc is null ? "unverified password wiped" : "already verified";
        if (login is null)
        {
            db.Add(NewPlaceholderLogin(customerId, now, verified: true));
        }
        else if (login.EmailVerifiedUtc is null)
        {
            // Whoever registered this address first never proved it, so their password is not trusted: replace it and end every session it opened.
            login.PasswordHash = Hasher.HashPassword(Dummy, Convert.ToBase64String(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32)));
            login.PasswordChangedUtc = now;
            login.SessionVersion++;
            login.EmailVerifiedUtc = now;
        }
        await db.SaveChangesAsync(ct);
        await RecordAsync(customerId, "social-adopted", null, detail, ct);
    }

    public async Task<SignInResult> SignInAsync(Guid customerId, string? code, CancellationToken ct = default)
    {
        var row = await (from l in db.Set<CustomerLogin>().AsNoTracking()
                         join c in db.Set<Customer>().AsNoTracking() on l.CustomerId equals c.Id
                         where l.CustomerId == customerId
                         select new { c.Email, c.Active, l.LockoutEnd, l.SessionVersion, l.EmailVerifiedUtc }).FirstOrDefaultAsync(ct);
        if (row is null || !row.Active) return new(SignInStatus.Invalid, null, null);
        if (row.LockoutEnd is { } until && until > DateTimeOffset.UtcNow) return new(SignInStatus.LockedOut, null, null);
        if (options.Value.RequireVerifiedEmail && row.EmailVerifiedUtc is null) return new(SignInStatus.Unverified, null, null);
        return await SecondStepAsync(new SignInResult(SignInStatus.Valid, customerId, row.Email, row.SessionVersion), code, ct);
    }

    public async Task RecordAsync(Guid customerId, string action, string? detail, CancellationToken ct = default)
        => await RecordAsync(customerId, action, actor: null, detail, ct);

    private CustomerLogin NewPlaceholderLogin(Guid customerId, DateTimeOffset now, bool verified) => new()
    {
        CustomerId = customerId,
        PasswordHash = Hasher.HashPassword(Dummy, Convert.ToBase64String(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32))), // nobody knows it
        CreatedUtc = now,
        PasswordChangedUtc = now,
        EmailVerifiedUtc = verified ? now : null,
    };
}
