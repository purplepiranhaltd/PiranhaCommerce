using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using PiranhaCommerce.Core.Identity;

namespace PiranhaCommerce.Api.Auth;

public sealed record TokenRequest(string Email, string Password);
public sealed record TokenResponse(string AccessToken, string TokenType, int ExpiresIn);

public sealed class JwtTokenService(IOptions<PiranhaJwtOptions> options, UserManager<PiranhaUser> users)
{
    public static SymmetricSecurityKey GetKey(PiranhaJwtOptions o)
    {
        if (string.IsNullOrEmpty(o.SigningKey) || o.SigningKey.Length < 32)
            throw new InvalidOperationException($"{PiranhaJwtOptions.SectionName}:SigningKey must be configured with at least 32 characters.");
        return new SymmetricSecurityKey(Encoding.UTF8.GetBytes(o.SigningKey));
    }

    /// <summary>Returns null when credentials are invalid, the user is inactive or locked out.</summary>
    public async Task<TokenResponse?> IssueAsync(TokenRequest request)
    {
        var user = await users.FindByEmailAsync(request.Email);
        if (user is null || !user.Active || await users.IsLockedOutAsync(user))
            return null;

        if (!await users.CheckPasswordAsync(user, request.Password))
        {
            await users.AccessFailedAsync(user);
            return null;
        }
        await users.ResetAccessFailedCountAsync(user);

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
