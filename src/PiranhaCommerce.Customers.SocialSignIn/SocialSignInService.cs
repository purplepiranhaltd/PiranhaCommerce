using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using PiranhaCommerce.Data;

namespace PiranhaCommerce.Customers.SocialSignIn;

public enum SocialOutcome
{
    /// <summary>Signed in (or, for linking, linked).</summary>
    SignedIn,
    UnknownProvider,
    InvalidToken,
    NoEmail,
    AccountExists,
    SignUpDisabled,
    EmailNotVerifiedByProvider,
    TwoFactorRequired,
    TwoFactorSetupRequired,
    EmailNotVerified,
    AlreadyLinked,
    ProviderAlreadyLinked,
    Failed,
}

/// <param name="SignIn">For <see cref="SocialOutcome.SignedIn"/> and the two-factor outcomes: who, and the setup token when one is needed.</param>
/// <param name="Linked">For a successful link.</param>
/// <param name="Message">A sentence a shopper can be shown, for the outcomes that have one.</param>
/// <param name="Unchanged">For a link: that account was already linked to this customer, so nothing was added.</param>
public sealed record SocialSignInOutcome(SocialOutcome Outcome, SignInResult? SignIn = null, string? Message = null, LinkedIdentityDto? Linked = null, bool Unchanged = false);

/// <summary>
/// Signing in with, linking and unlinking social accounts. The storefront API uses this, and so can a server-rendered shop that
/// receives the provider's ID token itself (as the Petalwick sample does).
/// </summary>
public interface ISocialSignInService
{
    /// <summary>What the shop offers, by name.</summary>
    IReadOnlyList<ExternalProviderDto> Providers { get; }

    /// <summary>Signs in with a provider's ID token. See the shoppers guide for which outcome means what. Never throws for a bad token.</summary>
    Task<SocialSignInOutcome> SignInAsync(string provider, string idToken, string? nonce, string? code, CancellationToken ct = default);

    /// <summary>Adds a social account to a customer who is already signed in. Idempotent for the same account.</summary>
    Task<SocialSignInOutcome> LinkAsync(Guid customerId, string provider, string idToken, string? nonce, CancellationToken ct = default);

    Task<bool> UnlinkAsync(Guid customerId, string provider, CancellationToken ct = default);

    Task<IReadOnlyList<LinkedIdentityDto>> LinkedAsync(Guid customerId, CancellationToken ct = default);
}

/// <summary>What staff can do about a customer's linked social accounts.</summary>
public interface ISocialSignInAdminService
{
    Task<IReadOnlyList<LinkedIdentityDto>> ListAsync(Guid customerId, CancellationToken ct = default);

    /// <summary>Unlinks it, so that social account can no longer sign in. Recorded in their sign-in history. False when it was not linked.</summary>
    [PiranhaCommerce.Core.Identity.RequiresPermission(PiranhaCommerce.Core.Identity.Permissions.CustomersManage)]
    Task<bool> UnlinkAsync(Guid customerId, string provider, string? actor = null, CancellationToken ct = default);
}

internal sealed class SocialSignInAdminService(PiranhaCommerceDbContext db, ICustomerExternalAccess access) : ISocialSignInAdminService
{
    public async Task<IReadOnlyList<LinkedIdentityDto>> ListAsync(Guid customerId, CancellationToken ct = default)
        => await db.Set<CustomerExternalLogin>().AsNoTracking().Where(l => l.CustomerId == customerId).OrderBy(l => l.Provider)
            .Select(l => new LinkedIdentityDto(l.Provider, l.Email, l.LinkedUtc, l.LastUsedUtc)).ToListAsync(ct);

    public async Task<bool> UnlinkAsync(Guid customerId, string provider, string? actor = null, CancellationToken ct = default)
    {
        var name = provider.ToLowerInvariant();
        if (await db.Set<CustomerExternalLogin>().Where(l => l.CustomerId == customerId && l.Provider == name).ExecuteDeleteAsync(ct) == 0) return false;
        await access.RecordByStaffAsync(customerId, "social-unlinked", actor, name, ct);
        return true;
    }
}

internal sealed class SocialSignInService(IOptions<SocialSignInOptions> options, IExternalIdentityVerifier verifier, ICustomerExternalAccess access, PiranhaCommerceDbContext db) : ISocialSignInService
{
    private const string AccountExistsMessage = "There is already an account with that email. Sign in the usual way, then add this social account from your profile.";
    private const string AlreadyLinkedMessage = "That social account is already linked to another account here.";

    public IReadOnlyList<ExternalProviderDto> Providers =>
        options.Value.Providers.Select(p => new ExternalProviderDto(p.Key.ToLowerInvariant(), p.Value.DisplayName ?? p.Key)).OrderBy(p => p.Name).ToList();

    public async Task<SocialSignInOutcome> SignInAsync(string provider, string idToken, string? nonce, string? code, CancellationToken ct = default)
    {
        var o = options.Value;
        var name = provider.ToLowerInvariant();
        if (!o.Providers.TryGetValue(name, out var p)) return new(SocialOutcome.UnknownProvider);
        if (await verifier.VerifyAsync(p, idToken, nonce, ct) is not { } who) return new(SocialOutcome.InvalidToken);

        var link = await db.Set<CustomerExternalLogin>().FirstOrDefaultAsync(l => l.Provider == name && l.Subject == who.Subject, ct);
        Guid customerId;
        var needsLink = false;
        if (link is not null)
        {
            customerId = link.CustomerId;
        }
        else
        {
            // Not linked to anyone. The address is only ever used to decide what is ALLOWED, never to find an account without the shop's say-so.
            if (string.IsNullOrWhiteSpace(who.Email))
                return new(SocialOutcome.NoEmail, Message: "That social account did not share an email address, which this shop needs.");

            if (await access.FindByEmailAsync(who.Email, ct) is { } existing)
            {
                if (o.AccountMatching != SocialAccountMatching.LinkVerifiedEmail || !who.EmailVerified) return new(SocialOutcome.AccountExists, Message: AccountExistsMessage);
                // The shop trusts this provider's verification. Whoever registered the address without proving it loses their password and sessions.
                if (!(existing.HasSignIn && existing.EmailVerified)) await access.AdoptAsync(existing.Id, ct);
                customerId = existing.Id;
            }
            else
            {
                if (!o.AllowSignUp) return new(SocialOutcome.SignUpDisabled, Message: "No account here is linked to that social account.");
                if (!who.EmailVerified) return new(SocialOutcome.EmailNotVerifiedByProvider, Message: "That social account has not confirmed its email address, so we cannot create an account from it.");
                customerId = (await access.CreateAsync(who.Email, who.FirstName, who.LastName, emailVerified: true, ct)).Id;
            }
            needsLink = true;
        }

        var result = await access.SignInAsync(customerId, code, ct);
        switch (result.Status)
        {
            case SignInStatus.Unverified: return new(SocialOutcome.EmailNotVerified);
            case SignInStatus.TwoFactorRequired:
                // Their shop password is not needed, but the shop's second step is: a stolen social login alone does not get in.
                return new(SocialOutcome.TwoFactorRequired, Message: "This account uses two-factor sign-in. Send the code from the authenticator app (or a recovery code) as \"code\" with the same ID token.");
            case SignInStatus.Valid or SignInStatus.TwoFactorSetupRequired when needsLink:
                if (await TryLinkAsync(customerId, name, who, ct) is null) return new(SocialOutcome.AlreadyLinked, Message: AlreadyLinkedMessage);
                await access.RecordAsync(customerId, "social-linked", name, ct);
                break;
        }
        if (result.Status == SignInStatus.TwoFactorSetupRequired)
            return new(SocialOutcome.TwoFactorSetupRequired, result, "This shop requires two-factor sign-in. Use the setup token at /auth/two-factor/setup and /auth/two-factor/enable to set up an authenticator app and sign in.");
        if (result is not { Status: SignInStatus.Valid, CustomerId: { } id }) return new(SocialOutcome.Failed);

        await db.Set<CustomerExternalLogin>().Where(l => l.CustomerId == id && l.Provider == name).ExecuteUpdateAsync(s => s.SetProperty(l => l.LastUsedUtc, (DateTimeOffset?)DateTimeOffset.UtcNow), ct);
        await access.RecordAsync(id, "social-sign-in", name, ct);
        return new(SocialOutcome.SignedIn, result);
    }

    public async Task<SocialSignInOutcome> LinkAsync(Guid customerId, string provider, string idToken, string? nonce, CancellationToken ct = default)
    {
        var name = provider.ToLowerInvariant();
        if (!options.Value.Providers.TryGetValue(name, out var p)) return new(SocialOutcome.UnknownProvider);
        if (await verifier.VerifyAsync(p, idToken, nonce, ct) is not { } who) return new(SocialOutcome.InvalidToken);

        var existing = await db.Set<CustomerExternalLogin>().FirstOrDefaultAsync(l => l.Provider == name && l.Subject == who.Subject, ct);
        if (existing is not null)
            return existing.CustomerId == customerId ? new(SocialOutcome.SignedIn, Linked: ToDto(existing), Unchanged: true) // already done: saying so again changes nothing
                : new(SocialOutcome.AlreadyLinked, Message: AlreadyLinkedMessage);
        if (await db.Set<CustomerExternalLogin>().AnyAsync(l => l.CustomerId == customerId && l.Provider == name, ct))
            return new(SocialOutcome.ProviderAlreadyLinked, Message: "You already have an account linked from this provider. Remove it first.");

        var link = await TryLinkAsync(customerId, name, who, ct);
        if (link is null) return new(SocialOutcome.AlreadyLinked, Message: AlreadyLinkedMessage);
        await access.RecordAsync(customerId, "social-linked", name, ct);
        return new(SocialOutcome.SignedIn, Linked: ToDto(link));
    }

    public async Task<bool> UnlinkAsync(Guid customerId, string provider, CancellationToken ct = default)
    {
        var name = provider.ToLowerInvariant();
        if (await db.Set<CustomerExternalLogin>().Where(l => l.CustomerId == customerId && l.Provider == name).ExecuteDeleteAsync(ct) == 0) return false;
        await access.RecordAsync(customerId, "social-unlinked", name, ct);
        return true;
    }

    public async Task<IReadOnlyList<LinkedIdentityDto>> LinkedAsync(Guid customerId, CancellationToken ct = default)
        => await db.Set<CustomerExternalLogin>().AsNoTracking().Where(l => l.CustomerId == customerId).OrderBy(l => l.Provider)
            .Select(l => new LinkedIdentityDto(l.Provider, l.Email, l.LinkedUtc, l.LastUsedUtc)).ToListAsync(ct);

    private static LinkedIdentityDto ToDto(CustomerExternalLogin l) => new(l.Provider, l.Email, l.LinkedUtc, l.LastUsedUtc);

    private async Task<CustomerExternalLogin?> TryLinkAsync(Guid customerId, string provider, ExternalIdentity who, CancellationToken ct)
    {
        var link = new CustomerExternalLogin { CustomerId = customerId, Provider = provider, Subject = who.Subject, Email = who.Email, LinkedUtc = DateTimeOffset.UtcNow };
        db.Add(link);
        try { await db.SaveChangesAsync(ct); return link; }
        catch (DbUpdateException) { db.ChangeTracker.Clear(); return null; } // lost a race on one of the unique indexes
    }
}
