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
using PiranhaCommerce.Core.Identity;
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
        services.AddScoped<ISocialSignInService, SocialSignInService>();
        services.AddScoped<ISocialSignInAdminService, SocialSignInAdminService>();
        services.AddSingleton<IApiEndpointContributor, SocialApiEndpoints>();
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

internal sealed class SocialApiEndpoints : IApiEndpointContributor
{
    public void MapEndpoints(IEndpointRouteBuilder api)
    {
        var customers = api.MapGroup("/customers").WithTags("Customers").RequirePermission(Permissions.CustomersView);

        customers.MapGet("/{id:guid}/social", async (Guid id, ISocialSignInAdminService svc, PiranhaCommerceDbContext db, CancellationToken ct) =>
            await db.Set<Customer>().AnyAsync(c => c.Id == id, ct) ? Results.Ok(await svc.ListAsync(id, ct)) : Results.NotFound());

        customers.MapDelete("/{id:guid}/social/{provider}", async (Guid id, string provider, HttpContext http, ISocialSignInAdminService svc, CancellationToken ct) =>
                await svc.UnlinkAsync(id, provider, http.User.FindFirst("email")?.Value, ct) ? Results.NoContent() : Results.NotFound())
            .RequirePermission(Permissions.CustomersManage);
    }
}

internal sealed class SocialStorefrontEndpoints : IStorefrontEndpointContributor
{
    public void MapEndpoints(IEndpointRouteBuilder storefront)
    {
        var auth = storefront.MapGroup("/auth/external").WithTags("Storefront social sign-in");

        // What the shop offers, so an app knows which buttons to show.
        auth.MapGet("/", (ISocialSignInService social) => Results.Ok(social.Providers)).AllowAnonymous();

        auth.MapPost("/{provider}", async (string provider, ExternalSignInInput input, ISocialSignInService social, ICustomerTokenIssuer tokens, ICustomerRefreshService refresh, HttpContext http, CancellationToken ct) =>
        {
            ModelValidator.ValidateOrThrow(input);
            var r = await social.SignInAsync(provider, input.IdToken, input.Nonce, input.Code, ct);
            if (r is { Outcome: SocialOutcome.SignedIn, SignIn: { CustomerId: { } id, Email: { } email } si })
            {
                var issued = await refresh.IssueRefreshTokenAsync(id, si.SessionVersion, http.Request.Headers.UserAgent.ToString(), ct);
                return Results.Ok(tokens.Issue(id, email, si.SessionVersion, issued?.SessionId) with { RefreshToken = issued?.Token });
            }
            return Failure(r);
        }).AllowAnonymous();

        var me = storefront.MapGroup("/me/external").WithTags("Storefront social sign-in").AddEndpointFilter<ActiveCustomerFilter>();

        me.MapGet("/", async (ClaimsPrincipal user, ISocialSignInService social, CancellationToken ct) => Results.Ok(await social.LinkedAsync(user.CustomerId()!.Value, ct)));

        // Adds a social account to the signed-in shopper. Nothing about the two emails is compared: the shopper is proven by their shop session
        // and the social account by its token, and the provider's own id is what gets stored.
        me.MapPost("/{provider}", async (string provider, ExternalLinkInput input, ClaimsPrincipal user, ISocialSignInService social, CancellationToken ct) =>
        {
            ModelValidator.ValidateOrThrow(input);
            var r = await social.LinkAsync(user.CustomerId()!.Value, provider, input.IdToken, input.Nonce, ct);
            return r.Outcome != SocialOutcome.SignedIn ? Failure(r) : r.Unchanged ? Results.Ok(r.Linked) : Results.Created("/api/v1/storefront/me/external", r.Linked);
        });

        // Removing one is always allowed: the shopper can always choose a password with "forgot password", their address being the proof.
        me.MapDelete("/{provider}", async (string provider, ClaimsPrincipal user, ISocialSignInService social, CancellationToken ct) =>
            await social.UnlinkAsync(user.CustomerId()!.Value, provider, ct) ? Results.NoContent() : Results.NotFound());
    }

    private static IResult Failure(SocialSignInOutcome r) => r.Outcome switch
    {
        SocialOutcome.UnknownProvider => Results.NotFound(new { error = "unknown_provider" }),
        SocialOutcome.InvalidToken => Results.Json(new { error = "invalid_token" }, statusCode: StatusCodes.Status401Unauthorized),
        SocialOutcome.NoEmail => Results.Json(new { error = "no_email", message = r.Message }, statusCode: StatusCodes.Status400BadRequest),
        SocialOutcome.AccountExists => Results.Json(new { error = "account_exists", message = r.Message }, statusCode: StatusCodes.Status409Conflict),
        SocialOutcome.AlreadyLinked => Results.Json(new { error = "already_linked", message = r.Message }, statusCode: StatusCodes.Status409Conflict),
        SocialOutcome.ProviderAlreadyLinked => Results.Json(new { error = "provider_already_linked", message = r.Message }, statusCode: StatusCodes.Status409Conflict),
        SocialOutcome.SignUpDisabled => Results.Json(new { error = "sign_up_disabled", message = r.Message }, statusCode: StatusCodes.Status403Forbidden),
        SocialOutcome.EmailNotVerifiedByProvider => Results.Json(new { error = "email_not_verified_by_provider", message = r.Message }, statusCode: StatusCodes.Status403Forbidden),
        SocialOutcome.EmailNotVerified => Results.Json(new { error = "email_not_verified" }, statusCode: StatusCodes.Status403Forbidden),
        SocialOutcome.TwoFactorRequired => Results.Json(new { error = "two_factor_required", message = r.Message }, statusCode: StatusCodes.Status401Unauthorized),
        SocialOutcome.TwoFactorSetupRequired => Results.Json(new { error = "two_factor_setup_required", setupToken = r.SignIn?.SetupToken, message = r.Message }, statusCode: StatusCodes.Status403Forbidden),
        _ => Results.Unauthorized(),
    };
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
