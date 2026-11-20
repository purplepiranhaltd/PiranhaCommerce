using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using PiranhaCommerce.Core.Validation;
using PiranhaCommerce.Data;

namespace PiranhaCommerce.Customers;

/// <summary>
/// A shopper's sign-in. Kept apart from admin users on purpose: a customer can never be, or become, a staff user, and holds no
/// roles. One row per customer who has an account; customers made by staff or by a guest order have none.
/// </summary>
public class CustomerLogin
{
    public Guid CustomerId { get; set; }
    public string PasswordHash { get; set; } = "";
    public int AccessFailedCount { get; set; }
    public DateTimeOffset? LockoutEnd { get; set; }
    public DateTimeOffset CreatedUtc { get; set; }
    public DateTimeOffset PasswordChangedUtc { get; set; }
}

/// <summary>Bound to <c>Piranha:Customers</c>.</summary>
public sealed class CustomerAccountOptions
{
    public const string SectionName = "Piranha:Customers";

    public int MaxFailedAttempts { get; set; } = 5;
    public int LockoutMinutes { get; set; } = 15;
    public int MinPasswordLength { get; set; } = 10;
}

public sealed class RegisterInput
{
    [Required, EmailAddress, StringLength(256)] public string Email { get; set; } = "";
    [Required, StringLength(128)] public string Password { get; set; } = "";
    [StringLength(100)] public string? FirstName { get; set; }
    [StringLength(100)] public string? LastName { get; set; }
}

public sealed record SignInInput(string Email, string Password);

public sealed class ChangePasswordInput
{
    [Required, StringLength(128)] public string CurrentPassword { get; set; } = "";
    [Required, StringLength(128)] public string NewPassword { get; set; } = "";
}

/// <summary>What a shopper may see of their own record: never the staff-only notes.</summary>
public sealed record ProfileDto(Guid Id, string Email, string? FirstName, string? LastName, string? Phone);

public sealed class ProfileInput
{
    [StringLength(100)] public string? FirstName { get; set; }
    [StringLength(100)] public string? LastName { get; set; }
    [StringLength(40)] public string? Phone { get; set; }
}

public enum SignInStatus { Valid, Invalid, LockedOut }

public sealed record SignInResult(SignInStatus Status, Guid? CustomerId, string? Email);

public interface ICustomerAccountService
{
    /// <summary>
    /// Creates a customer with a sign-in. Refused when the email already belongs to ANY customer (including one made by staff or by
    /// a guest order): taking over such a record would expose its order history to whoever typed the address. Linking those needs
    /// proof of email ownership, which does not exist yet.
    /// </summary>
    Task<ProfileDto> RegisterAsync(RegisterInput input, CancellationToken ct = default);

    /// <summary>One check for wrong passwords, unknown emails, customers without an account and inactive customers: all Invalid.</summary>
    Task<SignInResult> CheckAsync(string email, string password, CancellationToken ct = default);

    /// <summary>Null when the customer is missing or inactive (a token outlives deactivation, this does not).</summary>
    Task<ProfileDto?> GetProfileAsync(Guid customerId, CancellationToken ct = default);
    Task<ProfileDto?> UpdateProfileAsync(Guid customerId, ProfileInput input, CancellationToken ct = default);

    /// <summary>False when the current password is wrong (counted like a failed sign-in) or the customer cannot sign in.</summary>
    Task<bool> ChangePasswordAsync(Guid customerId, ChangePasswordInput input, CancellationToken ct = default);
}

internal sealed class CustomerAccountService(PiranhaDbContext db, IOptions<CustomerAccountOptions> options) : ICustomerAccountService
{
    private static readonly PasswordHasher<CustomerLogin> Hasher = new();
    private static readonly CustomerLogin Dummy = new();
    // Burned when no account matches so that "no such account" costs the same as "wrong password".
    private static readonly string DummyHash = Hasher.HashPassword(Dummy, "dummy-password-never-matches");

    public async Task<ProfileDto> RegisterAsync(RegisterInput input, CancellationToken ct = default)
    {
        ModelValidator.ValidateOrThrow(input);
        CheckPasswordRules(input.Password, input.Email, nameof(RegisterInput.Password));

        var normalized = Normalize(input.Email);
        if (await db.Set<Customer>().AnyAsync(c => c.NormalizedEmail == normalized, ct)) throw Taken();

        var customer = new Customer
        {
            Email = input.Email.Trim(),
            NormalizedEmail = normalized,
            FirstName = Clean(input.FirstName),
            LastName = Clean(input.LastName),
        };
        var now = DateTimeOffset.UtcNow;
        var login = new CustomerLogin { CustomerId = customer.Id, PasswordHash = Hasher.HashPassword(Dummy, input.Password), CreatedUtc = now, PasswordChangedUtc = now };
        db.Add(customer);
        db.Add(login);
        try
        {
            await db.SaveChangesAsync(ct); // one transaction: a customer never exists half-registered
        }
        catch (DbUpdateException)
        {
            db.ChangeTracker.Clear();
            throw Taken(); // lost a race on the unique email index
        }
        return ToProfile(customer);
    }

    public async Task<SignInResult> CheckAsync(string email, string password, CancellationToken ct = default)
    {
        password ??= "";
        var normalized = string.IsNullOrWhiteSpace(email) ? null : Normalize(email);
        var row = normalized is null ? null : await (
            from l in db.Set<CustomerLogin>()
            join c in db.Set<Customer>() on l.CustomerId equals c.Id
            where c.NormalizedEmail == normalized
            select new { l.CustomerId, c.Email, c.Active, l.PasswordHash, l.LockoutEnd }).FirstOrDefaultAsync(ct);

        if (row is null || !row.Active)
        {
            Hasher.VerifyHashedPassword(Dummy, DummyHash, password);
            return new(SignInStatus.Invalid, null, null);
        }

        if (row.LockoutEnd is { } until && until > DateTimeOffset.UtcNow)
            return new(SignInStatus.LockedOut, null, null);

        if (Hasher.VerifyHashedPassword(Dummy, row.PasswordHash, password) == PasswordVerificationResult.Failed)
            return new(await RecordFailureAsync(row.CustomerId, ct) ? SignInStatus.LockedOut : SignInStatus.Invalid, null, null);

        // Granted only if the account is STILL unlocked at this instant (one atomic statement), as for staff sign-in.
        var now = DateTimeOffset.UtcNow;
        var granted = await db.Set<CustomerLogin>().Where(l => l.CustomerId == row.CustomerId && (l.LockoutEnd == null || l.LockoutEnd <= now))
            .ExecuteUpdateAsync(s => s.SetProperty(l => l.AccessFailedCount, 0), ct);
        return granted > 0 ? new(SignInStatus.Valid, row.CustomerId, row.Email) : new(SignInStatus.LockedOut, null, null);
    }

    private async Task<bool> RecordFailureAsync(Guid customerId, CancellationToken ct)
    {
        var o = options.Value;
        await db.Set<CustomerLogin>().Where(l => l.CustomerId == customerId)
            .ExecuteUpdateAsync(s => s.SetProperty(l => l.AccessFailedCount, l => l.AccessFailedCount + 1), ct);

        var max = o.MaxFailedAttempts;
        var lockedUntil = DateTimeOffset.UtcNow.AddMinutes(o.LockoutMinutes);
        var locked = await db.Set<CustomerLogin>().Where(l => l.CustomerId == customerId && l.AccessFailedCount >= max)
            .ExecuteUpdateAsync(s => s.SetProperty(l => l.AccessFailedCount, 0).SetProperty(l => l.LockoutEnd, (DateTimeOffset?)lockedUntil), ct);
        return locked > 0;
    }

    public async Task<ProfileDto?> GetProfileAsync(Guid customerId, CancellationToken ct = default)
        => await db.Set<Customer>().AsNoTracking().Where(c => c.Id == customerId && c.Active)
            .Select(c => new ProfileDto(c.Id, c.Email, c.FirstName, c.LastName, c.Phone)).FirstOrDefaultAsync(ct);

    public async Task<ProfileDto?> UpdateProfileAsync(Guid customerId, ProfileInput input, CancellationToken ct = default)
    {
        ModelValidator.ValidateOrThrow(input);
        var customer = await db.Set<Customer>().FirstOrDefaultAsync(c => c.Id == customerId && c.Active, ct);
        if (customer is null) return null;
        customer.FirstName = Clean(input.FirstName);
        customer.LastName = Clean(input.LastName);
        customer.Phone = Clean(input.Phone);
        await db.SaveChangesAsync(ct);
        return ToProfile(customer);
    }

    public async Task<bool> ChangePasswordAsync(Guid customerId, ChangePasswordInput input, CancellationToken ct = default)
    {
        ModelValidator.ValidateOrThrow(input);
        var email = await db.Set<Customer>().Where(c => c.Id == customerId && c.Active).Select(c => c.Email).FirstOrDefaultAsync(ct);
        if (email is null) return false;

        // The current password is checked (and failures counted, and locked) exactly like a sign-in, so a stolen token cannot
        // be used to guess it, and it must belong to the same customer.
        var check = await CheckAsync(email, input.CurrentPassword, ct);
        if (check.Status != SignInStatus.Valid || check.CustomerId != customerId) return false;

        CheckPasswordRules(input.NewPassword, email, nameof(ChangePasswordInput.NewPassword));
        var now = DateTimeOffset.UtcNow;
        var hash = Hasher.HashPassword(Dummy, input.NewPassword);
        return await db.Set<CustomerLogin>().Where(l => l.CustomerId == customerId)
            .ExecuteUpdateAsync(s => s.SetProperty(l => l.PasswordHash, hash).SetProperty(l => l.PasswordChangedUtc, now), ct) > 0;
    }

    private void CheckPasswordRules(string password, string email, string field)
    {
        var min = options.Value.MinPasswordLength;
        if (password.Length < min)
            throw new PiranhaValidationException(field, $"Use at least {min} characters.");
        if (string.Equals(password.Trim(), email.Trim(), StringComparison.OrdinalIgnoreCase))
            throw new PiranhaValidationException(field, "The password cannot be your email address.");
    }

    private static PiranhaValidationException Taken()
        => new(nameof(RegisterInput.Email), "An account cannot be created with that email address.");

    private static string Normalize(string email) => email.Trim().ToUpperInvariant();
    private static string? Clean(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();
    private static ProfileDto ToProfile(Customer c) => new(c.Id, c.Email, c.FirstName, c.LastName, c.Phone);
}
