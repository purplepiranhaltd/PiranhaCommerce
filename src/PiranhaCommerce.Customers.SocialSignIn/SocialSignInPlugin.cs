using System.Security.Claims;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using PiranhaCommerce.Api;
using PiranhaCommerce.Api.Auth;
using PiranhaCommerce.Core;
using PiranhaCommerce.Core.Plugins;
using PiranhaCommerce.Core.Validation;
using PiranhaCommerce.Data;

namespace PiranhaCommerce.Customers.SocialSignIn;

/// <summary>
/// Lets shoppers sign in with a Google, Microsoft, Apple or other OpenID Connect account. The app signs the shopper in at the provider and
/// sends the ID token here; the shop checks it and answers with its own token. Needs Customers. Does nothing until a provider is configured.
/// </summary>
public sealed class SocialSignInPlugin : IPiranhaCommercePlugin
{
    public PluginInfo Info { get; } = new("PiranhaCommerce.Customers.SocialSignIn", "Social sign-in", "0.1.0", "Shopper sign-in with Google, Microsoft, Apple or any OpenID Connect provider.");

    public IReadOnlyList<IPiranhaCommercePlugin> Requires { get; } = [new CustomersPlugin()];

    public void ConfigureServices(IServiceCollection services)
    {
        services.AddOptions<SocialSignInOptions>().BindConfiguration(SocialSignInOptions.SectionName).ValidateOnStart();
        services.AddSingleton<IValidateOptions<SocialSignInOptions>, SocialSignInOptionsValidator>();
        services.TryAddSingleton<IOidcConfigurationSource, HttpOidcConfigurationSource>();
        services.TryAddSingleton<IExternalIdentityVerifier, OidcIdentityVerifier>();
        services.AddSingleton<IModelContributor, SocialModelContributor>();
        services.AddSingleton<IStorefrontEndpointContributor, SocialStorefrontEndpoints>();
        services.AddScoped<ICustomerDataContributor, SocialDataContributor>();
    }
}

internal sealed class SocialModelContributor : IModelContributor
{
    public void OnModelCreating(Microsoft.EntityFrameworkCore.ModelBuilder b)
    {
        b.Entity<CustomerExternalLogin>(e =>
        {
            e.ToTable("CustomerExternalLogin", "customer");
            e.Property(x => x.Provider).HasMaxLength(40).IsRequired();
            e.Property(x => x.Subject).HasMaxLength(256).IsRequired();
            e.Property(x => x.Email).HasMaxLength(256);
            e.HasIndex(x => new { x.Provider, x.Subject }).IsUnique(); // one social account belongs to one customer
            e.HasIndex(x => new { x.CustomerId, x.Provider }).IsUnique(); // and a customer has one account at each provider
            e.HasOne<Customer>().WithMany().HasForeignKey(x => x.CustomerId).OnDelete(DeleteBehavior.Cascade);
        });
    }
}

internal sealed class SocialStorefrontEndpoints : IStorefrontEndpointContributor
{
    public void MapEndpoints(IEndpointRouteBuilder storefront)
    {
        var auth = storefront.MapGroup("/auth/external").WithTags("Storefront social sign-in");

        // What the shop offers, so an app knows which buttons to show.
        auth.MapGet("/", (IOptions<SocialSignInOptions> o) =>
                Results.Ok(o.Value.Providers.Select(p => new ExternalProviderDto(p.Key.ToLowerInvariant(), p.Value.DisplayName ?? p.Key)).OrderBy(p => p.Name)))
            .AllowAnonymous();

        auth.MapPost("/{provider}", SignInAsync).AllowAnonymous();

        var me = storefront.MapGroup("/me/external").WithTags("Storefront social sign-in").AddEndpointFilter<ActiveCustomerFilter>();

        me.MapGet("/", async (ClaimsPrincipal user, PiranhaCommerceDbContext db, CancellationToken ct) =>
        {
            var id = user.CustomerId()!.Value;
            return Results.Ok(await db.Set<CustomerExternalLogin>().AsNoTracking().Where(l => l.CustomerId == id).OrderBy(l => l.Provider)
                .Select(l => new LinkedIdentityDto(l.Provider, l.Email, l.LinkedUtc, l.LastUsedUtc)).ToListAsync(ct));
        });

        // Adds a social account to the signed-in shopper. Nothing about the two emails is compared: the shopper is proven by their shop session
        // and the social account by its token, and the provider's own id is what gets stored.
        me.MapPost("/{provider}", async (string provider, ExternalLinkInput input, ClaimsPrincipal user, IOptions<SocialSignInOptions> o, IExternalIdentityVerifier verifier,
            ICustomerExternalAccess access, PiranhaCommerceDbContext db, CancellationToken ct) =>
        {
            ModelValidator.ValidateOrThrow(input);
            var name = provider.ToLowerInvariant();
            if (!o.Value.Providers.TryGetValue(name, out var p)) return Results.NotFound(new { error = "unknown_provider" });
            if (await verifier.VerifyAsync(p, input.IdToken, input.Nonce, ct) is not { } who) return Results.Json(new { error = "invalid_token" }, statusCode: StatusCodes.Status401Unauthorized);

            var customerId = user.CustomerId()!.Value;
            var existing = await db.Set<CustomerExternalLogin>().FirstOrDefaultAsync(l => l.Provider == name && l.Subject == who.Subject, ct);
            if (existing is not null)
                return existing.CustomerId == customerId ? Results.Ok(ToDto(existing)) // already done: saying so again changes nothing
                    : Results.Json(new { error = "already_linked", message = "That social account is already linked to another account here." }, statusCode: StatusCodes.Status409Conflict);
            if (await db.Set<CustomerExternalLogin>().AnyAsync(l => l.CustomerId == customerId && l.Provider == name, ct))
                return Results.Json(new { error = "provider_already_linked", message = "You already have an account linked from this provider. Remove it first." }, statusCode: StatusCodes.Status409Conflict);

            var link = await TryLinkAsync(db, customerId, name, who, ct);
            if (link is null) return Results.Json(new { error = "already_linked", message = "That social account is already linked to another account here." }, statusCode: StatusCodes.Status409Conflict);
            await access.RecordAsync(customerId, "social-linked", name, ct);
            return Results.Created("/api/v1/storefront/me/external", ToDto(link));
        });

        // Removing one is always allowed: the shopper can always choose a password with "forgot password", their address being the proof.
        me.MapDelete("/{provider}", async (string provider, ClaimsPrincipal user, ICustomerExternalAccess access, PiranhaCommerceDbContext db, CancellationToken ct) =>
        {
            var customerId = user.CustomerId()!.Value;
            var name = provider.ToLowerInvariant();
            if (await db.Set<CustomerExternalLogin>().Where(l => l.CustomerId == customerId && l.Provider == name).ExecuteDeleteAsync(ct) == 0) return Results.NotFound();
            await access.RecordAsync(customerId, "social-unlinked", name, ct);
            return Results.NoContent();
        });
    }

    private static LinkedIdentityDto ToDto(CustomerExternalLogin l) => new(l.Provider, l.Email, l.LinkedUtc, l.LastUsedUtc);

    private static async Task<CustomerExternalLogin?> TryLinkAsync(PiranhaCommerceDbContext db, Guid customerId, string provider, ExternalIdentity who, CancellationToken ct)
    {
        var link = new CustomerExternalLogin { CustomerId = customerId, Provider = provider, Subject = who.Subject, Email = who.Email, LinkedUtc = DateTimeOffset.UtcNow };
        db.Add(link);
        try { await db.SaveChangesAsync(ct); return link; }
        catch (DbUpdateException) { db.ChangeTracker.Clear(); return null; } // lost a race on one of the unique indexes
    }

    private static async Task<IResult> SignInAsync(string provider, ExternalSignInInput input, IOptions<SocialSignInOptions> options, IExternalIdentityVerifier verifier,
        ICustomerExternalAccess access, ICustomerTokenIssuer tokens, ICustomerRefreshService refresh, PiranhaCommerceDbContext db, HttpContext http, CancellationToken ct)
    {
        ModelValidator.ValidateOrThrow(input);
        var o = options.Value;
        var name = provider.ToLowerInvariant();
        if (!o.Providers.TryGetValue(name, out var p)) return Results.NotFound(new { error = "unknown_provider" });
        if (await verifier.VerifyAsync(p, input.IdToken, input.Nonce, ct) is not { } who) return Results.Json(new { error = "invalid_token" }, statusCode: StatusCodes.Status401Unauthorized);

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
                return Results.Json(new { error = "no_email", message = "That social account did not share an email address, which this shop needs." }, statusCode: StatusCodes.Status400BadRequest);

            if (await access.FindByEmailAsync(who.Email, ct) is { } existing)
            {
                if (o.AccountMatching != SocialAccountMatching.LinkVerifiedEmail || !who.EmailVerified)
                    return Results.Json(new { error = "account_exists", message = "There is already an account with that email. Sign in the usual way, then add this social account from your profile." }, statusCode: StatusCodes.Status409Conflict);
                // The shop trusts this provider's verification. Whoever registered the address without proving it loses their password and sessions.
                if (!(existing.HasSignIn && existing.EmailVerified)) await access.AdoptAsync(existing.Id, ct);
                customerId = existing.Id;
            }
            else
            {
                if (!o.AllowSignUp) return Results.Json(new { error = "sign_up_disabled", message = "No account here is linked to that social account." }, statusCode: StatusCodes.Status403Forbidden);
                if (!who.EmailVerified) return Results.Json(new { error = "email_not_verified_by_provider", message = "That social account has not confirmed its email address, so we cannot create an account from it." }, statusCode: StatusCodes.Status403Forbidden);
                customerId = (await access.CreateAsync(who.Email, who.FirstName, who.LastName, emailVerified: true, ct)).Id;
            }
            needsLink = true;
        }

        var result = await access.SignInAsync(customerId, input.Code, ct);
        switch (result.Status)
        {
            case SignInStatus.Unverified: return Results.Json(new { error = "email_not_verified" }, statusCode: StatusCodes.Status403Forbidden);
            case SignInStatus.TwoFactorRequired:
                // Their shop password is not needed, but the shop's second step is: a stolen social login alone does not get in.
                return Results.Json(new { error = "two_factor_required", message = "This account uses two-factor sign-in. Send the code from the authenticator app (or a recovery code) as \"code\" with the same ID token." }, statusCode: StatusCodes.Status401Unauthorized);
            case SignInStatus.TwoFactorSetupRequired when needsLink:
            case SignInStatus.Valid when needsLink:
                if (await TryLinkAsync(db, customerId, name, who, ct) is null) return Results.Json(new { error = "already_linked" }, statusCode: StatusCodes.Status409Conflict);
                await access.RecordAsync(customerId, "social-linked", name, ct);
                break;
        }
        if (result.Status == SignInStatus.TwoFactorSetupRequired)
            return Results.Json(new { error = "two_factor_setup_required", setupToken = result.SetupToken, message = "This shop requires two-factor sign-in. Use the setup token at /auth/two-factor/setup and /auth/two-factor/enable to set up an authenticator app and sign in." }, statusCode: StatusCodes.Status403Forbidden);
        if (result is not { Status: SignInStatus.Valid, CustomerId: { } id, Email: { } email }) return Results.Unauthorized();

        await db.Set<CustomerExternalLogin>().Where(l => l.CustomerId == id && l.Provider == name).ExecuteUpdateAsync(s => s.SetProperty(l => l.LastUsedUtc, (DateTimeOffset?)DateTimeOffset.UtcNow), ct);
        await access.RecordAsync(id, "social-sign-in", name, ct);
        var issued = await refresh.IssueRefreshTokenAsync(id, result.SessionVersion, http.Request.Headers.UserAgent.ToString(), ct);
        return Results.Ok(tokens.Issue(id, email, result.SessionVersion, issued?.SessionId) with { RefreshToken = issued?.Token });
    }
}

/// <summary>Linked social accounts in a customer's export, and gone when they are erased.</summary>
internal sealed class SocialDataContributor(PiranhaCommerceDbContext db) : ICustomerDataContributor
{
    public string Name => "social-sign-in";

    public Task<IReadOnlyList<string>> CheckErasureAsync(CustomerSubject subject, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<string>>([]);

    public async Task<object?> ExportAsync(CustomerSubject subject, CancellationToken ct = default)
    {
        var rows = await db.Set<CustomerExternalLogin>().AsNoTracking().Where(l => l.CustomerId == subject.CustomerId)
            .Select(l => new LinkedIdentityDto(l.Provider, l.Email, l.LinkedUtc, l.LastUsedUtc)).ToListAsync(ct);
        return rows.Count == 0 ? null : rows;
    }

    public async Task<int> EraseAsync(CustomerSubject subject, CancellationToken ct = default)
        => await db.Set<CustomerExternalLogin>().Where(l => l.CustomerId == subject.CustomerId).ExecuteDeleteAsync(ct);
}
