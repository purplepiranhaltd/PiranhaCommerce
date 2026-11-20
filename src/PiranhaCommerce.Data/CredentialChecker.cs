using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using PiranhaCommerce.Core.Identity;

namespace PiranhaCommerce.Data;

internal sealed class CredentialChecker(
    UserManager<PiranhaUser> users, PiranhaDbContext db, IOptions<IdentityOptions> options, IPasswordHasher<PiranhaUser> hasher) : ICredentialChecker
{
    // Burned when the email is unknown so that "no such user" takes as long as "wrong password".
    private static readonly PiranhaUser Dummy = new() { UserName = "dummy" };

    public async Task<CredentialResult> CheckAsync(string email, string password, CancellationToken ct = default)
    {
        var user = string.IsNullOrWhiteSpace(email) ? null : await users.FindByEmailAsync(email.Trim());
        if (user is null || !user.Active)
        {
            hasher.HashPassword(Dummy, password ?? "");
            return new(CredentialStatus.Invalid, null);
        }

        if (user.LockoutEnabled && user.LockoutEnd is { } until && until > DateTimeOffset.UtcNow)
            return new(CredentialStatus.LockedOut, null);

        if (!await users.CheckPasswordAsync(user, password ?? ""))
            return new(await RecordFailureAsync(user.Id, ct) ? CredentialStatus.LockedOut : CredentialStatus.Invalid, null);

        // Success is only granted if the account is STILL unlocked at this instant (one atomic statement). A burst of guesses that
        // all passed the check above before the lock landed cannot sign in once it has: the right password is refused after that.
        // Conflict-free as well: it does not touch the concurrency stamp, so simultaneous correct sign-ins cannot refuse each other.
        var now = DateTimeOffset.UtcNow;
        var granted = await db.Users.Where(u => u.Id == user.Id && (!u.LockoutEnabled || u.LockoutEnd == null || u.LockoutEnd <= now))
            .ExecuteUpdateAsync(s => s.SetProperty(u => u.AccessFailedCount, 0), ct);
        return granted > 0 ? new(CredentialStatus.Valid, user) : new(CredentialStatus.LockedOut, null);
    }

    /// <summary>Counts one failed attempt with a single atomic increment; returns true when it locked the account.</summary>
    private async Task<bool> RecordFailureAsync(Guid userId, CancellationToken ct)
    {
        var lockout = options.Value.Lockout;
        await db.Users.Where(u => u.Id == userId && u.LockoutEnabled)
            .ExecuteUpdateAsync(s => s.SetProperty(u => u.AccessFailedCount, u => u.AccessFailedCount + 1), ct);

        var max = lockout.MaxFailedAccessAttempts;
        var lockedUntil = DateTimeOffset.UtcNow + lockout.DefaultLockoutTimeSpan;
        var locked = await db.Users.Where(u => u.Id == userId && u.LockoutEnabled && u.AccessFailedCount >= max)
            .ExecuteUpdateAsync(s => s
                .SetProperty(u => u.AccessFailedCount, 0)
                .SetProperty(u => u.LockoutEnd, (DateTimeOffset?)lockedUntil), ct);
        return locked > 0;
    }
}
