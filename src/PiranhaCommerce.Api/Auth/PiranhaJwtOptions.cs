namespace PiranhaCommerce.Api.Auth;

/// <summary>Bound from <c>Piranha:Jwt</c>. The signing key must be at least 32 characters; supply it from secrets/Key Vault in production.</summary>
public sealed class PiranhaJwtOptions
{
    public const string SectionName = "Piranha:Jwt";

    public string Issuer { get; set; } = "PiranhaCommerce";
    public string Audience { get; set; } = "PiranhaCommerce.Api";
    public string? SigningKey { get; set; }
    public int LifetimeMinutes { get; set; } = 60;
}
