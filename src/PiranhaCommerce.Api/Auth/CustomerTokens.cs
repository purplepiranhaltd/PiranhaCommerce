using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;

namespace PiranhaCommerce.Api.Auth;

/// <summary>
/// Issues sign-in tokens for shoppers. These are deliberately not interchangeable with staff tokens: they use a different
/// audience and a different signing key (derived from the staff key), so the staff API refuses them and the storefront API
/// refuses staff tokens.
/// </summary>
public interface ICustomerTokenIssuer
{
    TokenResponse Issue(Guid customerId, string email);
}

internal sealed class CustomerTokenIssuer(IOptions<PiranhaJwtOptions> options) : ICustomerTokenIssuer
{
    /// <summary>Different key from the staff key, so a token for one audience can never verify for the other.</summary>
    internal static SymmetricSecurityKey GetKey(PiranhaJwtOptions o)
    {
        var staffKey = JwtTokenService.GetKey(o).Key;
        return new SymmetricSecurityKey(HMACSHA256.HashData(staffKey, Encoding.UTF8.GetBytes("piranha-commerce/customer-tokens")));
    }

    public TokenResponse Issue(Guid customerId, string email)
    {
        var o = options.Value;
        var claims = new List<Claim>
        {
            new(JwtRegisteredClaimNames.Sub, customerId.ToString()),
            new(JwtRegisteredClaimNames.Email, email),
            new(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString()),
        };
        var token = new JwtSecurityToken(o.Issuer, o.CustomerAudience, claims,
            expires: DateTime.UtcNow.AddMinutes(o.LifetimeMinutes),
            signingCredentials: new SigningCredentials(GetKey(o), SecurityAlgorithms.HmacSha256));
        return new TokenResponse(new JwtSecurityTokenHandler().WriteToken(token), "Bearer", o.LifetimeMinutes * 60);
    }
}
