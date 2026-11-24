using Microsoft.EntityFrameworkCore;
using PiranhaCommerce.Core.Paging;
using PiranhaCommerce.Core.Validation;
using PiranhaCommerce.Data;
using PiranhaCommerce.Core.Identity;

namespace PiranhaCommerce.Customers;

/// <summary>Single place for customer rules, shared by the API and the admin UI.</summary>
public interface ICustomerService
{
    Task<PagedResult<CustomerDto>> ListAsync(int? page, int? pageSize, string? search, CancellationToken ct = default);
    Task<CustomerDto?> GetAsync(Guid id, CancellationToken ct = default);

    /// <summary>A duplicate email (any letter case) is a validation error on <c>Email</c>.</summary>
    [RequiresPermission(Permissions.CustomersManage)]
    Task<CustomerDto> CreateAsync(CustomerInput input, CancellationToken ct = default);
    /// <summary>Switching the customer off or on is recorded in their sign-in history against <paramref name="actor"/>.</summary>
    [RequiresPermission(Permissions.CustomersManage)]
    Task<CustomerDto?> UpdateAsync(Guid id, CustomerInput input, string? actor = null, CancellationToken ct = default);

    /// <summary>Deletes the customer and their address book. False when not found.</summary>
    [RequiresPermission(Permissions.CustomersManage)]
    Task<bool> DeleteAsync(Guid id, CancellationToken ct = default);

    /// <summary>Null when the customer does not exist.</summary>
    Task<IReadOnlyList<AddressDto>?> ListAddressesAsync(Guid customerId, CancellationToken ct = default);

    /// <summary>Null when the customer does not exist.</summary>
    [RequiresPermission(Permissions.CustomersManage)]
    Task<AddressDto?> AddAddressAsync(Guid customerId, AddressInput input, CancellationToken ct = default);

    /// <summary>Null when the customer or the address (of that customer) does not exist.</summary>
    [RequiresPermission(Permissions.CustomersManage)]
    Task<AddressDto?> UpdateAddressAsync(Guid customerId, Guid addressId, AddressInput input, CancellationToken ct = default);

    [RequiresPermission(Permissions.CustomersManage)]
    Task<bool> DeleteAddressAsync(Guid customerId, Guid addressId, CancellationToken ct = default);
}

internal sealed class CustomerService(PiranhaCommerceDbContext db) : ICustomerService
{
    public const int MaxAddresses = 50;

    public async Task<PagedResult<CustomerDto>> ListAsync(int? page, int? pageSize, string? search, CancellationToken ct = default)
    {
        var (p, size) = Paging.Normalize(page, pageSize);
        var query = db.Set<Customer>().AsNoTracking();
        if (!string.IsNullOrWhiteSpace(search))
        {
            var s = search.Trim().ToLower(); // ToLower on both sides: case-insensitive on every provider
            query = query.Where(c => c.Email.ToLower().Contains(s)
                || (c.FirstName != null && c.FirstName.ToLower().Contains(s))
                || (c.LastName != null && c.LastName.ToLower().Contains(s))
                || (c.Phone != null && c.Phone.Contains(s)));
        }

        var total = await query.CountAsync(ct);
        var items = await query.OrderBy(c => c.NormalizedEmail).ThenBy(c => c.Id) // stable order for paging
            .Skip((p - 1) * size).Take(size)
            .Select(c => new CustomerDto(c.Id, c.Email, c.FirstName, c.LastName, c.Phone, c.Notes, c.Active, c.CreatedUtc, c.UpdatedUtc))
            .ToListAsync(ct);
        return new PagedResult<CustomerDto>(items, p, size, total);
    }

    public async Task<CustomerDto?> GetAsync(Guid id, CancellationToken ct = default)
        => await db.Set<Customer>().AsNoTracking().Where(c => c.Id == id)
            .Select(c => new CustomerDto(c.Id, c.Email, c.FirstName, c.LastName, c.Phone, c.Notes, c.Active, c.CreatedUtc, c.UpdatedUtc))
            .FirstOrDefaultAsync(ct);

    public async Task<CustomerDto> CreateAsync(CustomerInput input, CancellationToken ct = default)
    {
        ModelValidator.ValidateOrThrow(input);
        await EnsureEmailFreeAsync(input.Email, null, ct);
        var entity = new Customer { Email = input.Email.Trim(), NormalizedEmail = Normalize(input.Email) };
        Apply(entity, input);
        db.Add(entity);
        await SaveUniqueAsync(ct);
        return ToDto(entity);
    }

    public async Task<CustomerDto?> UpdateAsync(Guid id, CustomerInput input, string? actor = null, CancellationToken ct = default)
    {
        var entity = await db.Set<Customer>().FirstOrDefaultAsync(c => c.Id == id, ct);
        if (entity is null) return null;
        ModelValidator.ValidateOrThrow(input);
        await EnsureEmailFreeAsync(input.Email, id, ct);
        var emailChanged = entity.NormalizedEmail != Normalize(input.Email);
        entity.Email = input.Email.Trim();
        entity.NormalizedEmail = Normalize(input.Email);
        var switchedOff = entity.Active && !input.Active;
        var switchedOn = !entity.Active && input.Active;
        Apply(entity, input);
        await SaveUniqueAsync(ct);
        // Switching a customer off ends their sessions for good: without this, switching them back on would bring an old token back to life.
        if (switchedOff)
            await db.Set<CustomerLogin>().Where(l => l.CustomerId == id).ExecuteUpdateAsync(s => s.SetProperty(l => l.SessionVersion, l => l.SessionVersion + 1), ct);
        // A new address is unproven: it must be verified again, and every session (which names the old address) ends.
        if (emailChanged)
        {
            await db.Set<CustomerLogin>().Where(l => l.CustomerId == id).ExecuteUpdateAsync(s => s
                .SetProperty(l => l.EmailVerifiedUtc, (DateTimeOffset?)null).SetProperty(l => l.SessionVersion, l => l.SessionVersion + 1), ct);
            db.Add(new CustomerSignInEvent { CustomerId = id, OccurredUtc = DateTimeOffset.UtcNow, Actor = actor, Action = "email-changed" });
            await db.SaveChangesAsync(ct);
        }
        if (switchedOff || switchedOn)
        {
            db.Add(new CustomerSignInEvent { CustomerId = id, OccurredUtc = DateTimeOffset.UtcNow, Actor = actor, Action = switchedOff ? "switched-off" : "switched-on" });
            await db.SaveChangesAsync(ct);
        }
        return ToDto(entity);
    }

    public async Task<bool> DeleteAsync(Guid id, CancellationToken ct = default)
        => await db.DeleteTrackedAsync<Customer>(c => c.Id == id, ct) > 0; // addresses cascade in the database

    public async Task<IReadOnlyList<AddressDto>?> ListAddressesAsync(Guid customerId, CancellationToken ct = default)
    {
        if (!await db.Set<Customer>().AnyAsync(c => c.Id == customerId, ct)) return null;
        return await db.Set<CustomerAddress>().AsNoTracking().Where(a => a.CustomerId == customerId)
            .OrderBy(a => a.Label).ThenBy(a => a.Line1).ThenBy(a => a.Id)
            .Select(a => new AddressDto(a.Id, a.CustomerId, a.Label, a.Recipient, a.Line1, a.Line2, a.City, a.Region, a.PostalCode, a.CountryCode))
            .ToListAsync(ct);
    }

    public async Task<AddressDto?> AddAddressAsync(Guid customerId, AddressInput input, CancellationToken ct = default)
    {
        ModelValidator.ValidateOrThrow(input);
        if (!await db.Set<Customer>().AnyAsync(c => c.Id == customerId, ct)) return null;
        if (await db.Set<CustomerAddress>().CountAsync(a => a.CustomerId == customerId, ct) >= MaxAddresses)
            throw new PiranhaCommerceValidationException("", $"A customer can have at most {MaxAddresses} addresses.");

        var entity = new CustomerAddress { CustomerId = customerId, Line1 = "", City = "", CountryCode = "" };
        Apply(entity, input);
        db.Add(entity);
        await db.SaveChangesAsync(ct);
        return ToDto(entity);
    }

    public async Task<AddressDto?> UpdateAddressAsync(Guid customerId, Guid addressId, AddressInput input, CancellationToken ct = default)
    {
        var entity = await db.Set<CustomerAddress>().FirstOrDefaultAsync(a => a.Id == addressId && a.CustomerId == customerId, ct);
        if (entity is null) return null;
        ModelValidator.ValidateOrThrow(input);
        Apply(entity, input);
        await db.SaveChangesAsync(ct);
        return ToDto(entity);
    }

    public async Task<bool> DeleteAddressAsync(Guid customerId, Guid addressId, CancellationToken ct = default)
        => await db.DeleteTrackedAsync<CustomerAddress>(a => a.Id == addressId && a.CustomerId == customerId, ct) > 0;

    private static string Normalize(string email) => email.Trim().ToUpperInvariant();

    private async Task EnsureEmailFreeAsync(string email, Guid? selfId, CancellationToken ct)
    {
        var normalized = Normalize(email);
        if (await db.Set<Customer>().AnyAsync(c => c.NormalizedEmail == normalized && c.Id != selfId, ct))
            throw new PiranhaCommerceValidationException(nameof(CustomerInput.Email), "A customer with that email already exists.");
    }

    /// <summary>The unique index is the real guarantee; this turns a lost race into the same friendly error.</summary>
    private async Task SaveUniqueAsync(CancellationToken ct)
    {
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException)
        {
            db.ChangeTracker.Clear();
            throw new PiranhaCommerceValidationException(nameof(CustomerInput.Email), "A customer with that email already exists.");
        }
    }

    private static void Apply(Customer e, CustomerInput i)
    {
        e.FirstName = Clean(i.FirstName);
        e.LastName = Clean(i.LastName);
        e.Phone = Clean(i.Phone);
        e.Notes = Clean(i.Notes);
        e.Active = i.Active;
    }

    private static void Apply(CustomerAddress e, AddressInput i)
    {
        e.Label = Clean(i.Label);
        e.Recipient = Clean(i.Recipient);
        e.Line1 = i.Line1.Trim();
        e.Line2 = Clean(i.Line2);
        e.City = i.City.Trim();
        e.Region = Clean(i.Region);
        e.PostalCode = Clean(i.PostalCode);
        e.CountryCode = i.CountryCode.Trim().ToUpperInvariant();
    }

    private static string? Clean(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();

    private static CustomerDto ToDto(Customer c) =>
        new(c.Id, c.Email, c.FirstName, c.LastName, c.Phone, c.Notes, c.Active, c.CreatedUtc, c.UpdatedUtc);

    private static AddressDto ToDto(CustomerAddress a) =>
        new(a.Id, a.CustomerId, a.Label, a.Recipient, a.Line1, a.Line2, a.City, a.Region, a.PostalCode, a.CountryCode);
}
