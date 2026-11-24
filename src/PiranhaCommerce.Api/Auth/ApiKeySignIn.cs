using System.Security.Claims;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.Extensions.DependencyInjection;
using PiranhaCommerce.Core.Identity;

namespace PiranhaCommerce.Api;

/// <summary>Lets an API key sign in on the staff scheme. Everything after this (permissions, audit, rate limits) treats it like any staff caller.</summary>
internal static class ApiKeySignIn
{
    public static async Task AcceptAsync(MessageReceivedContext context)
    {
        var request = context.Request;
        string? presented = request.Headers["X-Api-Key"].FirstOrDefault();
        if (presented is null && request.Headers.Authorization.FirstOrDefault() is { } header && header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
            presented = header["Bearer ".Length..].Trim();
        if (!ApiKeyFormat.LooksLikeKey(presented)) return; // a normal token: let the JWT handler read it

        var authenticator = context.HttpContext.RequestServices.GetService<IApiKeyAuthenticator>();
        var who = authenticator is null ? null : await authenticator.AuthenticateAsync(presented!, context.HttpContext.RequestAborted);
        if (who is null)
        {
            context.Fail("The API key is not valid.");
            return;
        }

        var claims = new List<Claim>
        {
            new("sub", who.UserId.ToString()),
            new("email", who.Email),
            new("name", who.Name),
        };
        claims.AddRange(who.Roles.Select(r => new Claim("role", r)));
        context.Principal = new ClaimsPrincipal(new ClaimsIdentity(claims, "ApiKey", "sub", "role"));
        context.Success();
    }
}
