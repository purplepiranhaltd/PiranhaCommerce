using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using PiranhaCommerce.Api.Auth;
using PiranhaCommerce.Customers;

namespace PiranhaCommerce.IntegrationTests;

public sealed class VerifiedEmailFactory : PiranhaFactory
{
    protected override IReadOnlyDictionary<string, string?> ExtraSettings => new Dictionary<string, string?> { ["PiranhaCommerce:Customers:RequireVerifiedEmail"] = "true" };
}

/// <summary>When a shop requires it, a new shopper cannot sign in until they have followed the link emailed to the address.</summary>
public class VerifiedEmailTests(VerifiedEmailFactory factory) : IClassFixture<VerifiedEmailFactory>
{
    private const string Password = "a-long-enough-password";
    private static string NewEmail() => $"verify-{Guid.NewGuid():N}@example.com";

    private async Task<(string Email, Guid Id)> RegisterAsync()
    {
        var email = NewEmail();
        var r = await factory.CreateClient().PostAsJsonAsync("/api/v1/storefront/auth/register", new RegisterInput { Email = email, Password = Password });
        Assert.Equal(HttpStatusCode.Accepted, r.StatusCode);
        var body = await r.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(body.GetProperty("verificationRequired").GetBoolean());
        Assert.False(body.TryGetProperty("token", out _)); // nothing to sign in with yet
        return (email, body.GetProperty("profile").GetProperty("id").GetGuid());
    }

    private string CodeFor(string email, string key = "customer.verify-email")
    {
        var message = factory.Email.To(email).Last(m => m.TemplateKey == key);
        EmailContract.AssertMatchesDescriptor(factory, message);
        return (string)message.Variables["code"]!;
    }

    private Task<HttpResponseMessage> SignIn(string email, string password = Password)
        => factory.CreateClient().PostAsJsonAsync("/api/v1/storefront/auth/token", new SignInInput(email, password));

    private Task<HttpResponseMessage> Verify(string email, string code)
        => factory.CreateClient().PostAsJsonAsync("/api/v1/storefront/auth/verify-email", new VerifyEmailInput { Email = email, Token = code });

    [Fact]
    public async Task A_new_shopper_cannot_sign_in_until_the_emailed_link_is_followed_and_it_works_once()
    {
        var (email, _) = await RegisterAsync();

        var refused = await SignIn(email);
        Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
        Assert.Equal("email_not_verified", (await refused.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("error").GetString());
        // Only the right password learns why: a wrong one, or an unknown address, is just refused.
        Assert.Equal(HttpStatusCode.Unauthorized, (await SignIn(email, "wrong-password-here")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await SignIn(NewEmail())).StatusCode);

        var code = CodeFor(email);
        Assert.Equal(HttpStatusCode.NoContent, (await Verify(email, code)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await SignIn(email)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await Verify(email, code)).StatusCode); // spent
    }

    [Fact]
    public async Task A_verification_link_is_for_that_address_and_that_purpose_only()
    {
        var (a, _) = await RegisterAsync();
        var (b, _) = await RegisterAsync();
        var codeA = CodeFor(a);

        Assert.Equal(HttpStatusCode.BadRequest, (await Verify(b, codeA)).StatusCode); // another address
        Assert.Equal(HttpStatusCode.BadRequest, (await Verify(a, "not-a-code")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await SignIn(b)).StatusCode);

        // A verification code cannot reset a password, and a reset code cannot verify an address.
        var reset = await factory.CreateClient().PostAsJsonAsync("/api/v1/storefront/auth/reset-password", new ResetPasswordInput { Email = a, Token = codeA, NewPassword = "another-long-password" });
        Assert.Equal(HttpStatusCode.BadRequest, reset.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await SignIn(a)).StatusCode); // still unverified, password unchanged
        await factory.CreateClient().PostAsJsonAsync("/api/v1/storefront/auth/forgot-password", new ForgotPasswordInput { Email = a });
        Assert.Equal(HttpStatusCode.BadRequest, (await Verify(a, CodeFor(a, "customer.set-password"))).StatusCode);
    }

    [Fact]
    public async Task Resending_is_silent_for_everyone_and_limited_per_hour_without_using_up_password_resets()
    {
        var (email, _) = await RegisterAsync(); // one verification email already sent
        var client = factory.CreateClient();
        Assert.Equal(HttpStatusCode.Accepted, (await client.PostAsJsonAsync("/api/v1/storefront/auth/resend-verification", new ForgotPasswordInput { Email = NewEmail() })).StatusCode);
        for (var i = 0; i < 8; i++)
            Assert.Equal(HttpStatusCode.Accepted, (await client.PostAsJsonAsync("/api/v1/storefront/auth/resend-verification", new ForgotPasswordInput { Email = email })).StatusCode);
        Assert.Equal(5, factory.Email.To(email).Count(m => m.TemplateKey == "customer.verify-email")); // the hourly cap

        await client.PostAsJsonAsync("/api/v1/storefront/auth/forgot-password", new ForgotPasswordInput { Email = email });
        Assert.Single(factory.Email.To(email), m => m.TemplateKey == "customer.set-password"); // its own limit

        // Once verified, asking again sends nothing.
        Assert.Equal(HttpStatusCode.NoContent, (await Verify(email, CodeFor(email))).StatusCode);
        var before = factory.Email.To(email).Count;
        await client.PostAsJsonAsync("/api/v1/storefront/auth/resend-verification", new ForgotPasswordInput { Email = email });
        Assert.Equal(before, factory.Email.To(email).Count);
    }

    [Fact]
    public async Task Resetting_the_password_from_an_emailed_link_also_verifies_the_address()
    {
        var (email, _) = await RegisterAsync();
        await factory.CreateClient().PostAsJsonAsync("/api/v1/storefront/auth/forgot-password", new ForgotPasswordInput { Email = email });
        var reset = await factory.CreateClient().PostAsJsonAsync("/api/v1/storefront/auth/reset-password",
            new ResetPasswordInput { Email = email, Token = CodeFor(email, "customer.set-password"), NewPassword = "another-long-password" });
        Assert.Equal(HttpStatusCode.NoContent, reset.StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await SignIn(email, "another-long-password")).StatusCode);
    }

    [Fact]
    public async Task Staff_see_whether_the_address_is_verified_can_vouch_for_it_and_changing_the_address_unverifies_it()
    {
        var staff = await factory.CreateAuthenticatedApiClientAsync();
        var (email, id) = await RegisterAsync();
        var status = await staff.GetFromJsonAsync<JsonElement>($"/api/v1/customers/{id}/sign-in");
        Assert.Equal(JsonValueKind.Null, status.GetProperty("emailVerifiedUtc").ValueKind);

        Assert.Equal(HttpStatusCode.NoContent, (await staff.PostAsync($"/api/v1/customers/{id}/sign-in/verify-email", null)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await staff.PostAsync($"/api/v1/customers/{id}/sign-in/verify-email", null)).StatusCode); // already
        Assert.NotEqual(JsonValueKind.Null, (await staff.GetFromJsonAsync<JsonElement>($"/api/v1/customers/{id}/sign-in")).GetProperty("emailVerifiedUtc").ValueKind);

        var signedIn = await SignIn(email);
        Assert.Equal(HttpStatusCode.OK, signedIn.StatusCode);
        var token = (await signedIn.Content.ReadFromJsonAsync<TokenResponse>())!.AccessToken;
        var shopper = factory.CreateClient();
        shopper.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        Assert.Equal(HttpStatusCode.OK, (await shopper.GetAsync("/api/v1/storefront/me")).StatusCode);

        // Staff correct the address: the new one is unproven, and the old session (which names the old address) ends.
        var newEmail = NewEmail();
        Assert.Equal(HttpStatusCode.OK, (await staff.PutAsJsonAsync($"/api/v1/customers/{id}", new CustomerInput { Email = newEmail })).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await shopper.GetAsync("/api/v1/storefront/me")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await SignIn(newEmail)).StatusCode);

        await factory.CreateClient().PostAsJsonAsync("/api/v1/storefront/auth/resend-verification", new ForgotPasswordInput { Email = newEmail });
        Assert.Equal(HttpStatusCode.NoContent, (await Verify(newEmail, CodeFor(newEmail))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await SignIn(newEmail)).StatusCode);

        var actions = (await staff.GetFromJsonAsync<List<JsonElement>>($"/api/v1/customers/{id}/sign-in/history"))!.Select(e => e.GetProperty("action").GetString()).ToList();
        Assert.Contains("email-verified", actions);
        Assert.Contains("email-changed", actions);

        var (readerEmail, readerPassword) = await factory.CreateReadOnlyUserAsync();
        var reader = await factory.CreateAuthenticatedApiClientAsync(readerEmail, readerPassword);
        Assert.Equal(HttpStatusCode.Forbidden, (await reader.PostAsync($"/api/v1/customers/{id}/sign-in/verify-email", null)).StatusCode);
    }
}
