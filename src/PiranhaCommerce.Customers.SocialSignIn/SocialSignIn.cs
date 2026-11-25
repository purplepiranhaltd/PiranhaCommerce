using System.Collections.Concurrent;
using System.ComponentModel.DataAnnotations;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace PiranhaCommerce.Customers.SocialSignIn;

/// <summary>What a shop does when a shopper signs in with a social account that is not linked to anyone yet, but whose email address belongs to an existing customer.</summary>
public enum SocialAccountMatching
{
    /// <summary>
    /// Never join them up on the strength of an email address. The shopper signs in the usual way and adds the social account from their
    /// profile. The safest choice, and the default: a compromised social account can reach only the accounts it was deliberately linked to.
    /// </summary>
    Never = 0,

    /// <summary>
    /// Link them when the provider says the address is verified. Convenient, and it means trusting the provider with that address:
    /// anyone who controls the social account can reach the shop account. A password set by someone who registered the address
    /// without proving it is wiped, and their sessions end, when the real owner arrives this way.
    /// </summary>
    LinkVerifiedEmail = 1,
}

/// <summary>Bound to <c>PiranhaCommerce:Customers:Social</c>.</summary>
public sealed class SocialSignInOptions
{
    public const string SectionName = "PiranhaCommerce:Customers:Social";

    /// <summary>See <see cref="SocialAccountMatching"/>. Default <c>Never</c>.</summary>
    public SocialAccountMatching AccountMatching { get; set; } = SocialAccountMatching.Never;

    /// <summary>
    /// Whether someone with no shop account can make one by signing in with a social account. Off by default. Needs the provider to say
    /// the address is verified, so nobody can claim an address they do not own.
    /// </summary>
    public bool AllowSignUp { get; set; }

    /// <summary>The providers, by a short name used in the address (<c>google</c>, <c>microsoft</c>, <c>apple</c>). Nothing happens until one is set.</summary>
    public Dictionary<string, SocialProviderOptions> Providers { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

public sealed class SocialProviderOptions
{
    /// <summary>Shown to shoppers, for example "Google".</summary>
    public string? DisplayName { get; set; }

    /// <summary>The provider's OpenID Connect address, which has <c>/.well-known/openid-configuration</c> under it. For Google, <c>https://accounts.google.com</c>.</summary>
    public string Authority { get; set; } = "";

    /// <summary>The issuer a token must carry, when it differs from the one in the provider's configuration (some multi-tenant providers). Usually left out.</summary>
    public string? Issuer { get; set; }

    /// <summary>Your app's client ids at this provider. A token made for any other app is refused, however genuine it is.</summary>
    public List<string> ClientIds { get; set; } = [];
}

public sealed partial class SocialSignInOptionsValidator : IValidateOptions<SocialSignInOptions>
{
    [GeneratedRegex("^[a-z0-9-]{1,40}$")]
    private static partial Regex Name();

    public ValidateOptionsResult Validate(string? name, SocialSignInOptions o)
    {
        var errors = new List<string>();
        foreach (var (key, p) in o.Providers)
        {
            if (!Name().IsMatch(key.ToLowerInvariant())) errors.Add($"Social provider \"{key}\": the name may only use letters, digits and dashes.");
            if (!Uri.TryCreate(p.Authority, UriKind.Absolute, out var u) || u.Scheme != Uri.UriSchemeHttps) errors.Add($"Social provider \"{key}\": Authority must be an https address.");
            if (p.ClientIds.Count == 0 || p.ClientIds.Any(string.IsNullOrWhiteSpace)) errors.Add($"Social provider \"{key}\": set ClientIds to your app's client id(s), so tokens made for other apps are refused.");
        }
        return errors.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(errors);
    }
}

/// <summary>Who the provider says someone is. <paramref name="Subject"/> never changes for an account, unlike the email address.</summary>
public sealed record ExternalIdentity(string Subject, string? Email, bool EmailVerified, string? FirstName, string? LastName);

/// <summary>Turns a provider's ID token into who it says someone is, or null when it cannot be trusted.</summary>
public interface IExternalIdentityVerifier
{
    Task<ExternalIdentity?> VerifyAsync(SocialProviderOptions provider, string idToken, string? nonce, CancellationToken ct = default);
}

/// <summary>Where a provider's keys come from. The default fetches its OpenID Connect configuration over https and keeps it; tests replace it.</summary>
public interface IOidcConfigurationSource
{
    IConfigurationManager<OpenIdConnectConfiguration> For(string authority);
}

internal sealed class HttpOidcConfigurationSource : IOidcConfigurationSource
{
    private readonly ConcurrentDictionary<string, IConfigurationManager<OpenIdConnectConfiguration>> _managers = new();

    public IConfigurationManager<OpenIdConnectConfiguration> For(string authority) => _managers.GetOrAdd(authority, a =>
        new ConfigurationManager<OpenIdConnectConfiguration>(
            a.TrimEnd('/') + "/.well-known/openid-configuration", new OpenIdConnectConfigurationRetriever(), new HttpDocumentRetriever { RequireHttps = true }));
}

/// <summary>
/// Checks an OpenID Connect ID token the way the specification asks: a signature from one of the provider's published keys (asymmetric
/// only), the right issuer, one of OUR client ids as the audience, not expired, and the nonce if the caller sent one.
/// </summary>
internal sealed class OidcIdentityVerifier(IOidcConfigurationSource configurations) : IExternalIdentityVerifier
{
    private static readonly string[] Algorithms =
        [SecurityAlgorithms.RsaSha256, SecurityAlgorithms.RsaSha384, SecurityAlgorithms.RsaSha512, SecurityAlgorithms.EcdsaSha256, SecurityAlgorithms.EcdsaSha384, SecurityAlgorithms.EcdsaSha512];

    private static readonly JsonWebTokenHandler Handler = new();

    public async Task<ExternalIdentity?> VerifyAsync(SocialProviderOptions provider, string idToken, string? nonce, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(idToken) || idToken.Length > 8192 || !Handler.CanReadToken(idToken)) return null;
        var manager = configurations.For(provider.Authority);
        for (var attempt = 0; attempt < 2; attempt++)
        {
            OpenIdConnectConfiguration config;
            try { config = await manager.GetConfigurationAsync(ct); }
            catch (Exception) { return null; } // the provider cannot be reached: nobody signs in, and nothing is trusted by default

            var result = await Handler.ValidateTokenAsync(idToken, new TokenValidationParameters
            {
                ValidIssuer = provider.Issuer ?? config.Issuer,
                ValidAudiences = provider.ClientIds,
                IssuerSigningKeys = config.SigningKeys,
                ValidAlgorithms = Algorithms,
                ValidateIssuer = true,
                ValidateAudience = true,
                ValidateLifetime = true,
                ValidateIssuerSigningKey = true,
                RequireSignedTokens = true,
                RequireExpirationTime = true,
                ClockSkew = TimeSpan.FromMinutes(2),
            });
            if (!result.IsValid)
            {
                // A key the provider has just started using: fetch its keys again once, then try once more.
                if (attempt == 0 && result.Exception is SecurityTokenSignatureKeyNotFoundException) { manager.RequestRefresh(); continue; }
                return null;
            }

            var jwt = (JsonWebToken)result.SecurityToken;
            string? Claim(string type) => jwt.Claims.FirstOrDefault(c => c.Type == type)?.Value;
            if (nonce is { Length: > 0 } && !string.Equals(Claim("nonce"), nonce, StringComparison.Ordinal)) return null;
            if (Claim("sub") is not { Length: > 0 } subject) return null;
            // Providers send this as a boolean or as the text "true" (Apple), and some leave it out.
            var verified = string.Equals(Claim("email_verified"), "true", StringComparison.OrdinalIgnoreCase);
            return new ExternalIdentity(subject, Claim("email"), verified, Claim("given_name"), Claim("family_name"));
        }
        return null;
    }
}

/// <summary>A social account linked to a shop customer. The provider's own id is what is matched, never the email address, which can change and need not match the shop's.</summary>
public class CustomerExternalLogin
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid CustomerId { get; set; }

    /// <summary>The provider's name in the configuration, lower case.</summary>
    public string Provider { get; set; } = "";

    /// <summary>The provider's permanent id for the account (the token's <c>sub</c>).</summary>
    public string Subject { get; set; } = "";

    /// <summary>The address on the social account when it was linked. For display only.</summary>
    public string? Email { get; set; }

    public DateTimeOffset LinkedUtc { get; set; }
    public DateTimeOffset? LastUsedUtc { get; set; }
}

public sealed class ExternalSignInInput
{
    /// <summary>The ID token the provider's sign-in gave the app.</summary>
    [Required, StringLength(8192)]
    public string IdToken { get; set; } = "";

    /// <summary>The nonce the app asked the provider to put in the token, if it used one. Checked when sent.</summary>
    [StringLength(200)]
    public string? Nonce { get; set; }

    /// <summary>The shop's two-factor code (authenticator app or recovery code), when the shopper has one set up and has just been asked for it.</summary>
    [StringLength(32)]
    public string? Code { get; set; }
}

public sealed class ExternalLinkInput
{
    [Required, StringLength(8192)]
    public string IdToken { get; set; } = "";

    [StringLength(200)]
    public string? Nonce { get; set; }
}

public sealed record ExternalProviderDto(string Name, string DisplayName);

public sealed record LinkedIdentityDto(string Provider, string? Email, DateTimeOffset LinkedUtc, DateTimeOffset? LastUsedUtc);
