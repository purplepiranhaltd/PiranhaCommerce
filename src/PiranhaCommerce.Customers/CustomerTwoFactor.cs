using System.Buffers.Binary;
using System.ComponentModel.DataAnnotations;
using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using PiranhaCommerce.Core.Identity;
using PiranhaCommerce.Core.Validation;
using PiranhaCommerce.Data;

namespace PiranhaCommerce.Customers;

/// <summary>
/// A shopper's authenticator app. One row per customer: pending (<see cref="EnabledUtc"/> null) while they are setting it up, then on.
/// The key is stored as it is shown to the shopper, as authenticator apps need it back unchanged, so protect the database as you do
/// for the password hashes (a copy of the database holds the second factor too).
/// </summary>
public class CustomerTwoFactor
{
    public Guid CustomerId { get; set; }
    /// <summary>The shared key, as base32 text.</summary>
    public string SecretKey { get; set; } = "";
    public DateTimeOffset CreatedUtc { get; set; }
    /// <summary>When they proved the app works by typing a code. Null while setting up: sign-in does not ask for a code until then.</summary>
    public DateTimeOffset? EnabledUtc { get; set; }
    /// <summary>The newest 30-second step a code was accepted for. A code for this step or an older one is refused, so each code works once.</summary>
    public long LastUsedStep { get; set; }
}

/// <summary>A one-off code for a shopper who has lost their phone. Only a hash is kept.</summary>
public class CustomerRecoveryCode
{
    public Guid Id { get; set; }
    public Guid CustomerId { get; set; }
    public string CodeHash { get; set; } = "";
    public DateTimeOffset? UsedUtc { get; set; }
}

public sealed record TwoFactorStatusDto(bool Enabled, DateTimeOffset? EnabledUtc, int RecoveryCodesRemaining, bool Compulsory);

/// <param name="Secret">The key to type into an authenticator app, in groups of four.</param>
/// <param name="OtpAuthUri">The same key as an <c>otpauth://</c> address: draw it as a QR code for the app to scan.</param>
public sealed record TwoFactorSetupDto(string Secret, string OtpAuthUri);

/// <param name="RecoveryCodes">Shown once. Each works one time in place of an authenticator code.</param>
public sealed record TwoFactorEnabledDto(IReadOnlyList<string> RecoveryCodes);

public sealed class TwoFactorCodeInput
{
    [Required, StringLength(32)] public string Code { get; set; } = "";
}

public sealed class TwoFactorDisableInput
{
    [Required, StringLength(128)] public string Password { get; set; } = "";
    [Required, StringLength(32)] public string Code { get; set; } = "";
}

public sealed class TwoFactorSetupTokenInput
{
    [Required, StringLength(200)] public string SetupToken { get; set; } = "";
}

public sealed class TwoFactorEnableWithTokenInput
{
    [Required, StringLength(200)] public string SetupToken { get; set; } = "";
    [Required, StringLength(32)] public string Code { get; set; } = "";
}

/// <summary>A shopper's own two-factor sign-in. Every method works on the customer id from the token, never one from the request.</summary>
public interface ICustomerTwoFactorService
{
    /// <summary>Null when there is no such customer.</summary>
    Task<TwoFactorStatusDto?> GetStatusAsync(Guid customerId, CancellationToken ct = default);

    /// <summary>
    /// Starts (or restarts) setting it up and returns the key. Nothing changes at sign-in until <see cref="EnableAsync"/> succeeds.
    /// Null when the customer cannot have it (no password sign-in, or inactive); refused when it is already on.
    /// </summary>
    Task<TwoFactorSetupDto?> BeginSetupAsync(Guid customerId, CancellationToken ct = default);

    /// <summary>Turns it on once a current code from the app is typed, and returns the recovery codes. Null when the code is wrong or setup was not started.</summary>
    Task<IReadOnlyList<string>?> EnableAsync(Guid customerId, string code, CancellationToken ct = default);

    /// <summary>Turns it off. Needs the password and a current code (or a recovery code), counted like a sign-in. False when either is wrong.</summary>
    Task<bool> DisableAsync(Guid customerId, string password, string code, CancellationToken ct = default);

    /// <summary>Replaces the recovery codes (the old ones stop working). Needs a current code. Null when it is wrong or two-factor is off.</summary>
    Task<IReadOnlyList<string>?> RegenerateRecoveryCodesAsync(Guid customerId, string code, CancellationToken ct = default);

    /// <summary>The customer a still-valid setup token belongs to (the token is not used up), or null.</summary>
    Task<Guid?> CheckSetupTokenAsync(string setupToken, CancellationToken ct = default);

    /// <summary>
    /// Uses the token up and returns the sign-in it stands for (the shopper proved their password to get it), so the caller can issue their
    /// token. Null when it was already used or has run out.
    /// </summary>
    Task<SignInResult?> CompleteSetupSignInAsync(string setupToken, CancellationToken ct = default);
}

/// <summary>Time-based one-time passwords (RFC 6238): six digits, thirty seconds, HMAC-SHA1, as every authenticator app expects.</summary>
public static class Totp
{
    public const int Period = 30;

    public static string NewSecret() => Base32.Encode(RandomNumberGenerator.GetBytes(20));

    public static long StepAt(DateTimeOffset when) => when.ToUnixTimeSeconds() / Period;

    public static string CodeFor(string secret, long step)
    {
        Span<byte> message = stackalloc byte[8];
        BinaryPrimitives.WriteInt64BigEndian(message, step);
        var hash = HMACSHA1.HashData(Base32.Decode(secret), message);
        var offset = hash[^1] & 0x0F;
        var binary = ((hash[offset] & 0x7F) << 24) | (hash[offset + 1] << 16) | (hash[offset + 2] << 8) | hash[offset + 3];
        return (binary % 1_000_000).ToString("D6");
    }

    /// <summary>The step a code is right for, allowing one step either way for clock drift, or null.</summary>
    public static long? Match(string secret, string code, DateTimeOffset now)
    {
        long? found = null;
        var current = StepAt(now);
        for (var step = current - 1; step <= current + 1; step++)
            if (CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(CodeFor(secret, step)), Encoding.ASCII.GetBytes(code)))
                found = step; // no early exit: the answer takes the same time whichever step matched
        return found;
    }
}

internal static class Base32
{
    private const string Alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";

    public static string Encode(ReadOnlySpan<byte> data)
    {
        var sb = new StringBuilder((data.Length * 8 + 4) / 5);
        int buffer = 0, bits = 0;
        foreach (var b in data)
        {
            buffer = (buffer << 8) | b;
            bits += 8;
            while (bits >= 5) { sb.Append(Alphabet[(buffer >> (bits - 5)) & 31]); bits -= 5; }
        }
        if (bits > 0) sb.Append(Alphabet[(buffer << (5 - bits)) & 31]);
        return sb.ToString();
    }

    public static byte[] Decode(string text)
    {
        var bytes = new List<byte>(text.Length * 5 / 8);
        int buffer = 0, bits = 0;
        foreach (var c in text)
        {
            var value = Alphabet.IndexOf(char.ToUpperInvariant(c));
            if (value < 0) continue; // spaces and dashes
            buffer = (buffer << 5) | value;
            bits += 5;
            if (bits >= 8) { bytes.Add((byte)((buffer >> (bits - 8)) & 0xFF)); bits -= 8; }
        }
        return [.. bytes];
    }
}

internal sealed partial class CustomerAccountService : ICustomerTwoFactorService
{
    private const string SetupPurpose = "TwoFactorSetup";
    private const int SetupTokenMinutes = 10;
    private const int RecoveryCodeCount = 10;
    // No 0/O or 1/I/L, so a code read off paper is not misread.
    private const string RecoveryAlphabet = "ABCDEFGHJKMNPQRSTUVWXYZ23456789";

    /// <summary>What a shopper typed, as the stored form: no spaces or dashes, upper case.</summary>
    private static string CleanCode(string? code)
        => string.Concat((code ?? "").Where(c => !char.IsWhiteSpace(c) && c != '-')).ToUpperInvariant();

    private static bool LooksLikeAppCode(string clean) => clean.Length == 6 && clean.All(char.IsAsciiDigit);

    public async Task<TwoFactorStatusDto?> GetStatusAsync(Guid customerId, CancellationToken ct = default)
    {
        if (!await db.Set<Customer>().AnyAsync(c => c.Id == customerId, ct)) return null;
        var enabled = await db.Set<CustomerTwoFactor>().AsNoTracking().Where(t => t.CustomerId == customerId && t.EnabledUtc != null)
            .Select(t => t.EnabledUtc).FirstOrDefaultAsync(ct);
        var left = enabled is null ? 0 : await db.Set<CustomerRecoveryCode>().CountAsync(r => r.CustomerId == customerId && r.UsedUtc == null, ct);
        return new TwoFactorStatusDto(enabled is not null, enabled, left, options.Value.RequireTwoFactor);
    }

    public async Task<TwoFactorSetupDto?> BeginSetupAsync(Guid customerId, CancellationToken ct = default)
    {
        var customer = await (from c in db.Set<Customer>().AsNoTracking()
                              join l in db.Set<CustomerLogin>().AsNoTracking() on c.Id equals l.CustomerId
                              where c.Id == customerId && c.Active
                              select c.Email).FirstOrDefaultAsync(ct);
        if (customer is null) return null;

        var existing = await db.Set<CustomerTwoFactor>().FirstOrDefaultAsync(t => t.CustomerId == customerId, ct);
        if (existing?.EnabledUtc is not null)
            throw new PiranhaCommerceValidationException("TwoFactor", "Two-factor sign-in is already on. Turn it off first to set it up again.");

        // An unfinished key made in the last half hour is kept, so reloading the page does not change a QR code already scanned.
        var now = DateTimeOffset.UtcNow;
        var secret = existing is not null && existing.CreatedUtc > now.AddMinutes(-30) ? existing.SecretKey : Totp.NewSecret();
        if (existing is null) db.Add(new CustomerTwoFactor { CustomerId = customerId, SecretKey = secret, CreatedUtc = now });
        else if (existing.SecretKey != secret) { existing.SecretKey = secret; existing.CreatedUtc = now; }
        await db.SaveChangesAsync(ct);

        var issuer = Uri.EscapeDataString(options.Value.TwoFactorIssuer);
        var uri = $"otpauth://totp/{issuer}:{Uri.EscapeDataString(customer)}?secret={secret}&issuer={issuer}&algorithm=SHA1&digits=6&period={Totp.Period}";
        return new TwoFactorSetupDto(string.Join(' ', Enumerable.Range(0, (secret.Length + 3) / 4).Select(i => secret.Substring(i * 4, Math.Min(4, secret.Length - i * 4)))), uri);
    }

    public async Task<IReadOnlyList<string>?> EnableAsync(Guid customerId, string code, CancellationToken ct = default)
    {
        var row = await db.Set<CustomerTwoFactor>().FirstOrDefaultAsync(t => t.CustomerId == customerId && t.EnabledUtc == null, ct);
        var clean = CleanCode(code);
        if (row is null || !LooksLikeAppCode(clean) || Totp.Match(row.SecretKey, clean, DateTimeOffset.UtcNow) is not { } step) return null;

        row.EnabledUtc = DateTimeOffset.UtcNow;
        row.LastUsedStep = step; // the code just typed cannot be used again to sign in
        var codes = await ReplaceRecoveryCodesAsync(customerId, ct);
        return codes;
    }

    /// <summary>Makes a fresh set, stores their hashes in place of any older ones, and returns the codes (the only time they are readable). Saves.</summary>
    private async Task<IReadOnlyList<string>> ReplaceRecoveryCodesAsync(Guid customerId, CancellationToken ct)
    {
        var old = await db.Set<CustomerRecoveryCode>().Where(r => r.CustomerId == customerId).ToListAsync(ct);
        db.RemoveRange(old);
        var codes = new List<string>(RecoveryCodeCount);
        for (var i = 0; i < RecoveryCodeCount; i++)
        {
            var raw = new string(RandomNumberGenerator.GetItems<char>(RecoveryAlphabet, 10));
            codes.Add($"{raw[..5]}-{raw[5..]}");
            db.Add(new CustomerRecoveryCode { Id = Guid.NewGuid(), CustomerId = customerId, CodeHash = Hash(raw) });
        }
        await db.SaveChangesAsync(ct);
        return codes;
    }

    /// <summary>True when the code is a current authenticator code (each works once) or an unused recovery code (which it uses up).</summary>
    private async Task<bool> VerifyCodeAsync(Guid customerId, string? code, CancellationToken ct)
    {
        var clean = CleanCode(code);
        if (clean.Length == 0) return false;
        var key = await db.Set<CustomerTwoFactor>().AsNoTracking().Where(t => t.CustomerId == customerId && t.EnabledUtc != null)
            .Select(t => new { t.SecretKey, t.LastUsedStep }).FirstOrDefaultAsync(ct);
        if (key is null) return false;

        if (LooksLikeAppCode(clean))
        {
            if (Totp.Match(key.SecretKey, clean, DateTimeOffset.UtcNow) is not { } step || step <= key.LastUsedStep) return false;
            // One atomic statement, so two requests with the same code cannot both win.
            return await db.Set<CustomerTwoFactor>().Where(t => t.CustomerId == customerId && t.LastUsedStep < step)
                .ExecuteUpdateAsync(s => s.SetProperty(t => t.LastUsedStep, step), ct) > 0;
        }

        var hash = Hash(clean);
        var now = DateTimeOffset.UtcNow;
        return await db.Set<CustomerRecoveryCode>().Where(r => r.CustomerId == customerId && r.CodeHash == hash && r.UsedUtc == null)
            .ExecuteUpdateAsync(s => s.SetProperty(r => r.UsedUtc, (DateTimeOffset?)now), ct) > 0;
    }

    public async Task<bool> DisableAsync(Guid customerId, string password, string code, CancellationToken ct = default)
    {
        var email = await db.Set<Customer>().Where(c => c.Id == customerId && c.Active).Select(c => c.Email).FirstOrDefaultAsync(ct);
        if (email is null) return false;

        // The password is checked, and counted, as at sign-in; a wrong code counts too, so a stolen token cannot be used to guess either.
        var check = await CheckAsync(email, password, resetFailures: false, ct);
        if (check.Status != SignInStatus.Valid || check.CustomerId != customerId) return false;
        if (!await VerifyCodeAsync(customerId, code, ct))
        {
            await RecordFailureAsync(customerId, ct);
            return false;
        }
        await ClearFailuresAsync(customerId, ct);

        await db.Set<CustomerRecoveryCode>().Where(r => r.CustomerId == customerId).ExecuteDeleteAsync(ct);
        return await db.Set<CustomerTwoFactor>().Where(t => t.CustomerId == customerId).ExecuteDeleteAsync(ct) > 0;
    }

    public async Task<IReadOnlyList<string>?> RegenerateRecoveryCodesAsync(Guid customerId, string code, CancellationToken ct = default)
    {
        if (!await VerifyCodeAsync(customerId, code, ct))
        {
            await RecordFailureAsync(customerId, ct);
            return null;
        }
        return await ReplaceRecoveryCodesAsync(customerId, ct);
    }

    /// <summary>A single-use value that lets someone who has just proved their password set up two-factor, when the shop requires it. Not a sign-in.</summary>
    private async Task<string> CreateSetupTokenAsync(Guid customerId, CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        await db.Set<CustomerToken>().Where(t => t.CustomerId == customerId && t.Purpose == SetupPurpose && t.UsedUtc == null)
            .ExecuteUpdateAsync(s => s.SetProperty(t => t.UsedUtc, (DateTimeOffset?)now), ct); // only the newest one works
        var secret = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        var token = new CustomerToken { Id = Guid.NewGuid(), CustomerId = customerId, Purpose = SetupPurpose, TokenHash = Hash(secret), CreatedUtc = now, ExpiresUtc = now.AddMinutes(SetupTokenMinutes) };
        db.Add(token);
        await db.SaveChangesAsync(ct);
        return $"{token.Id:N}.{secret}";
    }

    public async Task<Guid?> CheckSetupTokenAsync(string setupToken, CancellationToken ct = default)
    {
        var parts = (setupToken ?? "").Split('.', 2);
        if (parts.Length != 2 || !Guid.TryParseExact(parts[0], "N", out var tokenId)) return null;
        var row = await db.Set<CustomerToken>().AsNoTracking().Where(t => t.Id == tokenId && t.Purpose == SetupPurpose)
            .Select(t => new { t.CustomerId, t.TokenHash, t.UsedUtc, t.ExpiresUtc }).FirstOrDefaultAsync(ct);
        if (row is null || row.UsedUtc is not null || row.ExpiresUtc <= DateTimeOffset.UtcNow) return null;
        if (!CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(row.TokenHash), Encoding.ASCII.GetBytes(Hash(parts[1])))) return null;
        return await db.Set<Customer>().AnyAsync(c => c.Id == row.CustomerId && c.Active, ct) ? row.CustomerId : null;
    }

    public async Task<SignInResult?> CompleteSetupSignInAsync(string setupToken, CancellationToken ct = default)
    {
        if (await CheckSetupTokenAsync(setupToken, ct) is not { } customerId) return null; // wrong, used or expired
        var tokenId = Guid.ParseExact(setupToken.Split('.', 2)[0], "N");
        var now = DateTimeOffset.UtcNow;
        var spent = await db.Set<CustomerToken>().Where(t => t.Id == tokenId && t.Purpose == SetupPurpose && t.UsedUtc == null && t.ExpiresUtc > now)
            .ExecuteUpdateAsync(s => s.SetProperty(t => t.UsedUtc, (DateTimeOffset?)now), ct) > 0;
        if (!spent) return null;
        var row = await (from l in db.Set<CustomerLogin>().AsNoTracking()
                         join c in db.Set<Customer>().AsNoTracking() on l.CustomerId equals c.Id
                         where l.CustomerId == customerId && c.Active
                         select new { c.Email, l.SessionVersion }).FirstOrDefaultAsync(ct);
        if (row is null) return null;
        await ClearFailuresAsync(customerId, ct);
        return new SignInResult(SignInStatus.Valid, customerId, row.Email, row.SessionVersion);
    }

    private Task ClearFailuresAsync(Guid customerId, CancellationToken ct)
        => db.Set<CustomerLogin>().Where(l => l.CustomerId == customerId).ExecuteUpdateAsync(s => s.SetProperty(l => l.AccessFailedCount, 0), ct);
}
