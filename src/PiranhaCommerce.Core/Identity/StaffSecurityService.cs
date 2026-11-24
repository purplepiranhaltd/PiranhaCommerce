using System.Globalization;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;
using PiranhaCommerce.Core.Audit;
using PiranhaCommerce.Core.Validation;

namespace PiranhaCommerce.Core.Identity;

/// <param name="Required">Two-factor sign-in is compulsory for staff, so it cannot be turned off.</param>
public sealed record TwoFactorStatus(bool Enabled, int RecoveryCodesLeft, bool Required = false);

/// <param name="SharedKey">The key to type into an authenticator app, in groups of four to be easy to read.</param>
/// <param name="Uri">The same key as an <c>otpauth://</c> address, which phones can open and QR codes carry.</param>
public sealed record TwoFactorSetup(string SharedKey, string Uri);

/// <summary>
/// Two-factor sign-in for the person who is signed in, using an authenticator app. Every method acts on the person it is given, so the admin page passes
/// the signed-in user's own id and nobody sets up or turns off someone else's. (A lost phone is dealt with by an administrator through
/// <see cref="IStaffAdminService.ResetTwoFactorAsync"/>.)
/// </summary>
public interface IStaffSecurityService
{
    Task<TwoFactorStatus> GetStatusAsync(Guid userId, CancellationToken ct = default);

    /// <summary>Starts setup: makes a key to put in the app. Nothing changes for sign-in until <see cref="ConfirmSetupAsync"/> succeeds.</summary>
    Task<TwoFactorSetup> BeginSetupAsync(Guid userId, CancellationToken ct = default);

    /// <summary>Checks the first code from the app, turns two-factor on and returns ten recovery codes, shown once.</summary>
    Task<IReadOnlyList<string>> ConfirmSetupAsync(Guid userId, string code, CancellationToken ct = default);

    /// <summary>Turns two-factor off. Needs a current code (or a recovery code), so someone at an unlocked screen cannot do it.</summary>
    Task DisableAsync(Guid userId, string code, CancellationToken ct = default);

    /// <summary>Replaces the recovery codes (the old ones stop working). Needs a current code.</summary>
    Task<IReadOnlyList<string>> NewRecoveryCodesAsync(Guid userId, string code, CancellationToken ct = default);
}

internal sealed class StaffSecurityService(UserManager<PiranhaCommerceUser> users, IServiceProvider services, StaffPermissionService permissions, Microsoft.Extensions.Options.IOptions<StaffSecurityOptions> options) : IStaffSecurityService
{
    private const string Issuer = "PiranhaCommerce";
    private const int RecoveryCodeCount = 10;

    public async Task<TwoFactorStatus> GetStatusAsync(Guid userId, CancellationToken ct = default)
    {
        var user = await FindAsync(userId);
        return new TwoFactorStatus(user.TwoFactorEnabled, user.TwoFactorEnabled ? await users.CountRecoveryCodesAsync(user) : 0, options.Value.RequireTwoFactor);
    }

    public async Task<TwoFactorSetup> BeginSetupAsync(Guid userId, CancellationToken ct = default)
    {
        var user = await FindAsync(userId);
        if (user.TwoFactorEnabled) throw new PiranhaCommerceConflictException("Two-factor sign-in is already on. Turn it off first to set it up on a new phone.");
        // A new key each time: someone who abandoned an earlier attempt cannot be confirmed with a stale one.
        await users.ResetAuthenticatorKeyAsync(user);
        var key = (await users.GetAuthenticatorKeyAsync(user))!;
        return new TwoFactorSetup(Spaced(key), $"otpauth://totp/{Uri.EscapeDataString(Issuer)}:{Uri.EscapeDataString(user.Email ?? user.UserName ?? "")}" +
            $"?secret={key}&issuer={Uri.EscapeDataString(Issuer)}&digits=6");
    }

    public async Task<IReadOnlyList<string>> ConfirmSetupAsync(Guid userId, string code, CancellationToken ct = default)
    {
        var user = await FindAsync(userId);
        if (user.TwoFactorEnabled) throw new PiranhaCommerceConflictException("Two-factor sign-in is already on.");
        if (await users.GetAuthenticatorKeyAsync(user) is null) throw new PiranhaCommerceConflictException("Start the setup first.");
        await RequireAppCodeAsync(user, code);
        await users.SetTwoFactorEnabledAsync(user, true);
        permissions.Invalidate(); // the person is let in at once
        var codes = (await users.GenerateNewTwoFactorRecoveryCodesAsync(user, RecoveryCodeCount))!.ToList();
        await RecordAsync(user, "two-factor turned on", "TwoFactor", "Off", "On", ct);
        return codes;
    }

    public async Task DisableAsync(Guid userId, string code, CancellationToken ct = default)
    {
        var user = await FindAsync(userId);
        if (!user.TwoFactorEnabled) return;
        if (options.Value.RequireTwoFactor) throw new PiranhaCommerceConflictException("Two-factor sign-in is required for all staff, so it cannot be turned off. To move to a new phone, ask an administrator to reset it.");
        await RequireCodeAsync(user, code);
        await TurnOffAsync(user);
        permissions.Invalidate();
        await RecordAsync(user, "two-factor turned off", "TwoFactor", "On", "Off", ct);
    }

    public async Task<IReadOnlyList<string>> NewRecoveryCodesAsync(Guid userId, string code, CancellationToken ct = default)
    {
        var user = await FindAsync(userId);
        if (!user.TwoFactorEnabled) throw new PiranhaCommerceConflictException("Turn two-factor sign-in on first.");
        await RequireAppCodeAsync(user, code);
        var codes = (await users.GenerateNewTwoFactorRecoveryCodesAsync(user, RecoveryCodeCount))!.ToList();
        await RecordAsync(user, "recovery codes replaced", "RecoveryCodes", null, "New", ct);
        return codes;
    }

    /// <summary>Used by the staff service for an administrator's reset.</summary>
    internal async Task TurnOffAsync(PiranhaCommerceUser user)
    {
        await users.SetTwoFactorEnabledAsync(user, false);
        await users.ResetAuthenticatorKeyAsync(user);
        // Leave no recovery codes behind that would still work after a fresh setup.
        await users.RemoveAuthenticationTokenAsync(user, "[AspNetUserStore]", "RecoveryCodes");
    }

    private async Task RequireAppCodeAsync(PiranhaCommerceUser user, string code)
    {
        if (!await users.VerifyTwoFactorTokenAsync(user, TokenOptions.DefaultAuthenticatorProvider, Clean(code)))
            await WrongAsync(user);
    }

    private async Task RequireCodeAsync(PiranhaCommerceUser user, string code)
    {
        if (await users.VerifyTwoFactorTokenAsync(user, TokenOptions.DefaultAuthenticatorProvider, Clean(code))) return;
        if ((await users.RedeemTwoFactorRecoveryCodeAsync(user, (code ?? "").Trim())).Succeeded) return;
        await WrongAsync(user);
    }

    private async Task WrongAsync(PiranhaCommerceUser user)
    {
        // Guessing codes from a signed-in session counts the same as guessing at the sign-in page.
        await users.AccessFailedAsync(user);
        throw new PiranhaCommerceValidationException("Code", "That code is not right. Check the app and try again.");
    }

    private async Task<PiranhaCommerceUser> FindAsync(Guid userId) =>
        await users.FindByIdAsync(userId.ToString()) is { Active: true } user ? user : throw new PiranhaCommerceConflictException("That account is not available.");

    private static string Clean(string? code) => (code ?? "").Replace(" ", "").Replace("-", "");

    private static string Spaced(string key) =>
        string.Join(' ', Enumerable.Range(0, (key.Length + 3) / 4).Select(i => key.Substring(i * 4, Math.Min(4, key.Length - i * 4)))).ToLower(CultureInfo.InvariantCulture);

    private async Task RecordAsync(PiranhaCommerceUser user, string what, string property, string? from, string to, CancellationToken ct)
    {
        if (services.GetService<IAuditTrail>() is { } trail)
            await trail.RecordUpdateAsync("StaffUser", user.Email ?? user.Id.ToString(), $"{user.Email} {what}", [new AuditChange(property, from, to)], ct);
    }
}
