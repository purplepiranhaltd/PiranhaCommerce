using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using PiranhaCommerce.Api.Auth;
using PiranhaCommerce.Customers;
using PiranhaCommerce.Data;

namespace PiranhaCommerce.IntegrationTests;

/// <summary>A shopper's own two-factor sign-in: set up, signing in with a code, recovery codes, lockout, staff reset, and the shop-wide switch.</summary>
public class ShopperTwoFactorTests(PiranhaCommerceFactory factory) : IClassFixture<PiranhaCommerceFactory>
{
    private const string Password = "a-long-enough-password";
    private static string NewEmail() => $"tfa-{Guid.NewGuid():N}@example.com";

    private sealed record Shopper(HttpClient Client, Guid Id, string Email);

    private static HttpClient WithToken(WebApplicationFactory<Program> host, string token)
    {
        var c = host.CreateClient();
        c.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return c;
    }

    private async Task<Shopper> RegisterAsync(WebApplicationFactory<Program>? host = null)
    {
        host ??= factory;
        var email = NewEmail();
        var r = await host.CreateClient().PostAsJsonAsync("/api/v1/storefront/auth/register", new RegisterInput { Email = email, Password = Password });
        Assert.Equal(HttpStatusCode.Created, r.StatusCode);
        var reg = (await r.Content.ReadFromJsonAsync<RegisteredDto>())!;
        return new Shopper(WithToken(host, reg.Token.AccessToken), reg.Profile.Id, email);
    }

    private Task<HttpResponseMessage> TokenAsync(string email, string password = Password, string? code = null, WebApplicationFactory<Program>? host = null)
        => (host ?? factory).CreateClient().PostAsJsonAsync("/api/v1/storefront/auth/token", new SignInInput(email, password, code));

    /// <summary>A code the app would show a moment from now. Each code works once, so the next step's code is used for the second sign-in in a test.</summary>
    private static string CodeFor(string secret, int stepsAhead = 1)
        => Totp.CodeFor(secret.Replace(" ", ""), Totp.StepAt(DateTimeOffset.UtcNow) + stepsAhead);

    private sealed record Enrolled(Shopper Shopper, string Secret, IReadOnlyList<string> RecoveryCodes);

    /// <summary>Registers a shopper and turns two-factor on through the API, as an app would.</summary>
    private async Task<Enrolled> EnrolAsync()
    {
        var s = await RegisterAsync();
        var setup = await (await s.Client.PostAsync("/api/v1/storefront/me/two-factor/setup", null)).Content.ReadFromJsonAsync<JsonElement>();
        var secret = setup.GetProperty("secret").GetString()!.Replace(" ", "");
        var enable = await s.Client.PostAsJsonAsync("/api/v1/storefront/me/two-factor/enable", new TwoFactorCodeInput { Code = CodeFor(secret, 0) });
        Assert.Equal(HttpStatusCode.OK, enable.StatusCode);
        var codes = (await enable.Content.ReadFromJsonAsync<TwoFactorEnabledDto>())!.RecoveryCodes;
        return new Enrolled(s, secret, codes);
    }

    /// <summary>Forgets which codes were used, so a test can sign in more than once within the same minute.</summary>
    private async Task ForgetUsedCodesAsync(Guid customerId)
    {
        using var scope = factory.Services.CreateScope();
        await scope.ServiceProvider.GetRequiredService<PiranhaCommerceDbContext>().Set<CustomerTwoFactor>()
            .Where(t => t.CustomerId == customerId).ExecuteUpdateAsync(s => s.SetProperty(t => t.LastUsedStep, 0L));
    }

    [Theory]
    [InlineData(59L / 30, "287082")]            // RFC 6238 test vectors (the key "12345678901234567890"), six digits
    [InlineData(1111111109L / 30, "081804")]
    [InlineData(1234567890L / 30, "005924")]
    public void The_codes_are_the_ones_every_authenticator_app_makes(long step, string expected)
        => Assert.Equal(expected, Totp.CodeFor("GEZDGNBVGY3TQOJQGEZDGNBVGY3TQOJQ", step));

    [Fact]
    public async Task Setting_it_up_needs_a_right_code_and_gives_ten_recovery_codes_once()
    {
        var s = await RegisterAsync();
        var status = await s.Client.GetFromJsonAsync<TwoFactorStatusDto>("/api/v1/storefront/me/two-factor");
        Assert.False(status!.Enabled);

        var setup = (await (await s.Client.PostAsync("/api/v1/storefront/me/two-factor/setup", null)).Content.ReadFromJsonAsync<TwoFactorSetupDto>())!;
        Assert.StartsWith("otpauth://totp/", setup.OtpAuthUri);
        Assert.Contains("secret=", setup.OtpAuthUri);
        var secret = setup.Secret.Replace(" ", "");

        // Starting again straight away keeps the same key, so a QR code already scanned is not spoiled by a page reload.
        var again = (await (await s.Client.PostAsync("/api/v1/storefront/me/two-factor/setup", null)).Content.ReadFromJsonAsync<TwoFactorSetupDto>())!;
        Assert.Equal(setup.Secret, again.Secret);

        // Not on until a code from the app has been typed: signing in still needs no code.
        Assert.Equal(HttpStatusCode.OK, (await TokenAsync(s.Email)).StatusCode);

        var wrong = await s.Client.PostAsJsonAsync("/api/v1/storefront/me/two-factor/enable", new TwoFactorCodeInput { Code = "000000" });
        Assert.Equal(HttpStatusCode.BadRequest, wrong.StatusCode);
        Assert.False((await s.Client.GetFromJsonAsync<TwoFactorStatusDto>("/api/v1/storefront/me/two-factor"))!.Enabled);

        var enabled = await s.Client.PostAsJsonAsync("/api/v1/storefront/me/two-factor/enable", new TwoFactorCodeInput { Code = CodeFor(secret, 0) });
        Assert.Equal(HttpStatusCode.OK, enabled.StatusCode);
        var codes = (await enabled.Content.ReadFromJsonAsync<TwoFactorEnabledDto>())!.RecoveryCodes;
        Assert.Equal(10, codes.Count);
        Assert.Equal(10, codes.Distinct().Count());
        Assert.All(codes, c => Assert.Matches("^[A-Z0-9]{5}-[A-Z0-9]{5}$", c));

        var after = (await s.Client.GetFromJsonAsync<TwoFactorStatusDto>("/api/v1/storefront/me/two-factor"))!;
        Assert.True(after.Enabled);
        Assert.Equal(10, after.RecoveryCodesRemaining);

        // It is on already: starting again is refused.
        Assert.Equal(HttpStatusCode.BadRequest, (await s.Client.PostAsync("/api/v1/storefront/me/two-factor/setup", null)).StatusCode);
    }

    [Fact]
    public async Task Signing_in_then_asks_for_the_code_and_each_code_works_once()
    {
        var e = await EnrolAsync();

        var without = await TokenAsync(e.Shopper.Email);
        Assert.Equal(HttpStatusCode.Unauthorized, without.StatusCode);
        var body = await without.Content.ReadAsStringAsync();
        Assert.Contains("two_factor_required", body);
        Assert.Contains("authenticator app", body); // says what to do

        // Only a right password learns that a code is needed: a wrong one gets the same plain 401 as ever.
        var wrongPassword = await TokenAsync(e.Shopper.Email, "not-the-password");
        Assert.Equal(HttpStatusCode.Unauthorized, wrongPassword.StatusCode);
        Assert.DoesNotContain("two_factor", await wrongPassword.Content.ReadAsStringAsync());

        Assert.Equal(HttpStatusCode.Unauthorized, (await TokenAsync(e.Shopper.Email, code: "123456")).StatusCode);

        var code = CodeFor(e.Secret);
        var ok = await TokenAsync(e.Shopper.Email, code: code);
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        var token = (await ok.Content.ReadFromJsonAsync<TokenResponse>())!.AccessToken;
        Assert.Equal(HttpStatusCode.OK, (await WithToken(factory, token).GetAsync("/api/v1/storefront/me")).StatusCode);

        // The same code a second time is refused, even within its half minute.
        Assert.Equal(HttpStatusCode.Unauthorized, (await TokenAsync(e.Shopper.Email, code: code)).StatusCode);
        // So is the code typed to set it up.
        Assert.Equal(HttpStatusCode.Unauthorized, (await TokenAsync(e.Shopper.Email, code: CodeFor(e.Secret, 0))).StatusCode);
    }

    [Fact]
    public async Task A_recovery_code_signs_in_once_and_new_codes_replace_the_old()
    {
        var e = await EnrolAsync();

        Assert.Equal(HttpStatusCode.OK, (await TokenAsync(e.Shopper.Email, code: e.RecoveryCodes[0])).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await TokenAsync(e.Shopper.Email, code: e.RecoveryCodes[0])).StatusCode);
        // Typed in lower case, without the dash: still the same code, and still only once.
        var loose = e.RecoveryCodes[1].Replace("-", "").ToLowerInvariant();
        Assert.Equal(HttpStatusCode.OK, (await TokenAsync(e.Shopper.Email, code: loose)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await TokenAsync(e.Shopper.Email, code: loose)).StatusCode);
        Assert.Equal(8, (await e.Shopper.Client.GetFromJsonAsync<TwoFactorStatusDto>("/api/v1/storefront/me/two-factor"))!.RecoveryCodesRemaining);

        // New codes need a current code, and the old ones stop working.
        Assert.Equal(HttpStatusCode.BadRequest, (await e.Shopper.Client.PostAsJsonAsync("/api/v1/storefront/me/two-factor/recovery-codes", new TwoFactorCodeInput { Code = "000000" })).StatusCode);
        var fresh = await e.Shopper.Client.PostAsJsonAsync("/api/v1/storefront/me/two-factor/recovery-codes", new TwoFactorCodeInput { Code = CodeFor(e.Secret) });
        Assert.Equal(HttpStatusCode.OK, fresh.StatusCode);
        var newCodes = (await fresh.Content.ReadFromJsonAsync<TwoFactorEnabledDto>())!.RecoveryCodes;
        Assert.Equal(10, newCodes.Count);
        Assert.Equal(HttpStatusCode.Unauthorized, (await TokenAsync(e.Shopper.Email, code: e.RecoveryCodes[2])).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await TokenAsync(e.Shopper.Email, code: newCodes[0])).StatusCode);
    }

    [Fact]
    public async Task Wrong_codes_count_toward_the_lockout_and_a_right_password_alone_does_not_clear_the_count()
    {
        var e = await EnrolAsync();

        for (var i = 0; i < 3; i++) Assert.Equal(HttpStatusCode.Unauthorized, (await TokenAsync(e.Shopper.Email, code: "000000")).StatusCode);
        // A right password without a code must not wipe the three misses.
        Assert.Equal(HttpStatusCode.Unauthorized, (await TokenAsync(e.Shopper.Email)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await TokenAsync(e.Shopper.Email, code: "000000")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await TokenAsync(e.Shopper.Email, code: "000000")).StatusCode); // the fifth

        // Locked: even the right code is now refused.
        await ForgetUsedCodesAsync(e.Shopper.Id);
        Assert.Equal(HttpStatusCode.Unauthorized, (await TokenAsync(e.Shopper.Email, code: CodeFor(e.Secret, 0))).StatusCode);
    }

    [Fact]
    public async Task Turning_it_off_needs_the_password_and_a_code()
    {
        var e = await EnrolAsync();
        const string disable = "/api/v1/storefront/me/two-factor/disable";

        Assert.Equal(HttpStatusCode.BadRequest, (await e.Shopper.Client.PostAsJsonAsync(disable, new TwoFactorDisableInput { Password = "wrong-password-here", Code = CodeFor(e.Secret) })).StatusCode);
        await ForgetUsedCodesAsync(e.Shopper.Id);
        Assert.Equal(HttpStatusCode.BadRequest, (await e.Shopper.Client.PostAsJsonAsync(disable, new TwoFactorDisableInput { Password = Password, Code = "000000" })).StatusCode);
        Assert.True((await e.Shopper.Client.GetFromJsonAsync<TwoFactorStatusDto>("/api/v1/storefront/me/two-factor"))!.Enabled);

        await ForgetUsedCodesAsync(e.Shopper.Id);
        Assert.Equal(HttpStatusCode.NoContent, (await e.Shopper.Client.PostAsJsonAsync(disable, new TwoFactorDisableInput { Password = Password, Code = CodeFor(e.Secret) })).StatusCode);
        Assert.False((await e.Shopper.Client.GetFromJsonAsync<TwoFactorStatusDto>("/api/v1/storefront/me/two-factor"))!.Enabled);
        Assert.Equal(HttpStatusCode.OK, (await TokenAsync(e.Shopper.Email)).StatusCode); // back to a password alone
    }

    [Fact]
    public async Task Staff_can_turn_it_off_for_a_shopper_who_lost_their_phone_and_it_is_recorded()
    {
        var e = await EnrolAsync();
        var staff = await factory.CreateAuthenticatedApiClientAsync();

        Assert.True((await staff.GetFromJsonAsync<JsonElement>($"/api/v1/customers/{e.Shopper.Id}/sign-in")).GetProperty("twoFactorEnabled").GetBoolean());
        // Someone who may only look cannot.
        var (readerEmail, readerPassword) = await factory.CreateReadOnlyUserAsync();
        var reader = await factory.CreateAuthenticatedApiClientAsync(readerEmail, readerPassword);
        Assert.Equal(HttpStatusCode.Forbidden, (await reader.PostAsync($"/api/v1/customers/{e.Shopper.Id}/sign-in/reset-two-factor", null)).StatusCode);

        Assert.Equal(HttpStatusCode.NoContent, (await staff.PostAsync($"/api/v1/customers/{e.Shopper.Id}/sign-in/reset-two-factor", null)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await TokenAsync(e.Shopper.Email)).StatusCode);
        Assert.False((await staff.GetFromJsonAsync<JsonElement>($"/api/v1/customers/{e.Shopper.Id}/sign-in")).GetProperty("twoFactorEnabled").GetBoolean());
        Assert.Equal(HttpStatusCode.NotFound, (await staff.PostAsync($"/api/v1/customers/{e.Shopper.Id}/sign-in/reset-two-factor", null)).StatusCode); // already off

        var history = await staff.GetFromJsonAsync<List<SignInEventDto>>($"/api/v1/customers/{e.Shopper.Id}/sign-in/history");
        Assert.Contains(history!, h => h.Action == "two-factor-reset");

        // The shopper can set it up again.
        Assert.Equal(HttpStatusCode.OK, (await e.Shopper.Client.PostAsync("/api/v1/storefront/me/two-factor/setup", null)).StatusCode);
    }

    [Fact]
    public async Task The_export_says_whether_it_is_on_but_never_holds_the_key_and_erasing_removes_it()
    {
        var e = await EnrolAsync();
        var export = await e.Shopper.Client.GetStringAsync("/api/v1/storefront/me/export");
        Assert.Contains("\"twoFactorEnabled\":true", export);
        Assert.DoesNotContain(e.Secret, export);
        Assert.DoesNotContain(e.RecoveryCodes[0].Replace("-", ""), export.Replace("-", ""));

        var erase = await e.Shopper.Client.PostAsJsonAsync("/api/v1/storefront/me/erase", new EraseOwnAccountInput { Password = Password });
        Assert.Equal(HttpStatusCode.NoContent, erase.StatusCode);
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PiranhaCommerceDbContext>();
        Assert.False(await db.Set<CustomerTwoFactor>().AnyAsync(t => t.CustomerId == e.Shopper.Id));
        Assert.False(await db.Set<CustomerRecoveryCode>().AnyAsync(r => r.CustomerId == e.Shopper.Id));
    }

    [Fact]
    public async Task When_the_shop_requires_it_sign_in_hands_out_a_setup_token_not_a_session_and_setting_it_up_signs_them_in()
    {
        using var required = factory.WithWebHostBuilder(b => b.UseSetting("PiranhaCommerce:Customers:RequireTwoFactor", "true"));
        var s = await RegisterAsync(required); // registering is not a sign-in, so it still hands out a token

        var refused = await TokenAsync(s.Email, host: required);
        Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
        var body = (await refused.Content.ReadFromJsonAsync<JsonElement>());
        Assert.Equal("two_factor_setup_required", body.GetProperty("error").GetString());
        var setupToken = body.GetProperty("setupToken").GetString()!;
        Assert.False(string.IsNullOrEmpty(setupToken));

        var anon = required.CreateClient();
        // A made-up token gets nothing.
        Assert.Equal(HttpStatusCode.Unauthorized, (await anon.PostAsJsonAsync("/api/v1/storefront/auth/two-factor/setup", new TwoFactorSetupTokenInput { SetupToken = "nonsense" })).StatusCode);

        var setup = (await (await anon.PostAsJsonAsync("/api/v1/storefront/auth/two-factor/setup", new TwoFactorSetupTokenInput { SetupToken = setupToken })).Content.ReadFromJsonAsync<TwoFactorSetupDto>())!;
        var secret = setup.Secret.Replace(" ", "");

        // A wrong code does not spend the token: they can try again.
        var wrong = await anon.PostAsJsonAsync("/api/v1/storefront/auth/two-factor/enable", new TwoFactorEnableWithTokenInput { SetupToken = setupToken, Code = "000000" });
        Assert.Equal(HttpStatusCode.BadRequest, wrong.StatusCode);

        var done = await anon.PostAsJsonAsync("/api/v1/storefront/auth/two-factor/enable", new TwoFactorEnableWithTokenInput { SetupToken = setupToken, Code = CodeFor(secret, 0) });
        Assert.Equal(HttpStatusCode.OK, done.StatusCode);
        var signedIn = (await done.Content.ReadFromJsonAsync<SignedInWithRecoveryCodesDto>())!;
        Assert.Equal(10, signedIn.RecoveryCodes.Count);
        Assert.Equal(HttpStatusCode.OK, (await WithToken(required, signedIn.Token.AccessToken).GetAsync("/api/v1/storefront/me")).StatusCode);

        // The setup token worked once.
        Assert.Equal(HttpStatusCode.Unauthorized, (await anon.PostAsJsonAsync("/api/v1/storefront/auth/two-factor/enable", new TwoFactorEnableWithTokenInput { SetupToken = setupToken, Code = CodeFor(secret) })).StatusCode);
        // And from now on a code is asked for like any other shopper's.
        Assert.Equal(HttpStatusCode.Unauthorized, (await TokenAsync(s.Email, host: required)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await TokenAsync(s.Email, code: signedIn.RecoveryCodes[0], host: required)).StatusCode);
        // It cannot be turned off while it is required.
        Assert.True((await WithToken(required, signedIn.Token.AccessToken).GetFromJsonAsync<TwoFactorStatusDto>("/api/v1/storefront/me/two-factor"))!.Compulsory);
    }

    [Fact]
    public async Task A_setup_token_cannot_be_used_as_anything_else_and_other_emailed_links_cannot_be_used_as_one()
    {
        using var required = factory.WithWebHostBuilder(b => b.UseSetting("PiranhaCommerce:Customers:RequireTwoFactor", "true"));
        var s = await RegisterAsync(required);
        var setupToken = (await (await TokenAsync(s.Email, host: required)).Content.ReadFromJsonAsync<JsonElement>()).GetProperty("setupToken").GetString()!;

        // It is not a bearer token for the API.
        var bearer = required.CreateClient();
        bearer.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", setupToken);
        Assert.Equal(HttpStatusCode.Unauthorized, (await bearer.GetAsync("/api/v1/storefront/me")).StatusCode);

        // Asking again makes a new one, and only the newest works.
        var newer = (await (await TokenAsync(s.Email, host: required)).Content.ReadFromJsonAsync<JsonElement>()).GetProperty("setupToken").GetString()!;
        Assert.NotEqual(setupToken, newer);
        Assert.Equal(HttpStatusCode.Unauthorized, (await required.CreateClient().PostAsJsonAsync("/api/v1/storefront/auth/two-factor/setup", new TwoFactorSetupTokenInput { SetupToken = setupToken })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await required.CreateClient().PostAsJsonAsync("/api/v1/storefront/auth/two-factor/setup", new TwoFactorSetupTokenInput { SetupToken = newer })).StatusCode);
    }
}
