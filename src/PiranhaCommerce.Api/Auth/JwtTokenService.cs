using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using PiranhaCommerce.Core.Identity;

namespace PiranhaCommerce.Api.Auth;

public sealed record TokenRequest(string Email, string Password);
/// <param name="RefreshToken">Shoppers only, and only when refresh tokens are switched on: swap it at the refresh endpoint for the next access token. Shown once.</param>
public sealed record TokenResponse(string AccessToken, string TokenType, int ExpiresIn, string? RefreshToken = null);

public sealed class JwtTokenService(IOptions<PiranhaCommerceJwtOptions> options, UserManager<PiranhaCommerceUser> users, ICredentialChecker credentials)
{
    public static SymmetricSecurityKey GetKey(PiranhaCommerceJwtOptions o)
    {
        if (string.IsNullOrEmpty(o.SigningKey) || o.SigningKey.Length < 32)
            throw new InvalidOperationException($"{PiranhaCommerceJwtOptions.SectionName}:SigningKey must be configured with at least 32 characters.");
        return new SymmetricSecurityKey(Encoding.UTF8.GetBytes(o.SigningKey));
    }

    /// <summary>Returns null when credentials are invalid, the user is inactive or locked out (all look the same to the caller).</summary>
    public async Task<TokenResponse?> IssueAsync(TokenRequest request, CancellationToken ct = default)
    {
        var check = await credentials.CheckAsync(request.Email, request.Password, ct);
        if (check.User is not { } user) return null;

        var o = options.Value;
        var claims = new List<Claim>
        {
            new(JwtRegisteredClaimNames.Sub, user.Id.ToString()),
            new(JwtRegisteredClaimNames.Email, user.Email ?? ""),
            new(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString()),
        };
        claims.AddRange((await users.GetRolesAsync(user)).Select(r => new Claim("role", r)));

        var expires = DateTime.UtcNow.AddMinutes(o.LifetimeMinutes);
        var token = new JwtSecurityToken(o.Issuer, o.Audience, claims, expires: expires,
            signingCredentials: new SigningCredentials(GetKey(o), SecurityAlgorithms.HmacSha256));

        return new TokenResponse(new JwtSecurityTokenHandler().WriteToken(token), "Bearer", o.LifetimeMinutes * 60);
    }
}
