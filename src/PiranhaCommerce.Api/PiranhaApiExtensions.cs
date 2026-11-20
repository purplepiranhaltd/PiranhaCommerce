using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
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
    public const string Read = "Piranha.Api.Read";

    /// <summary>Administrators only: use for create/update/delete.</summary>
    public const string Write = "Piranha.Api.Write";
}

public static class PiranhaApiExtensions
{
    public const string JwtScheme = "PiranhaJwt";

    public static PiranhaBuilder AddPiranhaApi(this PiranhaBuilder builder)
    {
        var services = builder.Services;
        services.AddOpenApi();
        services.AddOptions<PiranhaJwtOptions>().BindConfiguration(PiranhaJwtOptions.SectionName);
        services.AddScoped<JwtTokenService>();
        services.AddSignInRateLimiting();

        // Enums travel as their names ("Placed"), not numbers, so clients and stored documents stay readable and stable.
        services.ConfigureHttpJsonOptions(o => o.SerializerOptions.Converters.Add(new System.Text.Json.Serialization.JsonStringEnumConverter()));

        // Registered without a default scheme: the admin area owns the cookie default. The key is read
        // lazily (on first token validation) so design-time tooling can run without secrets.
        services.AddAuthentication().AddJwtBearer(JwtScheme, _ => { });
        services.AddOptions<JwtBearerOptions>(JwtScheme).Configure<IOptions<PiranhaJwtOptions>>((o, jwt) =>
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
        });

        services.AddAuthorizationBuilder()
            .AddPolicy(ApiPolicies.Read, p => p.AddAuthenticationSchemes(JwtScheme).RequireAuthenticatedUser())
            .AddPolicy(ApiPolicies.Write, p => p.AddAuthenticationSchemes(JwtScheme).RequireRole(PiranhaRoles.Administrator));
        return builder;
    }

    /// <summary>
    /// Maps <c>POST {prefix}/auth/token</c> (anonymous) and every contributor's endpoints. The group requires
    /// a valid JWT (401, never a login redirect); contributors tighten writes with <see cref="ApiPolicies.Write"/>.
    /// </summary>
    public static IEndpointRouteBuilder MapPiranhaApi(this IEndpointRouteBuilder app, string prefix = "/api/v1")
    {
        var root = app.MapGroup(prefix);

        root.MapPost("/auth/token", async (TokenRequest request, JwtTokenService tokens) =>
                await tokens.IssueAsync(request) is { } t ? Results.Ok(t) : Results.Unauthorized())
            .AllowAnonymous().WithTags("Auth");

        var api = root.MapGroup("").RequireAuthorization(ApiPolicies.Read).AddEndpointFilter<PiranhaExceptionFilter>();
        foreach (var c in app.ServiceProvider.GetServices<IApiEndpointContributor>())
            c.MapEndpoints(api);

        app.MapOpenApi().AllowAnonymous();
        return app;
    }
}
