using Microsoft.EntityFrameworkCore;
using PiranhaCommerce.Core.Domain;
using PiranhaCommerce.Data;
using PiranhaCommerce.Core.Identity;

namespace PiranhaCommerce.Customers;

/// <summary>
/// One thing staff did about a customer's sign-in (unlock, sign out, reset link, remove, switch off or on). Holds no password or token,
/// and goes when the customer is erased (it is part of their data, so it is in their export too).
/// </summary>
public class CustomerSignInEvent : Entity
{
    public Guid CustomerId { get; set; }
    public DateTimeOffset OccurredUtc { get; set; }
    /// <summary>Who did it: the staff member's email, or null when unknown.</summary>
    public string? Actor { get; set; }
    /// <summary>For example "unlocked", "signed-out", "reset-link", "sign-in-removed", "switched-off", "switched-on".</summary>
    public string Action { get; set; } = "";
    /// <summary>Short outcome where there is one, for example "Sent" or "RateLimited".</summary>
    public string? Detail { get; set; }
}

public sealed record SignInEventDto(DateTimeOffset OccurredUtc, string? Actor, string Action, string? Detail);

/// <summary>Where a customer's sign-in stands, as staff see it.</summary>
public enum SignInState
{
    /// <summary>No sign-in: a customer made by staff or by a guest order. A reset link doubles as their first invitation.</summary>
    None,
    Active,
    /// <summary>Too many wrong passwords; clears by itself at <see cref="CustomerSignInDto.LockedUntil"/>, or when staff unlock it.</summary>
    Locked,
    /// <summary>The customer record is switched off (the Active flag): every session ended and nothing signs in.</summary>
    Disabled,
}

/// <summary>Never carries the password hash or the session version.</summary>
public sealed record CustomerSignInDto(
    Guid CustomerId, SignInState State, bool HasSignIn, bool CustomerActive, DateTimeOffset? LockedUntil, int FailedAttempts,
    DateTimeOffset? CreatedUtc, DateTimeOffset? PasswordChangedUtc, DateTimeOffset? EmailVerifiedUtc, bool TwoFactorEnabled = false);

/// <summary>Why staff sending a reset link did or did not result in an email.</summary>
public enum ResetLinkOutcome
{
    Sent,
    /// <summary>The customer is switched off, so nothing is sent.</summary>
    Inactive,
    /// <summary>Too many links were sent to them in the last hour (staff sends count too).</summary>
    RateLimited,
    /// <summary>The email provider refused it or could not be reached; the link was made but nobody received it.</summary>
    SendFailed,
}

public sealed record ResetLinkResultDto(bool Sent, ResetLinkOutcome Outcome);

/// <summary>What staff can do about a shopper's sign-in. Every method says false/null when the customer (or their sign-in) does not exist.</summary>
public interface ICustomerSignInAdminService
{
    Task<CustomerSignInDto?> GetAsync(Guid customerId, CancellationToken ct = default);

    /// <summary>The state of many customers at once (one query), for a list. Customers that do not exist are left out.</summary>
    Task<IReadOnlyDictionary<Guid, SignInState>> GetStatesAsync(IReadOnlyCollection<Guid> customerIds, CancellationToken ct = default);

    /// <summary>Clears a lockout and the count of wrong passwords. False when there is no sign-in.</summary>
    [RequiresPermission(Permissions.CustomersManage)]
    Task<bool> UnlockAsync(Guid customerId, string? actor = null, CancellationToken ct = default);

    /// <summary>Ends every live session at once (a token is refused on its next call). They can sign in again. False when there is no sign-in.</summary>
    [RequiresPermission(Permissions.CustomersManage)]
    Task<bool> SignOutEverywhereAsync(Guid customerId, string? actor = null, CancellationToken ct = default);

    /// <summary>
    /// Emails them a link to choose a password. For a customer with no sign-in this is the invitation: following the link creates it,
    /// the email address being the proof. Null when the customer does not exist.
    /// </summary>
    [RequiresPermission(Permissions.CustomersManage)]
    Task<ResetLinkOutcome?> SendResetLinkAsync(Guid customerId, string? actor = null, CancellationToken ct = default);

    /// <summary>
    /// Deletes the sign-in (the customer, their addresses and orders stay) and spends any link already emailed, so an old one cannot bring
    /// it back. They can still ask for a new link themselves. False when there was none.
    /// </summary>
    [RequiresPermission(Permissions.CustomersManage)]
    Task<bool> RemoveAsync(Guid customerId, string? actor = null, CancellationToken ct = default);

    /// <summary>
    /// Turns off the shopper's two-factor sign-in and forgets its key and recovery codes, for a shopper who has lost their phone and their recovery
    /// codes. Staff must be sure who they are talking to first. They can set it up again. False when it was not on.
    /// </summary>
    [RequiresPermission(Permissions.CustomersManage)]
    Task<bool> ResetTwoFactorAsync(Guid customerId, string? actor = null, CancellationToken ct = default);

    /// <summary>Staff vouch for the address (they checked it another way). False when there is no sign-in.</summary>
    [RequiresPermission(Permissions.CustomersManage)]
    Task<bool> MarkEmailVerifiedAsync(Guid customerId, string? actor = null, CancellationToken ct = default);

    /// <summary>What staff have done about this customer's sign-in, newest first (at most <paramref name="limit"/>, default 50). Null when the customer does not exist.</summary>
    Task<IReadOnlyList<SignInEventDto>?> HistoryAsync(Guid customerId, int limit = 50, CancellationToken ct = default);
}

internal sealed partial class CustomerAccountService : ICustomerSignInAdminService
{
    private static SignInState StateOf(bool hasLogin, bool active, DateTimeOffset? lockoutEnd, DateTimeOffset now)
        => !hasLogin ? SignInState.None
         : !active ? SignInState.Disabled
         : lockoutEnd is { } until && until > now ? SignInState.Locked
         : SignInState.Active;

    public async Task<CustomerSignInDto?> GetAsync(Guid customerId, CancellationToken ct = default)
    {
        var row = await (
            from c in db.Set<Customer>().AsNoTracking()
            where c.Id == customerId
            join l in db.Set<CustomerLogin>().AsNoTracking() on c.Id equals l.CustomerId into logins
            from l in logins.DefaultIfEmpty()
            select new
            {
                c.Active, HasLogin = l != null,
                LockoutEnd = l != null ? l.LockoutEnd : null,
                Failed = l != null ? l.AccessFailedCount : 0,
                Created = l != null ? (DateTimeOffset?)l.CreatedUtc : null,
                Changed = l != null ? (DateTimeOffset?)l.PasswordChangedUtc : null,
                Verified = l != null ? l.EmailVerifiedUtc : null,
            }).FirstOrDefaultAsync(ct);
        if (row is null) return null;
        var twoFactor = await db.Set<CustomerTwoFactor>().AsNoTracking().AnyAsync(t => t.CustomerId == customerId && t.EnabledUtc != null, ct);

        var now = DateTimeOffset.UtcNow;
        var state = StateOf(row.HasLogin, row.Active, row.LockoutEnd, now);
        return new CustomerSignInDto(customerId, state, row.HasLogin, row.Active,
            state == SignInState.Locked ? row.LockoutEnd : null, row.Failed, row.Created, row.Changed, row.Verified, twoFactor);
    }

    public async Task<IReadOnlyDictionary<Guid, SignInState>> GetStatesAsync(IReadOnlyCollection<Guid> customerIds, CancellationToken ct = default)
    {
        if (customerIds.Count == 0) return new Dictionary<Guid, SignInState>();
        var rows = await (
            from c in db.Set<Customer>().AsNoTracking()
            where customerIds.Contains(c.Id)
            join l in db.Set<CustomerLogin>().AsNoTracking() on c.Id equals l.CustomerId into logins
            from l in logins.DefaultIfEmpty()
            select new { c.Id, c.Active, HasLogin = l != null, LockoutEnd = l != null ? l.LockoutEnd : null }).ToListAsync(ct);
        var now = DateTimeOffset.UtcNow;
        return rows.ToDictionary(r => r.Id, r => StateOf(r.HasLogin, r.Active, r.LockoutEnd, now));
    }

    internal async Task RecordAsync(Guid customerId, string action, string? actor, string? detail, CancellationToken ct)
    {
        db.Add(new CustomerSignInEvent { CustomerId = customerId, OccurredUtc = DateTimeOffset.UtcNow, Actor = actor, Action = action, Detail = detail });
        try { await db.SaveChangesAsync(ct); }
        catch (DbUpdateException) { db.ChangeTracker.Clear(); } // the customer was erased a moment ago: there is nothing left to record against
    }

    public async Task<bool> UnlockAsync(Guid customerId, string? actor = null, CancellationToken ct = default)
    {
        var done = await db.Set<CustomerLogin>().Where(l => l.CustomerId == customerId)
            .ExecuteUpdateAsync(s => s.SetProperty(l => l.LockoutEnd, (DateTimeOffset?)null).SetProperty(l => l.AccessFailedCount, 0), ct) > 0;
        if (done) await RecordAsync(customerId, "unlocked", actor, null, ct);
        return done;
    }

    public async Task<bool> SignOutEverywhereAsync(Guid customerId, string? actor = null, CancellationToken ct = default)
    {
        var done = await db.Set<CustomerLogin>().Where(l => l.CustomerId == customerId)
            .ExecuteUpdateAsync(s => s.SetProperty(l => l.SessionVersion, l => l.SessionVersion + 1), ct) > 0;
        if (done) await RecordAsync(customerId, "signed-out", actor, null, ct);
        return done;
    }

    public async Task<IReadOnlyList<SignInEventDto>?> HistoryAsync(Guid customerId, int limit = 50, CancellationToken ct = default)
    {
        if (!await db.Set<Customer>().AnyAsync(c => c.Id == customerId, ct)) return null;
        return await db.Set<CustomerSignInEvent>().AsNoTracking().Where(e => e.CustomerId == customerId)
            .OrderByDescending(e => e.OccurredUtc).ThenByDescending(e => e.Id).Take(Math.Clamp(limit, 1, 500))
            .Select(e => new SignInEventDto(e.OccurredUtc, e.Actor, e.Action, e.Detail)).ToListAsync(ct);
    }

    public async Task<ResetLinkOutcome?> SendResetLinkAsync(Guid customerId, string? actor = null, CancellationToken ct = default)
    {
        var customer = await db.Set<Customer>().AsNoTracking().Where(c => c.Id == customerId).Select(c => new { c.Email, c.Active }).FirstOrDefaultAsync(ct);
        if (customer is null) return null;
        var outcome = customer.Active ? await SendResetLinkCoreAsync(customerId, customer.Email, ct) : ResetLinkOutcome.Inactive;
        await RecordAsync(customerId, "reset-link", actor, outcome.ToString(), ct); // attempts are recorded too, not just successes
        return outcome;
    }

    public async Task<bool> ResetTwoFactorAsync(Guid customerId, string? actor = null, CancellationToken ct = default)
    {
        await db.Set<CustomerRecoveryCode>().Where(r => r.CustomerId == customerId).ExecuteDeleteAsync(ct);
        var removed = await db.Set<CustomerTwoFactor>().Where(t => t.CustomerId == customerId).ExecuteDeleteAsync(ct) > 0;
        if (removed) await RecordAsync(customerId, "two-factor-reset", actor, null, ct);
        return removed;
    }

    public async Task<bool> MarkEmailVerifiedAsync(Guid customerId, string? actor = null, CancellationToken ct = default)
    {
        var now = DateTimeOffset.UtcNow;
        var done = await db.Set<CustomerLogin>().Where(l => l.CustomerId == customerId && l.EmailVerifiedUtc == null)
            .ExecuteUpdateAsync(s => s.SetProperty(l => l.EmailVerifiedUtc, (DateTimeOffset?)now), ct) > 0;
        if (done) await RecordAsync(customerId, "email-verified", actor, "by staff", ct);
        return done;
    }

    public async Task<bool> RemoveAsync(Guid customerId, string? actor = null, CancellationToken ct = default)
    {
        // Links first: the other way round, a link used between the two steps would put the sign-in straight back.
        var now = DateTimeOffset.UtcNow;
        await db.Set<CustomerToken>().Where(t => t.CustomerId == customerId && t.UsedUtc == null)
            .ExecuteUpdateAsync(s => s.SetProperty(t => t.UsedUtc, (DateTimeOffset?)now), ct);
        var removed = await db.Set<CustomerLogin>().Where(l => l.CustomerId == customerId).ExecuteDeleteAsync(ct) > 0;
        if (removed) await RecordAsync(customerId, "sign-in-removed", actor, null, ct);
        return removed;
    }
}
