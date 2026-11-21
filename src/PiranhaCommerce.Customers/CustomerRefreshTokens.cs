using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace PiranhaCommerce.Customers;

/// <summary>
/// A long-lived, single-use token that lets a shopper's app get a new short access token without asking for the password again. Only a hash is
/// stored. Each use replaces it with a new one in the same family; using one a second time means it was copied, so the whole family is revoked.
/// It also dies when the sign-in's session version moves on (sign out everywhere, password change or reset, switching the customer off).
/// </summary>
public class CustomerRefreshToken
{
    public Guid Id { get; set; }
    public Guid CustomerId { get; set; }

    /// <summary>One sign-in on one device: every token handed out by refreshing it shares this.</summary>
    public Guid FamilyId { get; set; }
    public string TokenHash { get; set; } = "";

    /// <summary>The sign-in's session version when this was issued.</summary>
    public int SessionVersion { get; set; }
    public DateTimeOffset CreatedUtc { get; set; }
    public DateTimeOffset ExpiresUtc { get; set; }

    /// <summary>When the first token of the family was issued, so a family cannot be refreshed for ever.</summary>
    public DateTimeOffset FamilyStartedUtc { get; set; }
    public DateTimeOffset? UsedUtc { get; set; }
    public DateTimeOffset? RevokedUtc { get; set; }
}

public sealed class RefreshTokenInput
{
    [System.ComponentModel.DataAnnotations.Required, System.ComponentModel.DataAnnotations.StringLength(200)]
    public string RefreshToken { get; set; } = "";
}

public sealed record RefreshResult(Guid CustomerId, string Email, int SessionVersion, string RefreshToken);

public interface ICustomerRefreshService
{
    /// <summary>
    /// Starts a new refresh family for a sign-in that has just succeeded and returns the token (shown once, never stored). Null when refresh
    /// tokens are switched off (<c>RefreshTokenDays</c> is 0).
    /// </summary>
    Task<string?> IssueRefreshTokenAsync(Guid customerId, int sessionVersion, CancellationToken ct = default);

    /// <summary>
    /// Swaps a refresh token for the next one. Null for anything that is not a live token: unknown, expired, revoked, from before sessions were
    /// ended, or for a customer who can no longer sign in. A token used twice revokes its whole family.
    /// </summary>
    Task<RefreshResult?> RefreshAsync(string refreshToken, CancellationToken ct = default);

    /// <summary>Ends the sign-in this token belongs to on its device. Says nothing about whether the token was real.</summary>
    Task RevokeRefreshTokenAsync(string refreshToken, CancellationToken ct = default);
}

internal sealed partial class CustomerAccountService : ICustomerRefreshService
{
    private static string RefreshHash(string token) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token)));

    public async Task<string?> IssueRefreshTokenAsync(Guid customerId, int sessionVersion, CancellationToken ct = default)
    {
        if (options.Value.RefreshTokenDays <= 0) return null;
        var now = DateTimeOffset.UtcNow;
        return await NewRefreshTokenAsync(customerId, Guid.NewGuid(), sessionVersion, now, now, ct);
    }

    private async Task<string> NewRefreshTokenAsync(Guid customerId, Guid family, int sessionVersion, DateTimeOffset familyStarted, DateTimeOffset now, CancellationToken ct)
    {
        var o = options.Value;
        var raw = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_'); // 256 bits: cannot be guessed
        var cap = familyStarted.AddDays(Math.Max(o.RefreshTokenDays, o.RefreshTokenMaxDays));
        var expires = now.AddDays(o.RefreshTokenDays);
        db.Add(new CustomerRefreshToken
        {
            Id = Guid.NewGuid(), CustomerId = customerId, FamilyId = family, TokenHash = RefreshHash(raw), SessionVersion = sessionVersion,
            CreatedUtc = now, ExpiresUtc = expires < cap ? expires : cap, FamilyStartedUtc = familyStarted,
        });
        await db.SaveChangesAsync(ct);

        // Tidy up this customer's long-dead tokens as we go (a week past expiry: they are kept until then so a replayed one is still recognised).
        var old = now.AddDays(-7);
        await db.Set<CustomerRefreshToken>().Where(t => t.CustomerId == customerId && t.ExpiresUtc < old).ExecuteDeleteAsync(ct);
        return raw;
    }

    public async Task<RefreshResult?> RefreshAsync(string refreshToken, CancellationToken ct = default)
    {
        if (options.Value.RefreshTokenDays <= 0 || string.IsNullOrWhiteSpace(refreshToken) || refreshToken.Length > 200) return null;
        var hash = RefreshHash(refreshToken.Trim());
        var now = DateTimeOffset.UtcNow;

        var row = await db.Set<CustomerRefreshToken>().AsNoTracking().FirstOrDefaultAsync(t => t.TokenHash == hash, ct);
        if (row is null || row.RevokedUtc is not null || row.ExpiresUtc <= now) return null;

        // The claim: only one caller can turn an unused token into a used one. A token already used, or used by someone a moment ago, is a copy.
        var claimed = row.UsedUtc is null && await db.Set<CustomerRefreshToken>()
            .Where(t => t.Id == row.Id && t.UsedUtc == null && t.RevokedUtc == null)
            .ExecuteUpdateAsync(s => s.SetProperty(t => t.UsedUtc, (DateTimeOffset?)now), ct) > 0;
        if (!claimed)
        {
            await RevokeFamilyAsync(row.FamilyId, now, ct);
            logger.LogWarning("A refresh token was used twice; its sign-in on that device has been ended.");
            return null;
        }

        if (!await IsSessionCurrentAsync(row.CustomerId, row.SessionVersion, ct))
        {
            await RevokeFamilyAsync(row.FamilyId, now, ct);
            return null;
        }
        var email = await db.Set<Customer>().AsNoTracking().Where(c => c.Id == row.CustomerId).Select(c => c.Email).FirstOrDefaultAsync(ct);
        if (email is null) return null;

        var next = await NewRefreshTokenAsync(row.CustomerId, row.FamilyId, row.SessionVersion, row.FamilyStartedUtc, now, ct);
        return new RefreshResult(row.CustomerId, email, row.SessionVersion, next);
    }

    public async Task RevokeRefreshTokenAsync(string refreshToken, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(refreshToken) || refreshToken.Length > 200) return;
        var hash = RefreshHash(refreshToken.Trim());
        var family = await db.Set<CustomerRefreshToken>().AsNoTracking().Where(t => t.TokenHash == hash).Select(t => (Guid?)t.FamilyId).FirstOrDefaultAsync(ct);
        if (family is { } f) await RevokeFamilyAsync(f, DateTimeOffset.UtcNow, ct);
    }

    private Task<int> RevokeFamilyAsync(Guid family, DateTimeOffset now, CancellationToken ct) =>
        db.Set<CustomerRefreshToken>().Where(t => t.FamilyId == family && t.RevokedUtc == null)
            .ExecuteUpdateAsync(s => s.SetProperty(t => t.RevokedUtc, (DateTimeOffset?)now), ct);
}
