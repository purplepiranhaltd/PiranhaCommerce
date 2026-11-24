namespace PiranhaCommerce.Api.Auth;

/// <summary>Bound from <c>PiranhaCommerce:Jwt</c>. The signing key must be at least 32 characters; supply it from secrets/Key Vault in production.</summary>
public sealed class PiranhaJwtOptions
{
    public const string SectionName = "PiranhaCommerce:Jwt";

    public string Issuer { get; set; } = "PiranhaCommerce";
    public string Audience { get; set; } = "PiranhaCommerce.Api";
    /// <summary>Audience of shopper tokens. Must differ from <see cref="Audience"/> so the two kinds are never interchangeable.</summary>
    public string CustomerAudience { get; set; } = "PiranhaCommerce.Storefront";

    public string? SigningKey { get; set; }
    public int LifetimeMinutes { get; set; } = 60;
}
