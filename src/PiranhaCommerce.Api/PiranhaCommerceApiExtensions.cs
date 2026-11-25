using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using System.Security.Cryptography;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using PiranhaCommerce.Api.Auth;
using PiranhaCommerce.Api.RateLimiting;
using PiranhaCommerce.Core.Identity;
using PiranhaCommerce.Core.Plugins;

namespace PiranhaCommerce.Api;

/// <summary>Authorization policy names for API endpoints.</summary>
public static class ApiPolicies
{
    /// <summary>Any authenticated API caller.</summary>
    public const string Read = "PiranhaCommerce.Api.Read";

    /// <summary>Administrators only: use for create/update/delete.</summary>
    public const string Write = "PiranhaCommerce.Api.Write";

    /// <summary>A signed-in shopper (storefront API only). A staff token does not satisfy it.</summary>
    public const string Customer = "PiranhaCommerce.Api.Customer";
}

public static class PiranhaCommerceApiExtensions
{
    public const string JwtScheme = "PiranhaCommerceJwt";
    public const string CustomerJwtScheme = "PiranhaCommerceCustomerJwt";

    public static PiranhaCommerceBuilder AddPiranhaCommerceApi(this PiranhaCommerceBuilder builder)
    {
        var services = builder.Services;
        services.AddOpenApi();
        services.AddOptions<PiranhaCommerceJwtOptions>().BindConfiguration(PiranhaCommerceJwtOptions.SectionName)
            // No signing key: in Development make a temporary one, so a new project works without setup (tokens stop working when the
            // app restarts). Anywhere else the app refuses to start, so a missing key is found at deployment and not on the first sign-in.
            .PostConfigure<IHostEnvironment, ILoggerFactory>((o, env, logs) =>
            {
                if (!string.IsNullOrEmpty(o.SigningKey) || !env.IsDevelopment()) return;
                o.SigningKey = Convert.ToBase64String(RandomNumberGenerator.GetBytes(48));
                logs.CreateLogger("PiranhaCommerce.Api").LogWarning(
                    "{Setting} is not set, so a temporary key is being used and API tokens stop working when the app restarts. Set it (32 or more characters, from a secret store) for a fixed key.",
                    $"{PiranhaCommerceJwtOptions.SectionName}:SigningKey");
            })
            .Validate(o => o.SigningKey is { Length: >= 32 },
                $"{PiranhaCommerceJwtOptions.SectionName}:SigningKey must be set to a secret of 32 or more characters (keep it in a secret store, not in a file). Without it the API cannot sign tokens.")
            .ValidateOnStart();
        services.AddScoped<JwtTokenService>();
        services.AddSignInRateLimiting();

        // Enums travel as their names ("Placed"), not numbers, so clients and stored documents stay readable and stable.
        services.ConfigureHttpJsonOptions(o => o.SerializerOptions.Converters.Add(new System.Text.Json.Serialization.JsonStringEnumConverter()));

        // Registered without a default scheme: the admin area owns the cookie default. The key is read
        // lazily (on first token validation) so design-time tooling can run without secrets.
        services.AddAuthentication().AddJwtBearer(JwtScheme, _ => { });
        services.AddOptions<JwtBearerOptions>(JwtScheme).Configure<IOptions<PiranhaCommerceJwtOptions>>((o, jwt) =>
        {
            o.MapInboundClaims = false;
            o.TokenValidationParameters = new TokenValidationParameters
            {
                ValidIssuer = jwt.Value.Issuer,
                ValidAudience = jwt.Value.Audience,
                IssuerSigningKey = JwtTokenService.GetKey(jwt.Value),
                NameClaimType = "sub",
                RoleClaimType = "role",
                ClockSkew = TimeSpan.FromMinutes(1),
            };
            // An API key is accepted on the same scheme, as "Authorization: Bearer pck_..." or "X-Api-Key: pck_...". Anything else is a token.
            o.Events = new JwtBearerEvents { OnMessageReceived = ApiKeySignIn.AcceptAsync };
        });

        // Shoppers: own scheme, own audience and own key, so a shopper token cannot open the staff API (or the reverse).
        services.AddSingleton<ICustomerTokenIssuer, CustomerTokenIssuer>();
        services.AddAuthentication().AddJwtBearer(CustomerJwtScheme, _ => { });
        services.AddOptions<JwtBearerOptions>(CustomerJwtScheme).Configure<IOptions<PiranhaCommerceJwtOptions>>((o, jwt) =>
        {
            o.MapInboundClaims = false;
            o.TokenValidationParameters = new TokenValidationParameters
            {
                ValidIssuer = jwt.Value.Issuer,
                ValidAudience = jwt.Value.CustomerAudience,
                IssuerSigningKey = CustomerTokenIssuer.GetKey(jwt.Value),
                NameClaimType = "sub",
                ClockSkew = TimeSpan.FromMinutes(1),
            };
        });

        services.AddAuthorizationBuilder()
            .AddPolicy(ApiPolicies.Customer, p => p.AddAuthenticationSchemes(CustomerJwtScheme).RequireAuthenticatedUser())
            .AddPolicy(ApiPolicies.Read, p => p.AddAuthenticationSchemes(JwtScheme).RequireAuthenticatedUser())
            .AddPolicy(ApiPolicies.Write, p => p.AddAuthenticationSchemes(JwtScheme).RequireRole(PiranhaCommerceRoles.Administrator));
        return builder;
    }

    /// <summary>
    /// Maps <c>POST {prefix}/auth/token</c> (anonymous) and every contributor's endpoints. The group requires
    /// a valid JWT (401, never a login redirect); contributors tighten writes with <see cref="ApiPolicies.Write"/>.
    /// </summary>
    public static IEndpointRouteBuilder MapPiranhaCommerceApi(this IEndpointRouteBuilder app, string prefix = "/api/v1")
    {
        var root = app.MapGroup(prefix);

        root.MapPost("/auth/token", async (TokenRequest request, JwtTokenService tokens) =>
            {
                var (token, status) = await tokens.IssueWithStatusAsync(request);
                if (token is not null) return Results.Ok(token);
                // The password was right but the person has two-factor on: say so, so the caller can ask for the code and send it as "code".
                if (status == CredentialStatus.TwoFactorSetupRequired) return Results.Json(new { error = "two_factor_setup_required", message = "Two-factor sign-in is compulsory for staff and this account has not set it up. Sign in to the admin, open My account > Security and set up an authenticator app. For local development only, set PiranhaCommerce:Staff:RequireTwoFactor to false." }, statusCode: StatusCodes.Status403Forbidden);
                return status == CredentialStatus.TwoFactorRequired ? Results.Json(new { error = "two_factor_required", message = "This account uses two-factor sign-in. Send the code from the authenticator app (or a recovery code) as \"code\" with the email and password." }, statusCode: StatusCodes.Status401Unauthorized) : Results.Unauthorized();
            })
            .AllowAnonymous().WithTags("Auth");

        var api = root.MapGroup("").RequireAuthorization(ApiPolicies.Read).AddEndpointFilter<AuditActorFilter>().AddEndpointFilter<PiranhaCommerceExceptionFilter>();
        ((IEndpointConventionBuilder)api).Finally(PermissionEndpointExtensions.RequireAdministratorUnlessPermissionNamed); // an endpoint that names no permission is for administrators only
        foreach (var c in app.ServiceProvider.GetServices<IApiEndpointContributor>())
            c.MapEndpoints(api);

        // Shopper endpoints: closed unless a shopper token is presented; anonymous ones (register, sign in) opt out one by one.
        var storefront = root.MapGroup("/storefront").RequireAuthorization(ApiPolicies.Customer).AddEndpointFilter<AuditActorFilter>().AddEndpointFilter<PiranhaCommerceExceptionFilter>();
        foreach (var c in app.ServiceProvider.GetServices<IStorefrontEndpointContributor>())
            c.MapEndpoints(storefront);

        app.MapOpenApi().AllowAnonymous();
        return app;
    }
}
