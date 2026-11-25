using System.Net;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Mvc.Testing;
using PiranhaCommerce.Customers;

namespace PiranhaCommerce.IntegrationTests;

/// <summary>The sample shop's security page and sign-in code box, driven as a browser would.</summary>
public partial class ShopTwoFactorPageTests(PiranhaCommerceFactory factory) : IClassFixture<PiranhaCommerceFactory>
{
    private const string Password = "Shopper-Passw0rd!";

    [GeneratedRegex("name=\"__RequestVerificationToken\"[^>]*value=\"([^\"]+)\"")]
    private static partial Regex TokenPattern();

    [GeneratedRegex("Key: <code>([A-Z2-7 ]+)</code>")]
    private static partial Regex KeyPattern();

    [GeneratedRegex("<code>([A-Z0-9]{5}-[A-Z0-9]{5})</code>")]
    private static partial Regex RecoveryPattern();

    private static HttpClient Browser(WebApplicationFactory<Program> f) => f.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false, HandleCookies = true });

    private static async Task<HttpResponseMessage> PostAsync(HttpClient b, string pageWithForm, string action, params (string, string)[] fields)
    {
        var page = await b.GetStringAsync(pageWithForm);
        var token = TokenPattern().Match(page).Groups[1].Value;
        return await b.PostAsync(action, new FormUrlEncodedContent(fields.Append(("__RequestVerificationToken", token)).Select(f => KeyValuePair.Create(f.Item1, f.Item2))));
    }

    [Fact]
    public async Task A_shopper_sets_up_two_factor_on_the_security_page_sees_codes_once_and_then_needs_a_code_to_sign_in()
    {
        var email = $"tfa-page-{Guid.NewGuid():N}@example.com";
        var b = Browser(factory);
        Assert.Equal("/account", (await PostAsync(b, "/account/sign-in", "/account/register", ("email", email), ("password", Password), ("firstName", "Sam"), ("lastName", "S"))).Headers.Location!.OriginalString);

        var setup = await b.GetStringAsync("/account/security?setup=1");
        Assert.Contains("<svg", setup);
        var secret = KeyPattern().Match(setup).Groups[1].Value.Replace(" ", "");
        Assert.NotEmpty(secret);

        // A wrong code goes back to the page; a right one shows ten recovery codes, once.
        var wrong = await PostAsync(b, "/account/security?setup=1", "/account/two-factor/enable", ("code", "000000"));
        Assert.StartsWith("/account/security", wrong.Headers.Location!.OriginalString);
        var good = await PostAsync(b, "/account/security?setup=1", "/account/two-factor/enable", ("code", Totp.CodeFor(secret, Totp.StepAt(DateTimeOffset.UtcNow))));
        Assert.Equal("/account/security", good.Headers.Location!.OriginalString);
        var shown = await b.GetStringAsync("/account/security");
        Assert.Equal(10, RecoveryPattern().Matches(shown).Count);
        Assert.Contains("Two-factor sign-in is on", shown);
        Assert.Empty(RecoveryPattern().Matches(await b.GetStringAsync("/account/security"))); // not shown again

        // Signing in again needs the code.
        var other = Browser(factory);
        var noCode = await PostAsync(other, "/account/sign-in", "/account/sign-in", ("email", email), ("password", Password), ("returnTo", ""));
        Assert.StartsWith("/account/sign-in", noCode.Headers.Location!.OriginalString);
        var withCode = await PostAsync(other, "/account/sign-in", "/account/sign-in", ("email", email), ("password", Password), ("returnTo", ""),
            ("code", Totp.CodeFor(secret, Totp.StepAt(DateTimeOffset.UtcNow) + 1)));
        Assert.Equal("/account", withCode.Headers.Location!.OriginalString);
    }

    [Fact]
    public async Task When_the_shop_requires_it_signing_in_leads_to_the_setup_page_which_signs_them_in()
    {
        using var required = factory.WithWebHostBuilder(h => h.UseSetting("PiranhaCommerce:Customers:RequireTwoFactor", "true"));
        var email = $"tfa-req-{Guid.NewGuid():N}@example.com";
        var b = Browser(required);
        await PostAsync(b, "/account/sign-in", "/account/register", ("email", email), ("password", Password), ("firstName", "Sam"), ("lastName", "S"));
        await b.PostAsync("/account/sign-out", new FormUrlEncodedContent([KeyValuePair.Create("__RequestVerificationToken", TokenPattern().Match(await b.GetStringAsync("/account")).Groups[1].Value)]));

        var signIn = await PostAsync(b, "/account/sign-in", "/account/sign-in", ("email", email), ("password", Password), ("returnTo", ""));
        var to = signIn.Headers.Location!.OriginalString;
        Assert.StartsWith("/account/two-factor/setup", to);

        var page = await b.GetStringAsync(to);
        var secret = KeyPattern().Match(page).Groups[1].Value.Replace(" ", "");
        var token = TokenPattern().Match(page).Groups[1].Value;
        var finish = await b.PostAsync("/account/two-factor/finish", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["setupToken"] = Regex.Match(page, "name=\"setupToken\" value=\"([^\"]+)\"").Groups[1].Value.Replace("&#x2E;", "."),
            ["returnTo"] = "/account", ["code"] = Totp.CodeFor(secret, Totp.StepAt(DateTimeOffset.UtcNow)), ["__RequestVerificationToken"] = token,
        }));
        Assert.Equal("/account/security", finish.Headers.Location!.OriginalString);
        Assert.Contains(email, await b.GetStringAsync("/account"));
    }
}
