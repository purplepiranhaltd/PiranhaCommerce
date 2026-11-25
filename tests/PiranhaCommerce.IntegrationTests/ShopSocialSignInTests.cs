using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;
using PiranhaCommerce.Customers.SocialSignIn;

namespace PiranhaCommerce.IntegrationTests;

/// <summary>The sample shop's Google button and the forms behind it, driven as a browser would (with a stand-in for Google's keys).</summary>
public partial class ShopSocialSignInTests(PiranhaCommerceFactory factory) : IClassFixture<PiranhaCommerceFactory>
{
    private const string Password = "Shopper-Passw0rd!";
    private const string Issuer = "https://accounts.google.test";
    private const string ClientId = "petalwick-client";
    private static readonly RsaSecurityKey Key = new(RSA.Create(2048)) { KeyId = "k1" };

    [GeneratedRegex("name=\"__RequestVerificationToken\"[^>]*value=\"([^\"]+)\"")]
    private static partial Regex TokenPattern();

    [GeneratedRegex("data-nonce=\"([^\"]*)\"")]
    private static partial Regex NoncePattern();

    private sealed class FakeGoogle : IOidcConfigurationSource
    {
        public IConfigurationManager<OpenIdConnectConfiguration> For(string authority)
        {
            var config = new OpenIdConnectConfiguration { Issuer = Issuer };
            config.SigningKeys.Add(Key);
            return new StaticConfigurationManager<OpenIdConnectConfiguration>(config);
        }
    }

    private WebApplicationFactory<Program> Shop(bool configured = true, bool signUp = false) => factory.WithWebHostBuilder(b =>
    {
        if (configured)
        {
            b.UseSetting("PiranhaCommerce:Customers:Social:Providers:google:Authority", Issuer);
            b.UseSetting("PiranhaCommerce:Customers:Social:Providers:google:ClientIds:0", ClientId);
        }
        b.UseSetting("PiranhaCommerce:Customers:Social:AllowSignUp", signUp ? "true" : "false");
        b.ConfigureServices(s => s.AddSingleton<IOidcConfigurationSource>(new FakeGoogle()));
    });

    private static HttpClient Browser(WebApplicationFactory<Program> shop) =>
        shop.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false, HandleCookies = true });

    private static string Token(string subject, string email, string? nonce) => new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
    {
        Issuer = Issuer, Audience = ClientId, Expires = DateTime.UtcNow.AddMinutes(10),
        Claims = new Dictionary<string, object> { ["sub"] = subject, ["email"] = email, ["email_verified"] = true, ["nonce"] = nonce ?? "" },
        SigningCredentials = new SigningCredentials(Key, SecurityAlgorithms.RsaSha256),
    });

    private static string NewEmail() => $"google-{Guid.NewGuid():N}@example.com";

    /// <summary>What Google's button does: posts the credential, with its own double-submit cookie.</summary>
    private static async Task<HttpResponseMessage> GoogleAsync(HttpClient browser, string path, string credential, string csrf = "abc123", bool sendCookie = true)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, path)
        {
            Content = new FormUrlEncodedContent([KeyValuePair.Create("credential", credential), KeyValuePair.Create("g_csrf_token", csrf)]),
        };
        if (sendCookie) request.Headers.Add("Cookie", "g_csrf_token=abc123");
        return await browser.SendAsync(request);
    }

    private static async Task<string> NonceOfAsync(HttpClient browser, string page)
    {
        var response = await browser.GetAsync(page);
        var body = await response.Content.ReadAsStringAsync();
        Assert.True(response.IsSuccessStatusCode, $"{(int)response.StatusCode} {page}: {body[..Math.Min(body.Length, 1500)]}");
        return NoncePattern().Match(body).Groups[1].Value;
    }

    private static async Task RegisterAndSignInAsync(HttpClient browser, string email)
    {
        var page = await browser.GetStringAsync("/account/sign-in");
        var token = TokenPattern().Matches(page)[1].Groups[1].Value; // the second form is the registration one
        var r = await browser.PostAsync("/account/register", new FormUrlEncodedContent(new Dictionary<string, string>
            { ["email"] = email, ["password"] = Password, ["firstName"] = "Sam", ["lastName"] = "Shopper", ["__RequestVerificationToken"] = token }));
        Assert.Equal(HttpStatusCode.Redirect, r.StatusCode);
        Assert.Equal("/account", r.Headers.Location!.OriginalString);
    }

    [Fact]
    public async Task The_button_is_shown_only_when_google_is_configured()
    {
        using var on = Shop();
        var page = await Browser(on).GetStringAsync("/account/sign-in");
        Assert.Contains("accounts.google.com/gsi/client", page);
        Assert.Contains($"data-client_id=\"{ClientId}\"", page);
        Assert.Contains("/account/social/google\"", page);
        Assert.False(string.IsNullOrEmpty(NoncePattern().Match(page).Groups[1].Value));

        using var off = Shop(configured: false);
        Assert.DoesNotContain("gsi/client", await Browser(off).GetStringAsync("/account/sign-in"));
    }

    [Fact]
    public async Task Google_signs_in_a_new_shopper_when_sign_up_is_on_and_the_nonce_and_csrf_cookie_must_match()
    {
        using var shop = Shop(signUp: true);
        var email = NewEmail();
        var browser = Browser(shop);
        var nonce = await NonceOfAsync(browser, "/account/sign-in");

        // Not from Google's button (no double-submit cookie), or for a different page load (wrong nonce): refused.
        Assert.Equal(HttpStatusCode.BadRequest, (await GoogleAsync(browser, "/account/social/google", Token("sub-1", email, nonce), sendCookie: false)).StatusCode);
        var wrong = await GoogleAsync(browser, "/account/social/google", Token("sub-1", email, "somebody-elses-nonce"));
        Assert.StartsWith("/account/sign-in", wrong.Headers.Location!.OriginalString);

        // The refusal used the nonce up; the sign-in page makes a fresh one each time it is shown.
        nonce = await NonceOfAsync(browser, "/account/sign-in");
        var ok = await GoogleAsync(browser, "/account/social/google", Token("sub-1", email, nonce));
        Assert.Equal(HttpStatusCode.Redirect, ok.StatusCode);
        Assert.Equal("/account", ok.Headers.Location!.OriginalString);
        Assert.Contains(email, await browser.GetStringAsync("/account"));
    }

    [Fact]
    public async Task A_shopper_can_add_google_from_their_security_page_and_then_sign_in_with_it_on_another_email()
    {
        using var shop = Shop();
        var browser = Browser(shop);
        await RegisterAndSignInAsync(browser, NewEmail());

        var securityPage = await browser.GetStringAsync("/account/security");
        Assert.Contains("/account/social/google/link\"", securityPage);
        var nonce = NoncePattern().Match(securityPage).Groups[1].Value; // the page set the nonce cookie that the form post is checked against
        var linked = await GoogleAsync(browser, "/account/social/google/link", Token("sub-link", NewEmail(), nonce));
        Assert.StartsWith("/account/security", linked.Headers.Location!.OriginalString);
        Assert.Contains("is linked", await browser.GetStringAsync("/account/security"));

        var other = Browser(shop); // a fresh browser, signed out
        var fresh = await NonceOfAsync(other, "/account/sign-in");
        var ok = await GoogleAsync(other, "/account/social/google", Token("sub-link", NewEmail(), fresh));
        Assert.Equal("/account", ok.Headers.Location!.OriginalString);

        // Removing it closes that door.
        var page = await browser.GetStringAsync("/account/security");
        var csrf = TokenPattern().Match(page).Groups[1].Value;
        Assert.Equal(HttpStatusCode.Redirect, (await browser.PostAsync("/account/social/google/remove", new FormUrlEncodedContent(new Dictionary<string, string> { ["__RequestVerificationToken"] = csrf }))).StatusCode);
        var again = Browser(shop);
        var n = await NonceOfAsync(again, "/account/sign-in");
        Assert.StartsWith("/account/sign-in", (await GoogleAsync(again, "/account/social/google", Token("sub-link", NewEmail(), n))).Headers.Location!.OriginalString);
    }
}
