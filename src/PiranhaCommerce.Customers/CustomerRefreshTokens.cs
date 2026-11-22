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

/// <summary>One signed-in device of a shopper: its refresh chain. Id is what ends it.</summary>
public sealed record ShopperSessionDto(Guid Id, DateTimeOffset StartedUtc, DateTimeOffset LastUsedUtc, DateTimeOffset ExpiresUtc);

public interface ICustomerRefreshService
{
    /// <summary>The shopper's devices that can still refresh. A device whose access token is still running but which has no refresh token is not listed.</summary>
    Task<IReadOnlyList<ShopperSessionDto>> ListSessionsAsync(Guid customerId, CancellationToken ct = default);

    /// <summary>Ends one device's chain. False when it is not one of this customer's live sessions.</summary>
    Task<bool> EndSessionAsync(Guid customerId, Guid sessionId, CancellationToken ct = default);

    /// <summary>Ends every session, access tokens included, and returns the new session version for the new token of whoever asked. Null if the customer is gone.</summary>
    Task<PasswordChangeResult?> SignOutEverywhereAsync(Guid customerId, CancellationToken ct = default);

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

        // A replay can revoke the family between our claim and the new token being saved, which would leave this one alive. Looking again AFTER
        // saving closes that: either the revoke came first (seen here) or it comes later (and covers the new token).
        if (await db.Set<CustomerRefreshToken>().AsNoTracking().AnyAsync(t => t.FamilyId == row.FamilyId && t.RevokedUtc != null, ct))
        {
            await RevokeFamilyAsync(row.FamilyId, now, ct);
            return null;
        }
        return new RefreshResult(row.CustomerId, email, row.SessionVersion, next);
    }

    public async Task<IReadOnlyList<ShopperSessionDto>> ListSessionsAsync(Guid customerId, CancellationToken ct = default)
    {
        var now = DateTimeOffset.UtcNow;
        var live = await (from t in db.Set<CustomerRefreshToken>().AsNoTracking()
                          join l in db.Set<CustomerLogin>() on t.CustomerId equals l.CustomerId
                          where t.CustomerId == customerId && t.UsedUtc == null && t.RevokedUtc == null && t.ExpiresUtc > now && t.SessionVersion == l.SessionVersion
                          select new ShopperSessionDto(t.FamilyId, t.FamilyStartedUtc, t.CreatedUtc, t.ExpiresUtc)).ToListAsync(ct);
        return live.OrderByDescending(x => x.LastUsedUtc).ToList();
    }

    public async Task<bool> EndSessionAsync(Guid customerId, Guid sessionId, CancellationToken ct = default)
    {
        var mine = await db.Set<CustomerRefreshToken>().AnyAsync(t => t.FamilyId == sessionId && t.CustomerId == customerId && t.RevokedUtc == null, ct);
        if (!mine) return false;
        await RevokeFamilyAsync(sessionId, DateTimeOffset.UtcNow, ct);
        return true;
    }

    async Task<PasswordChangeResult?> ICustomerRefreshService.SignOutEverywhereAsync(Guid customerId, CancellationToken ct)
    {
        if (!await SignOutEverywhereAsync(customerId, "shopper", ct)) return null;
        var row = await (from l in db.Set<CustomerLogin>().AsNoTracking() join c in db.Set<Customer>() on l.CustomerId equals c.Id
                         where l.CustomerId == customerId select new { c.Email, l.SessionVersion }).FirstOrDefaultAsync(ct);
        return row is null ? null : new PasswordChangeResult(row.Email, row.SessionVersion);
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
