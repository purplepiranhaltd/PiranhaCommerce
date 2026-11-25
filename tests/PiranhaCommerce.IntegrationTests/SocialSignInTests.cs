using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;
using PiranhaCommerce.Api.Auth;
using PiranhaCommerce.Customers;
using PiranhaCommerce.Customers.SocialSignIn;
using PiranhaCommerce.Data;

namespace PiranhaCommerce.IntegrationTests;

/// <summary>
/// Sign-in with a social account: the token is really checked (signature, issuer, audience, expiry, nonce), the shop's own rules still apply
/// (two-factor, matching, sign-up), and linking is by the provider's account id, so a different email on the social account does not matter.
/// </summary>
public class SocialSignInTests(PiranhaCommerceFactory factory) : IClassFixture<PiranhaCommerceFactory>
{
    private const string Password = "a-long-enough-password";
    private const string Issuer = "https://accounts.test";
    private const string ClientId = "shop-app-1";
    private static readonly RsaSecurityKey Key = new(RSA.Create(2048)) { KeyId = "k1" };
    private static readonly RsaSecurityKey OtherKey = new(RSA.Create(2048)) { KeyId = "k1" }; // same id, different key: a forgery

    private static string NewEmail() => $"social-{Guid.NewGuid():N}@example.com";

    /// <summary>A provider's published keys, held in memory in place of the web request that would fetch them.</summary>
    private sealed class FakeProvider : IOidcConfigurationSource
    {
        public IConfigurationManager<OpenIdConnectConfiguration> For(string authority)
        {
            var config = new OpenIdConnectConfiguration { Issuer = Issuer };
            config.SigningKeys.Add(Key);
            return new StaticConfigurationManager<OpenIdConnectConfiguration>(config);
        }
    }

    private WebApplicationFactory<Program> Host(Action<Dictionary<string, string?>>? settings = null)
    {
        var s = new Dictionary<string, string?>
        {
            ["PiranhaCommerce:Customers:Social:Providers:google:DisplayName"] = "Google",
            ["PiranhaCommerce:Customers:Social:Providers:google:Authority"] = Issuer,
            ["PiranhaCommerce:Customers:Social:Providers:google:ClientIds:0"] = ClientId,
        };
        settings?.Invoke(s);
        return factory.WithWebHostBuilder(b =>
        {
            foreach (var (k, v) in s) b.UseSetting(k, v);
            b.ConfigureServices(services => services.AddSingleton<IOidcConfigurationSource>(new FakeProvider()));
        });
    }

    private static string IdToken(string subject, string? email, bool verified = true, string audience = ClientId, string issuer = Issuer, TimeSpan? lifetime = null,
        SecurityKey? key = null, string algorithm = SecurityAlgorithms.RsaSha256, string? nonce = null, string? first = null)
    {
        var claims = new Dictionary<string, object> { ["sub"] = subject };
        if (email is not null) { claims["email"] = email; claims["email_verified"] = verified; }
        if (nonce is not null) claims["nonce"] = nonce;
        if (first is not null) claims["given_name"] = first;
        var now = DateTime.UtcNow;
        var life = lifetime ?? TimeSpan.FromMinutes(10);
        return new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
        {
            Issuer = issuer, Audience = audience, Claims = claims,
            NotBefore = life < TimeSpan.Zero ? now.AddHours(-2) : now, IssuedAt = life < TimeSpan.Zero ? now.AddHours(-2) : now, Expires = now.Add(life),
            SigningCredentials = new SigningCredentials(key ?? Key, algorithm),
        });
    }

    private static HttpClient WithToken(WebApplicationFactory<Program> host, string token)
    {
        var c = host.CreateClient();
        c.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return c;
    }

    private sealed record Shopper(HttpClient Client, Guid Id, string Email);

    private static async Task<Shopper> RegisterAsync(WebApplicationFactory<Program> host)
    {
        var email = NewEmail();
        var r = await host.CreateClient().PostAsJsonAsync("/api/v1/storefront/auth/register", new RegisterInput { Email = email, Password = Password });
        Assert.Equal(HttpStatusCode.Created, r.StatusCode);
        var reg = (await r.Content.ReadFromJsonAsync<RegisteredDto>())!;
        return new Shopper(WithToken(host, reg.Token.AccessToken), reg.Profile.Id, email);
    }

    private static Task<HttpResponseMessage> SocialAsync(WebApplicationFactory<Program> host, string idToken, string? nonce = null, string? code = null, string provider = "google")
        => host.CreateClient().PostAsJsonAsync($"/api/v1/storefront/auth/external/{provider}", new ExternalSignInInput { IdToken = idToken, Nonce = nonce, Code = code });

    private static async Task<string> ErrorAsync(HttpResponseMessage r)
        => (await r.Content.ReadFromJsonAsync<JsonElement>()).TryGetProperty("error", out var e) ? e.GetString()! : "";

    private static async Task AssertSignedInAsync(WebApplicationFactory<Program> host, HttpResponseMessage r, string? expectedEmail = null)
    {
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        var token = (await r.Content.ReadFromJsonAsync<TokenResponse>())!;
        var me = await WithToken(host, token.AccessToken).GetAsync("/api/v1/storefront/me");
        Assert.Equal(HttpStatusCode.OK, me.StatusCode);
        if (expectedEmail is not null) Assert.Contains(expectedEmail, await me.Content.ReadAsStringAsync(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task The_shop_lists_its_providers_and_an_unknown_one_is_not_found()
    {
        using var host = Host();
        var list = await host.CreateClient().GetFromJsonAsync<JsonElement>("/api/v1/storefront/auth/external");
        Assert.Equal("google", list[0].GetProperty("name").GetString());
        Assert.Equal("Google", list[0].GetProperty("displayName").GetString());
        Assert.Equal(HttpStatusCode.NotFound, (await SocialAsync(host, IdToken("x", NewEmail()), provider: "facebook")).StatusCode);
    }

    [Fact]
    public async Task A_token_that_is_not_right_is_refused_whatever_is_wrong_with_it()
    {
        using var host = Host(s => s["PiranhaCommerce:Customers:Social:AllowSignUp"] = "true"); // so only the token can be the reason
        var email = NewEmail();
        var bad = new Dictionary<string, string>
        {
            ["another app's client id"] = IdToken("s1", email, audience: "someone-elses-app"),
            ["another issuer"] = IdToken("s1", email, issuer: "https://evil.test"),
            ["expired"] = IdToken("s1", email, lifetime: TimeSpan.FromMinutes(-30)),
            ["signed with a different key"] = IdToken("s1", email, key: OtherKey),
            ["a symmetric (HS256) signature"] = IdToken("s1", email, key: new SymmetricSecurityKey(RandomNumberGenerator.GetBytes(32)) { KeyId = "k1" }, algorithm: SecurityAlgorithms.HmacSha256),
            ["not a token at all"] = "this.is.not-a-token",
        };
        foreach (var (why, token) in bad)
        {
            var r = await SocialAsync(host, token);
            Assert.True(r.StatusCode == HttpStatusCode.Unauthorized, $"{why}: {r.StatusCode}");
            Assert.Equal("invalid_token", await ErrorAsync(r));
        }
        // A right token is fine, and a wrong nonce is not.
        Assert.Equal(HttpStatusCode.Unauthorized, (await SocialAsync(host, IdToken("s1", email, nonce: "abc"), nonce: "different")).StatusCode);
        await AssertSignedInAsync(host, await SocialAsync(host, IdToken("s1", email, nonce: "abc"), nonce: "abc"));
    }

    [Fact]
    public async Task By_default_an_unlinked_social_account_cannot_reach_a_shop_account_with_the_same_email_or_make_one()
    {
        using var host = Host();
        var s = await RegisterAsync(host);

        var same = await SocialAsync(host, IdToken("g-" + Guid.NewGuid(), s.Email));
        Assert.Equal(HttpStatusCode.Conflict, same.StatusCode);
        Assert.Equal("account_exists", await ErrorAsync(same));

        var stranger = await SocialAsync(host, IdToken("g-" + Guid.NewGuid(), NewEmail()));
        Assert.Equal(HttpStatusCode.Forbidden, stranger.StatusCode);
        Assert.Equal("sign_up_disabled", await ErrorAsync(stranger));
    }

    [Fact]
    public async Task Linking_from_a_signed_in_session_works_with_a_different_email_and_then_signs_in_by_the_provider_id()
    {
        using var host = Host();
        var s = await RegisterAsync(host);
        var sub = "g-" + Guid.NewGuid();
        var gmail = NewEmail(); // not the shop email

        var link = await s.Client.PostAsJsonAsync("/api/v1/storefront/me/external/google", new ExternalLinkInput { IdToken = IdToken(sub, gmail) });
        Assert.Equal(HttpStatusCode.Created, link.StatusCode);
        var listed = await s.Client.GetFromJsonAsync<JsonElement>("/api/v1/storefront/me/external");
        Assert.Equal("google", listed[0].GetProperty("provider").GetString());

        // Now Google signs them in, and the email on either side is irrelevant: even after it changes at Google.
        await AssertSignedInAsync(host, await SocialAsync(host, IdToken(sub, gmail)), s.Email);
        await AssertSignedInAsync(host, await SocialAsync(host, IdToken(sub, NewEmail(), verified: false)), s.Email);

        // Linking twice is harmless, and nobody else can take the same social account or add a second one from the same provider.
        Assert.Equal(HttpStatusCode.OK, (await s.Client.PostAsJsonAsync("/api/v1/storefront/me/external/google", new ExternalLinkInput { IdToken = IdToken(sub, gmail) })).StatusCode);
        var other = await RegisterAsync(host);
        var taken = await other.Client.PostAsJsonAsync("/api/v1/storefront/me/external/google", new ExternalLinkInput { IdToken = IdToken(sub, gmail) });
        Assert.Equal(HttpStatusCode.Conflict, taken.StatusCode);
        Assert.Equal("already_linked", await ErrorAsync(taken));
        var second = await s.Client.PostAsJsonAsync("/api/v1/storefront/me/external/google", new ExternalLinkInput { IdToken = IdToken("g-" + Guid.NewGuid(), gmail) });
        Assert.Equal("provider_already_linked", await ErrorAsync(second));

        // A bad token does not link, and linking needs a shop session.
        Assert.Equal(HttpStatusCode.Unauthorized, (await s.Client.PostAsJsonAsync("/api/v1/storefront/me/external/google", new ExternalLinkInput { IdToken = IdToken("z", gmail, audience: "other") })).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await host.CreateClient().PostAsJsonAsync("/api/v1/storefront/me/external/google", new ExternalLinkInput { IdToken = IdToken("z", gmail) })).StatusCode);

        // Removing it closes that door, and the password still works.
        Assert.Equal(HttpStatusCode.NoContent, (await s.Client.DeleteAsync("/api/v1/storefront/me/external/google")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await SocialAsync(host, IdToken(sub, gmail))).StatusCode); // unlinked again, and a different email reaches nothing
        Assert.Equal(HttpStatusCode.OK, (await host.CreateClient().PostAsJsonAsync("/api/v1/storefront/auth/token", new SignInInput(s.Email, Password))).StatusCode);
    }

    [Fact]
    public async Task When_sign_up_is_allowed_a_new_account_is_made_only_for_an_address_the_provider_vouches_for()
    {
        using var host = Host(s => s["PiranhaCommerce:Customers:Social:AllowSignUp"] = "true");
        var email = NewEmail();
        var sub = "g-" + Guid.NewGuid();

        var unverified = await SocialAsync(host, IdToken(sub, email, verified: false));
        Assert.Equal(HttpStatusCode.Forbidden, unverified.StatusCode);
        Assert.Equal("email_not_verified_by_provider", await ErrorAsync(unverified));
        Assert.Equal(HttpStatusCode.BadRequest, (await SocialAsync(host, IdToken(sub, null))).StatusCode); // no email shared

        await AssertSignedInAsync(host, await SocialAsync(host, IdToken(sub, email, first: "Sam")), email);
        // Next time it is the link that is used. They cannot sign in with a password they were never given.
        await AssertSignedInAsync(host, await SocialAsync(host, IdToken(sub, email)), email);
        Assert.Equal(HttpStatusCode.Unauthorized, (await host.CreateClient().PostAsJsonAsync("/api/v1/storefront/auth/token", new SignInInput(email, Password))).StatusCode);

        // A different social account with the same address does not get in: the address belongs to an account now.
        Assert.Equal(HttpStatusCode.Conflict, (await SocialAsync(host, IdToken("g-" + Guid.NewGuid(), email))).StatusCode);
    }

    [Fact]
    public async Task Matching_on_a_verified_email_is_off_unless_the_shop_turns_it_on_and_then_needs_the_provider_to_vouch()
    {
        using var host = Host(s => s["PiranhaCommerce:Customers:Social:AccountMatching"] = "LinkVerifiedEmail");
        var s = await RegisterAsync(host);
        // The provider does not vouch for the address: still refused.
        Assert.Equal(HttpStatusCode.Conflict, (await SocialAsync(host, IdToken("g-" + Guid.NewGuid(), s.Email, verified: false))).StatusCode);
        // It does: linked and signed in.
        var sub = "g-" + Guid.NewGuid();
        await AssertSignedInAsync(host, await SocialAsync(host, IdToken(sub, s.Email)), s.Email);
        await AssertSignedInAsync(host, await SocialAsync(host, IdToken(sub, NewEmail())), s.Email); // and from then on by id
    }

    [Fact]
    public async Task Matching_wipes_the_password_and_sessions_of_an_account_whose_email_was_never_proved()
    {
        using var host = Host(s =>
        {
            s["PiranhaCommerce:Customers:Social:AccountMatching"] = "LinkVerifiedEmail";
            s["PiranhaCommerce:Customers:RequireVerifiedEmail"] = "false";
        });
        var squatter = await RegisterAsync(host); // registered the address without proving it
        await AssertStillSignedInAsync(squatter.Client);

        var owner = await SocialAsync(host, IdToken("g-" + Guid.NewGuid(), squatter.Email));
        await AssertSignedInAsync(host, owner, squatter.Email);

        // The squatter's password and live session are gone.
        Assert.Equal(HttpStatusCode.Unauthorized, (await host.CreateClient().PostAsJsonAsync("/api/v1/storefront/auth/token", new SignInInput(squatter.Email, Password))).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await squatter.Client.GetAsync("/api/v1/storefront/me")).StatusCode);
    }

    private static async Task AssertStillSignedInAsync(HttpClient c) => Assert.Equal(HttpStatusCode.OK, (await c.GetAsync("/api/v1/storefront/me")).StatusCode);

    [Fact]
    public async Task A_shopper_with_two_factor_still_needs_the_code_after_a_social_sign_in()
    {
        using var host = Host();
        var s = await RegisterAsync(host);
        var sub = "g-" + Guid.NewGuid();
        Assert.Equal(HttpStatusCode.Created, (await s.Client.PostAsJsonAsync("/api/v1/storefront/me/external/google", new ExternalLinkInput { IdToken = IdToken(sub, NewEmail()) })).StatusCode);

        var setup = await (await s.Client.PostAsync("/api/v1/storefront/me/two-factor/setup", null)).Content.ReadFromJsonAsync<JsonElement>();
        var secret = setup.GetProperty("secret").GetString()!.Replace(" ", "");
        var enable = await s.Client.PostAsJsonAsync("/api/v1/storefront/me/two-factor/enable", new TwoFactorCodeInput { Code = Totp.CodeFor(secret, Totp.StepAt(DateTimeOffset.UtcNow)) });
        Assert.Equal(HttpStatusCode.OK, enable.StatusCode);

        var without = await SocialAsync(host, IdToken(sub, null));
        Assert.Equal(HttpStatusCode.Unauthorized, without.StatusCode);
        Assert.Equal("two_factor_required", await ErrorAsync(without));
        Assert.Equal(HttpStatusCode.Unauthorized, (await SocialAsync(host, IdToken(sub, null), code: "000000")).StatusCode);
        await AssertSignedInAsync(host, await SocialAsync(host, IdToken(sub, null), code: Totp.CodeFor(secret, Totp.StepAt(DateTimeOffset.UtcNow) + 1)));
    }

    [Fact]
    public async Task When_the_shop_requires_two_factor_a_new_social_account_is_taken_through_setting_it_up()
    {
        using var host = Host(s =>
        {
            s["PiranhaCommerce:Customers:Social:AllowSignUp"] = "true";
            s["PiranhaCommerce:Customers:RequireTwoFactor"] = "true";
        });
        var refused = await SocialAsync(host, IdToken("g-" + Guid.NewGuid(), NewEmail()));
        Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
        var body = await refused.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("two_factor_setup_required", body.GetProperty("error").GetString());
        Assert.False(string.IsNullOrEmpty(body.GetProperty("setupToken").GetString()));
    }

    [Fact]
    public async Task Linked_accounts_are_in_the_export_and_erased_with_the_customer()
    {
        using var host = Host();
        var s = await RegisterAsync(host);
        Assert.Equal(HttpStatusCode.Created, (await s.Client.PostAsJsonAsync("/api/v1/storefront/me/external/google", new ExternalLinkInput { IdToken = IdToken("g-" + Guid.NewGuid(), NewEmail()) })).StatusCode);

        var staff = await factory.CreateAuthenticatedApiClientAsync();
        Assert.Contains("social-sign-in", await staff.GetStringAsync($"/api/v1/customers/{s.Id}/export"));

        var history = await staff.GetStringAsync($"/api/v1/customers/{s.Id}/sign-in/history");
        Assert.Contains("social-linked", history);

        var erase = await staff.PostAsJsonAsync($"/api/v1/customers/{s.Id}/erase", new EraseCustomerInput { ConfirmEmail = s.Email });
        Assert.True(erase.IsSuccessStatusCode, await erase.Content.ReadAsStringAsync());
        using var scope = host.Services.CreateScope();
        Assert.False(await scope.ServiceProvider.GetRequiredService<PiranhaCommerceDbContext>().Set<CustomerExternalLogin>().AnyAsync(l => l.CustomerId == s.Id));
    }

    [Fact]
    public void A_provider_must_say_which_apps_it_is_for_and_use_https()
    {
        var validator = new SocialSignInOptionsValidator();
        var ok = new SocialSignInOptions { Providers = { ["google"] = new() { Authority = "https://accounts.google.com", ClientIds = ["x"] } } };
        Assert.True(validator.Validate(null, ok).Succeeded);
        Assert.False(validator.Validate(null, new SocialSignInOptions { Providers = { ["google"] = new() { Authority = "http://accounts.google.com", ClientIds = ["x"] } } }).Succeeded);
        Assert.False(validator.Validate(null, new SocialSignInOptions { Providers = { ["google"] = new() { Authority = "https://accounts.google.com" } } }).Succeeded);
        Assert.False(validator.Validate(null, new SocialSignInOptions { Providers = { ["bad name!"] = new() { Authority = "https://a.test", ClientIds = ["x"] } } }).Succeeded);
    }
}
