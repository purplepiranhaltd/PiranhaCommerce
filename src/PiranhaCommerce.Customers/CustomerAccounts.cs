using System.ComponentModel.DataAnnotations;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using PiranhaCommerce.Core.Email;
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

    /// <summary>
    /// Carried in every token this sign-in issues. A token is valid only while it equals this number, and ending sessions adds one,
    /// so it is a counter rather than a clock: a token issued a moment after a revoke is not caught by it, and one issued a moment before is.
    /// Never shown to anyone.
    /// </summary>
    public int SessionVersion { get; set; }

    /// <summary>When the owner of the email address proved it (followed an emailed link, or set a password from one). Null until then.</summary>
    public DateTimeOffset? EmailVerifiedUtc { get; set; }
}

/// <summary>Bound to <c>PiranhaCommerce:Customers</c>.</summary>
public sealed class CustomerAccountOptions
{
    public const string SectionName = "PiranhaCommerce:Customers";

    public int MaxFailedAttempts { get; set; } = 5;
    public int LockoutMinutes { get; set; } = 15;
    public int MinPasswordLength { get; set; } = 10;

    /// <summary>How long a password-reset link works.</summary>
    public int ResetTokenMinutes { get; set; } = 60;

    /// <summary>Most reset emails sent to one customer in an hour, so the form cannot be used to flood someone's inbox.</summary>
    public int MaxResetEmailsPerHour { get; set; } = 5;

    /// <summary>
    /// Link put in the email, for example <c>https://shop.example.com/reset?email={email}&amp;token={token}</c>. The storefront serves
    /// that page and posts the values to <c>/auth/reset-password</c>. When empty the email carries the token itself.
    /// </summary>
    public string? ResetUrlTemplate { get; set; }

    /// <summary>
    /// When true a shopper who registers must follow an emailed link before they can sign in (and registering returns no token). Off by default.
    /// Existing sign-ins count as verified when this feature was added, so turning it on locks nobody out. Resetting a password with an emailed
    /// link also verifies the address, since receiving it is the proof.
    /// </summary>
    public bool RequireVerifiedEmail { get; set; }

    /// <summary>How long a verification link works.</summary>
    public int VerifyTokenMinutes { get; set; } = 1440;

    /// <summary>Like <see cref="ResetUrlTemplate"/>: the page the verification email links to, with <c>{token}</c> and <c>{email}</c>. It posts them to <c>/auth/verify-email</c>.</summary>
    public string? VerifyUrlTemplate { get; set; }

    /// <summary>
    /// How long a shopper's refresh token lasts after each use (so someone who keeps using the shop stays signed in). 0 switches refresh tokens off:
    /// shoppers then sign in again when the short access token ends.
    /// </summary>
    [System.ComponentModel.DataAnnotations.Range(0, 3650)]
    public int RefreshTokenDays { get; set; } = 30;

    /// <summary>The longest a sign-in on one device can be kept going by refreshing, however often it is used.</summary>
    [System.ComponentModel.DataAnnotations.Range(1, 3650)]
    public int RefreshTokenMaxDays { get; set; } = 180;
}

public sealed class VerifyEmailInput
{
    [Required, StringLength(256)] public string Email { get; set; } = "";
    [Required, StringLength(200)] public string Token { get; set; } = ""; // Unsigned codes are single use and expire
}

/// <summary>Registering when a verified email is required: no token yet.</summary>
public sealed record VerificationRequiredDto(ProfileDto Profile, bool VerificationRequired);

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

public sealed class ForgotPasswordInput
{
    [Required, StringLength(256)] public string Email { get; set; } = "";
}

public sealed class ResetPasswordInput
{
    [Required, StringLength(256)] public string Email { get; set; } = "";
    [Required, StringLength(200)] public string Token { get; set; } = "";
    [Required, StringLength(128)] public string NewPassword { get; set; } = "";
}

/// <summary>A single-use emailed token. Only a hash is stored, so a copy of the database cannot reset anyone's password.</summary>
public class CustomerToken
{
    public Guid Id { get; set; }
    public Guid CustomerId { get; set; }
    public string Purpose { get; set; } = "PasswordReset";
    public string TokenHash { get; set; } = "";
    public DateTimeOffset CreatedUtc { get; set; }
    public DateTimeOffset ExpiresUtc { get; set; }
    public DateTimeOffset? UsedUtc { get; set; }
}

/// <summary>Unverified is only ever returned for a CORRECT password, so it tells nobody that an address has an account.</summary>
public enum SignInStatus { Valid, Invalid, LockedOut, Unverified }

public sealed record SignInResult(SignInStatus Status, Guid? CustomerId, string? Email, int SessionVersion = 0);

/// <summary>The result of a successful password change: the version a fresh token for the device that changed it must carry.</summary>
public sealed record PasswordChangeResult(string Email, int SessionVersion);

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

    /// <summary>Null when the current password is wrong (counted like a failed sign-in) or the customer cannot sign in. Success ends every other session: the caller gets a fresh token for this one.</summary>
    Task<PasswordChangeResult?> ChangePasswordAsync(Guid customerId, ChangePasswordInput input, CancellationToken ct = default);

    /// <summary>True while the token's version is the current one, the customer is active and the sign-in exists. Checked on every storefront call.</summary>
    Task<bool> IsSessionCurrentAsync(Guid customerId, int sessionVersion, CancellationToken ct = default);

    /// <summary>
    /// Emails a single-use link to the address if (and only if) an active customer has it. Says nothing either way, so it cannot be
    /// used to find out who is a customer. The same link also CREATES a sign-in for a customer who has none (made by staff or by a
    /// guest order): receiving the email is the proof that the address is theirs.
    /// </summary>
    Task RequestPasswordResetAsync(ForgotPasswordInput input, CancellationToken ct = default);

    /// <summary>False when the link is wrong, used, expired or for another address. Success also clears any lockout.</summary>
    Task<bool> ResetPasswordAsync(ResetPasswordInput input, CancellationToken ct = default);

    /// <summary>False when the link is wrong, used, expired or for another address. Success verifies the address.</summary>
    Task<bool> VerifyEmailAsync(VerifyEmailInput input, CancellationToken ct = default);

    /// <summary>Emails a new verification link if (and only if) an active customer with that address has a sign-in that is not verified. Says nothing either way.</summary>
    Task RequestVerificationAsync(ForgotPasswordInput input, CancellationToken ct = default);
}

internal sealed partial class CustomerAccountService(
    PiranhaCommerceDbContext db, IOptions<CustomerAccountOptions> options, IEmailSender email, ILogger<CustomerAccountService> logger) : ICustomerAccountService
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
        if (options.Value.RequireVerifiedEmail) await SendTokenEmailAsync(customer.Id, customer.Email, VerifyPurpose, ct); // best effort: they can ask again
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
            select new { l.CustomerId, c.Email, c.Active, l.PasswordHash, l.LockoutEnd, l.SessionVersion, l.EmailVerifiedUtc }).FirstOrDefaultAsync(ct);

        if (row is null || !row.Active)
        {
            Hasher.VerifyHashedPassword(Dummy, DummyHash, password);
            return new(SignInStatus.Invalid, null, null);
        }

        if (row.LockoutEnd is { } until && until > DateTimeOffset.UtcNow)
            return new(SignInStatus.LockedOut, null, null);

        if (Hasher.VerifyHashedPassword(Dummy, row.PasswordHash, password) == PasswordVerificationResult.Failed)
            return new(await RecordFailureAsync(row.CustomerId, ct) ? SignInStatus.LockedOut : SignInStatus.Invalid, null, null);

        // Only now, with the right password, is it safe to say why they cannot come in.
        if (options.Value.RequireVerifiedEmail && row.EmailVerifiedUtc is null) return new(SignInStatus.Unverified, null, null);

        // Granted only if the account is STILL unlocked at this instant (one atomic statement), as for staff sign-in.
        var now = DateTimeOffset.UtcNow;
        var granted = await db.Set<CustomerLogin>().Where(l => l.CustomerId == row.CustomerId && (l.LockoutEnd == null || l.LockoutEnd <= now))
            .ExecuteUpdateAsync(s => s.SetProperty(l => l.AccessFailedCount, 0), ct);
        return granted > 0 ? new(SignInStatus.Valid, row.CustomerId, row.Email, row.SessionVersion) : new(SignInStatus.LockedOut, null, null);
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

    public async Task<bool> IsSessionCurrentAsync(Guid customerId, int sessionVersion, CancellationToken ct = default)
    {
        var needVerified = options.Value.RequireVerifiedEmail; // a token from before the rule, or from before the address changed, stops working too
        return await (from l in db.Set<CustomerLogin>().AsNoTracking()
                      join c in db.Set<Customer>().AsNoTracking() on l.CustomerId equals c.Id
                      where l.CustomerId == customerId && c.Active && l.SessionVersion == sessionVersion && (!needVerified || l.EmailVerifiedUtc != null)
                      select l.CustomerId).AnyAsync(ct);
    }

    public async Task<PasswordChangeResult?> ChangePasswordAsync(Guid customerId, ChangePasswordInput input, CancellationToken ct = default)
    {
        ModelValidator.ValidateOrThrow(input);
        var email = await db.Set<Customer>().Where(c => c.Id == customerId && c.Active).Select(c => c.Email).FirstOrDefaultAsync(ct);
        if (email is null) return null;

        // The current password is checked (and failures counted, and locked) exactly like a sign-in, so a stolen token cannot
        // be used to guess it, and it must belong to the same customer.
        var check = await CheckAsync(email, input.CurrentPassword, ct);
        if (check.Status != SignInStatus.Valid || check.CustomerId != customerId) return null;

        CheckPasswordRules(input.NewPassword, email, nameof(ChangePasswordInput.NewPassword));
        var now = DateTimeOffset.UtcNow;
        var hash = Hasher.HashPassword(Dummy, input.NewPassword);
        // Every session ends, this one included: the caller is handed a new token. Read after the update, so a revoke that lands in between
        // only makes that token die too (it fails safe).
        var changed = await db.Set<CustomerLogin>().Where(l => l.CustomerId == customerId)
            .ExecuteUpdateAsync(s => s.SetProperty(l => l.PasswordHash, hash).SetProperty(l => l.PasswordChangedUtc, now)
                .SetProperty(l => l.SessionVersion, l => l.SessionVersion + 1), ct);
        if (changed == 0) return null;
        var version = await db.Set<CustomerLogin>().AsNoTracking().Where(l => l.CustomerId == customerId).Select(l => l.SessionVersion).FirstAsync(ct);
        return new PasswordChangeResult(email, version);
    }

    public async Task RequestPasswordResetAsync(ForgotPasswordInput input, CancellationToken ct = default)
    {
        ModelValidator.ValidateOrThrow(input);
        var normalized = Normalize(input.Email);
        var customer = await db.Set<Customer>().AsNoTracking().Where(c => c.NormalizedEmail == normalized && c.Active)
            .Select(c => new { c.Id, c.Email }).FirstOrDefaultAsync(ct);
        if (customer is null) return;
        await SendResetLinkCoreAsync(customer.Id, customer.Email, ct); // the outcome is deliberately not shown to whoever asked
    }

    private const string ResetPurpose = "PasswordReset";
    private const string VerifyPurpose = "VerifyEmail";

    private Task<ResetLinkOutcome> SendResetLinkCoreAsync(Guid customerId, string customerEmail, CancellationToken ct)
        => SendTokenEmailAsync(customerId, customerEmail, ResetPurpose, ct);

    /// <summary>Makes a single-use link and emails it: to choose a password (reset or invitation) or to verify the address. Each has its own hourly limit.</summary>
    private async Task<ResetLinkOutcome> SendTokenEmailAsync(Guid customerId, string customerEmail, string purpose, CancellationToken ct)
    {
        var customer = new { Id = customerId, Email = customerEmail };
        var o = options.Value;
        var verify = purpose == VerifyPurpose;
        var minutes = verify ? o.VerifyTokenMinutes : o.ResetTokenMinutes;
        var urlTemplate = verify ? o.VerifyUrlTemplate : o.ResetUrlTemplate;
        var now = DateTimeOffset.UtcNow;
        var hourAgo = now.AddHours(-1);
        if (await db.Set<CustomerToken>().CountAsync(t => t.CustomerId == customer.Id && t.Purpose == purpose && t.CreatedUtc > hourAgo, ct) >= o.MaxResetEmailsPerHour) return ResetLinkOutcome.RateLimited;

        var secret = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        var token = new CustomerToken { Id = Guid.NewGuid(), CustomerId = customer.Id, Purpose = purpose, TokenHash = Hash(secret), CreatedUtc = now, ExpiresUtc = now.AddMinutes(minutes) };
        db.Add(token);
        await db.SaveChangesAsync(ct);

        var code = $"{token.Id:N}.{secret}";
        var link = string.IsNullOrWhiteSpace(urlTemplate) ? null
            : urlTemplate.Replace("{token}", Uri.EscapeDataString(code)).Replace("{email}", Uri.EscapeDataString(customer.Email));
        var message = new EmailMessage(customer.Email, verify ? CustomerEmailTemplates.VerifyEmail : CustomerEmailTemplates.SetPassword, new Dictionary<string, object?>
        {
            ["email"] = customer.Email, ["link"] = link, ["code"] = code, ["expiresInMinutes"] = minutes,
        });
        try { await email.SendAsync(message, ct); return ResetLinkOutcome.Sent; }
        catch (Exception ex) { logger.LogWarning(ex, "A {Purpose} email could not be sent.", purpose); return ResetLinkOutcome.SendFailed; }
    }

    public async Task<bool> ResetPasswordAsync(ResetPasswordInput input, CancellationToken ct = default)
    {
        ModelValidator.ValidateOrThrow(input);
        var parts = input.Token.Split('.', 2);
        if (parts.Length != 2 || !Guid.TryParseExact(parts[0], "N", out var tokenId)) return false;

        var normalized = Normalize(input.Email);
        var customer = await db.Set<Customer>().AsNoTracking().Where(c => c.NormalizedEmail == normalized && c.Active)
            .Select(c => new { c.Id, c.Email }).FirstOrDefaultAsync(ct);
        var row = await db.Set<CustomerToken>().AsNoTracking().Where(t => t.Id == tokenId)
            .Select(t => new { t.CustomerId, t.TokenHash, t.UsedUtc, t.ExpiresUtc, t.Purpose }).FirstOrDefaultAsync(ct);
        var now = DateTimeOffset.UtcNow;
        if (customer is null || row is null || row.Purpose != ResetPurpose || row.CustomerId != customer.Id || row.UsedUtc is not null || row.ExpiresUtc <= now) return false;
        if (!CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(row.TokenHash), Encoding.ASCII.GetBytes(Hash(parts[1])))) return false;

        // A weak password is refused BEFORE the link is spent, so the shopper can try again with the same link.
        CheckPasswordRules(input.NewPassword, customer.Email, nameof(ResetPasswordInput.NewPassword));

        // Spend the link: one atomic statement, so two simultaneous uses cannot both win.
        var spent = await db.Set<CustomerToken>().Where(t => t.Id == tokenId && t.UsedUtc == null && t.ExpiresUtc > now)
            .ExecuteUpdateAsync(s => s.SetProperty(t => t.UsedUtc, (DateTimeOffset?)now), ct);
        if (spent == 0) return false;
        await db.Set<CustomerToken>().Where(t => t.CustomerId == customer.Id && t.UsedUtc == null)
            .ExecuteUpdateAsync(s => s.SetProperty(t => t.UsedUtc, (DateTimeOffset?)now), ct); // any other outstanding links die with it

        var hash = Hasher.HashPassword(Dummy, input.NewPassword);
        var updated = await db.Set<CustomerLogin>().Where(l => l.CustomerId == customer.Id)
            .ExecuteUpdateAsync(s => s.SetProperty(l => l.PasswordHash, hash).SetProperty(l => l.PasswordChangedUtc, now)
                .SetProperty(l => l.AccessFailedCount, 0).SetProperty(l => l.LockoutEnd, (DateTimeOffset?)null)
                .SetProperty(l => l.SessionVersion, l => l.SessionVersion + 1)
                .SetProperty(l => l.EmailVerifiedUtc, l => l.EmailVerifiedUtc ?? (DateTimeOffset?)now), ct); // whoever held the old password is signed out; the link proved the address
        if (updated == 0)
        {
            // No sign-in yet: this is how a customer made by staff or a guest order gets one, having proved the address.
            db.Add(new CustomerLogin { CustomerId = customer.Id, PasswordHash = hash, CreatedUtc = now, PasswordChangedUtc = now, EmailVerifiedUtc = now });
            try { await db.SaveChangesAsync(ct); }
            catch (DbUpdateException)
            {
                db.ChangeTracker.Clear(); // another reset created it a moment ago: ours replaces its password
                await db.Set<CustomerLogin>().Where(l => l.CustomerId == customer.Id)
                    .ExecuteUpdateAsync(s => s.SetProperty(l => l.PasswordHash, hash).SetProperty(l => l.PasswordChangedUtc, now)
                        .SetProperty(l => l.SessionVersion, l => l.SessionVersion + 1)
                        .SetProperty(l => l.EmailVerifiedUtc, l => l.EmailVerifiedUtc ?? (DateTimeOffset?)now), ct);
            }
        }
        return true;
    }

    public async Task<bool> VerifyEmailAsync(VerifyEmailInput input, CancellationToken ct = default)
    {
        ModelValidator.ValidateOrThrow(input);
        var parts = input.Token.Split('.', 2);
        if (parts.Length != 2 || !Guid.TryParseExact(parts[0], "N", out var tokenId)) return false;

        var normalized = Normalize(input.Email);
        var customerId = await db.Set<Customer>().AsNoTracking().Where(c => c.NormalizedEmail == normalized && c.Active).Select(c => (Guid?)c.Id).FirstOrDefaultAsync(ct);
        var row = await db.Set<CustomerToken>().AsNoTracking().Where(t => t.Id == tokenId)
            .Select(t => new { t.CustomerId, t.TokenHash, t.UsedUtc, t.ExpiresUtc, t.Purpose }).FirstOrDefaultAsync(ct);
        var now = DateTimeOffset.UtcNow;
        if (customerId is null || row is null || row.Purpose != VerifyPurpose || row.CustomerId != customerId || row.UsedUtc is not null || row.ExpiresUtc <= now) return false;
        if (!CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(row.TokenHash), Encoding.ASCII.GetBytes(Hash(parts[1])))) return false;

        var spent = await db.Set<CustomerToken>().Where(t => t.Id == tokenId && t.UsedUtc == null && t.ExpiresUtc > now)
            .ExecuteUpdateAsync(s => s.SetProperty(t => t.UsedUtc, (DateTimeOffset?)now), ct);
        if (spent == 0) return false;
        return await db.Set<CustomerLogin>().Where(l => l.CustomerId == customerId)
            .ExecuteUpdateAsync(s => s.SetProperty(l => l.EmailVerifiedUtc, l => l.EmailVerifiedUtc ?? (DateTimeOffset?)now), ct) > 0;
    }

    public async Task RequestVerificationAsync(ForgotPasswordInput input, CancellationToken ct = default)
    {
        ModelValidator.ValidateOrThrow(input);
        var normalized = Normalize(input.Email);
        var customer = await (from c in db.Set<Customer>().AsNoTracking()
                              join l in db.Set<CustomerLogin>().AsNoTracking() on c.Id equals l.CustomerId
                              where c.NormalizedEmail == normalized && c.Active && l.EmailVerifiedUtc == null
                              select new { c.Id, c.Email }).FirstOrDefaultAsync(ct);
        if (customer is null) return;
        await SendTokenEmailAsync(customer.Id, customer.Email, VerifyPurpose, ct); // the outcome is not shown to whoever asked
    }

    private static string Hash(string secret) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(secret)));

    private void CheckPasswordRules(string password, string email, string field)
    {
        var min = options.Value.MinPasswordLength;
        if (password.Length < min)
            throw new PiranhaCommerceValidationException(field, $"Use at least {min} characters.");
        if (string.Equals(password.Trim(), email.Trim(), StringComparison.OrdinalIgnoreCase))
            throw new PiranhaCommerceValidationException(field, "The password cannot be your email address.");
    }

    private static PiranhaCommerceValidationException Taken()
        => new(nameof(RegisterInput.Email), "An account cannot be created with that email address. If you have an account or have ordered before, use forgot password to set a password.");

    private static string Normalize(string email) => email.Trim().ToUpperInvariant();
    private static string? Clean(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();
    private static ProfileDto ToProfile(Customer c) => new(c.Id, c.Email, c.FirstName, c.LastName, c.Phone);
}
