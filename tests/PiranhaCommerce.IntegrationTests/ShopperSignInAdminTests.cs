using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using PiranhaCommerce.Api.Auth;
using Microsoft.Extensions.DependencyInjection;
using PiranhaCommerce.Customers;

namespace PiranhaCommerce.IntegrationTests;

/// <summary>What staff can do about a shopper's sign-in, and that every way of ending a session really ends it.</summary>
public class ShopperSignInAdminTests(PiranhaCommerceFactory factory) : IClassFixture<PiranhaCommerceFactory>
{
    private const string Password = "a-long-enough-password";
    private static string NewEmail() => $"signin-{Guid.NewGuid():N}@example.com";

    private sealed record Shopper(HttpClient Client, Guid Id, string Email, string Token);

    private HttpClient WithToken(string token)
    {
        var c = factory.CreateClient();
        c.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return c;
    }

    private async Task<Shopper> RegisterAsync()
    {
        var email = NewEmail();
        var r = await factory.CreateClient().PostAsJsonAsync("/api/v1/storefront/auth/register", new RegisterInput { Email = email, Password = Password });
        Assert.Equal(HttpStatusCode.Created, r.StatusCode);
        var reg = (await r.Content.ReadFromJsonAsync<RegisteredDto>())!;
        return new Shopper(WithToken(reg.Token.AccessToken), reg.Profile.Id, email, reg.Token.AccessToken);
    }

    private async Task<string> SignInAsync(string email, string password = Password)
    {
        var r = await factory.CreateClient().PostAsJsonAsync("/api/v1/storefront/auth/token", new SignInInput(email, password));
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        return (await r.Content.ReadFromJsonAsync<TokenResponse>())!.AccessToken;
    }

    /// <summary>The token is refused on the profile AND on a basket call (a different endpoint group), so no route skips the check.</summary>
    private static async Task AssertRefusedAsync(HttpClient client)
    {
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/v1/storefront/me")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.PostAsync("/api/v1/storefront/baskets", null)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/v1/storefront/orders")).StatusCode);
    }

    private static async Task AssertAcceptedAsync(HttpClient client)
    {
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/v1/storefront/me")).StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await client.PostAsync("/api/v1/storefront/baskets", null)).StatusCode);
    }

    private static async Task<JsonElement> StatusAsync(HttpClient staff, Guid id)
        => (await staff.GetFromJsonAsync<JsonElement>($"/api/v1/customers/{id}/sign-in"));

    [Fact]
    public async Task Signing_out_everywhere_ends_every_live_token_but_signing_in_again_works_at_once()
    {
        var staff = await factory.CreateAuthenticatedApiClientAsync();
        var s = await RegisterAsync();
        var second = WithToken(await SignInAsync(s.Email)); // another device
        await AssertAcceptedAsync(s.Client);
        await AssertAcceptedAsync(second);

        Assert.Equal(HttpStatusCode.NoContent, (await staff.PostAsync($"/api/v1/customers/{s.Id}/sign-in/sign-out", null)).StatusCode);
        await AssertRefusedAsync(s.Client);
        await AssertRefusedAsync(second);

        // Straight after: a counter, not a clock, so there is no second in which the new token could be caught by the revoke.
        await AssertAcceptedAsync(WithToken(await SignInAsync(s.Email)));
    }

    [Fact]
    public async Task Switching_the_customer_off_ends_their_session_and_switching_back_on_does_not_revive_it()
    {
        var staff = await factory.CreateAuthenticatedApiClientAsync();
        var s = await RegisterAsync();
        await AssertAcceptedAsync(s.Client);

        Assert.Equal(HttpStatusCode.OK, (await staff.PutAsJsonAsync($"/api/v1/customers/{s.Id}", new CustomerInput { Email = s.Email, Active = false })).StatusCode);
        await AssertRefusedAsync(s.Client);
        Assert.Equal("Disabled", (await StatusAsync(staff, s.Id)).GetProperty("state").GetString());
        Assert.Equal(HttpStatusCode.Unauthorized, (await factory.CreateClient().PostAsJsonAsync("/api/v1/storefront/auth/token", new SignInInput(s.Email, Password))).StatusCode);

        // Switched back on: the old token stays dead (switching off ended the session for good); signing in afresh works.
        Assert.Equal(HttpStatusCode.OK, (await staff.PutAsJsonAsync($"/api/v1/customers/{s.Id}", new CustomerInput { Email = s.Email, Active = true })).StatusCode);
        await AssertRefusedAsync(s.Client);
        await AssertAcceptedAsync(WithToken(await SignInAsync(s.Email)));
    }

    [Fact]
    public async Task Removing_the_sign_in_ends_the_session_and_an_old_reset_link_cannot_bring_it_back()
    {
        var staff = await factory.CreateAuthenticatedApiClientAsync();
        var s = await RegisterAsync();

        // A link emailed before the removal.
        await factory.CreateClient().PostAsJsonAsync("/api/v1/storefront/auth/forgot-password", new ForgotPasswordInput { Email = s.Email });
        var code = (string)factory.Email.To(s.Email).Last(m => m.TemplateKey == "customer.set-password").Variables["code"]!;

        Assert.Equal(HttpStatusCode.NoContent, (await staff.DeleteAsync($"/api/v1/customers/{s.Id}/sign-in")).StatusCode);
        await AssertRefusedAsync(s.Client);
        Assert.Equal(HttpStatusCode.Unauthorized, (await factory.CreateClient().PostAsJsonAsync("/api/v1/storefront/auth/token", new SignInInput(s.Email, Password))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await factory.CreateClient().PostAsJsonAsync("/api/v1/storefront/auth/reset-password",
            new ResetPasswordInput { Email = s.Email, Token = code, NewPassword = "another-long-password" })).StatusCode);

        // The customer and the state say what happened; a second removal has nothing to remove.
        Assert.Equal(HttpStatusCode.OK, (await staff.GetAsync($"/api/v1/customers/{s.Id}")).StatusCode);
        var status = await StatusAsync(staff, s.Id);
        Assert.Equal(("None", false), (status.GetProperty("state").GetString(), status.GetProperty("hasSignIn").GetBoolean()));
        Assert.Equal(HttpStatusCode.NotFound, (await staff.DeleteAsync($"/api/v1/customers/{s.Id}/sign-in")).StatusCode);
    }

    [Fact]
    public async Task Changing_the_password_ends_other_sessions_and_hands_back_a_token_that_works()
    {
        var s = await RegisterAsync();
        var otherDevice = WithToken(await SignInAsync(s.Email));
        const string next = "another-long-password";

        var changed = await s.Client.PostAsJsonAsync("/api/v1/storefront/me/password", new ChangePasswordInput { CurrentPassword = Password, NewPassword = next });
        Assert.Equal(HttpStatusCode.OK, changed.StatusCode);
        var fresh = (await changed.Content.ReadFromJsonAsync<TokenResponse>())!;

        await AssertRefusedAsync(otherDevice);
        await AssertRefusedAsync(s.Client); // the token it was called with is spent as well
        await AssertAcceptedAsync(WithToken(fresh.AccessToken));
    }

    [Fact]
    public async Task Resetting_the_password_with_a_link_ends_every_session()
    {
        var s = await RegisterAsync();
        await factory.CreateClient().PostAsJsonAsync("/api/v1/storefront/auth/forgot-password", new ForgotPasswordInput { Email = s.Email });
        var code = (string)factory.Email.To(s.Email).Last(m => m.TemplateKey == "customer.set-password").Variables["code"]!;

        var reset = await factory.CreateClient().PostAsJsonAsync("/api/v1/storefront/auth/reset-password",
            new ResetPasswordInput { Email = s.Email, Token = code, NewPassword = "another-long-password" });
        Assert.Equal(HttpStatusCode.NoContent, reset.StatusCode);
        await AssertRefusedAsync(s.Client);
        await AssertAcceptedAsync(WithToken(await SignInAsync(s.Email, "another-long-password")));
    }

    [Fact]
    public async Task A_locked_shopper_is_shown_as_locked_and_can_sign_in_again_once_staff_unlock_them()
    {
        var staff = await factory.CreateAuthenticatedApiClientAsync();
        var s = await RegisterAsync();
        var anon = factory.CreateClient();
        for (var i = 0; i < 5; i++)
            await anon.PostAsJsonAsync("/api/v1/storefront/auth/token", new SignInInput(s.Email, "wrong-password-" + i));
        Assert.Equal(HttpStatusCode.Unauthorized, (await anon.PostAsJsonAsync("/api/v1/storefront/auth/token", new SignInInput(s.Email, Password))).StatusCode);

        var locked = await StatusAsync(staff, s.Id);
        Assert.Equal("Locked", locked.GetProperty("state").GetString());
        Assert.NotEqual(JsonValueKind.Null, locked.GetProperty("lockedUntil").ValueKind);

        Assert.Equal(HttpStatusCode.NoContent, (await staff.PostAsync($"/api/v1/customers/{s.Id}/sign-in/unlock", null)).StatusCode);
        Assert.Equal("Active", (await StatusAsync(staff, s.Id)).GetProperty("state").GetString());
        await AssertAcceptedAsync(WithToken(await SignInAsync(s.Email)));
    }

    [Fact]
    public async Task A_customer_staff_made_has_no_sign_in_and_the_reset_link_is_their_invitation()
    {
        var staff = await factory.CreateAuthenticatedApiClientAsync();
        var email = NewEmail();
        var made = (await (await staff.PostAsJsonAsync("/api/v1/customers", new CustomerInput { Email = email, FirstName = "Pat" })).Content.ReadFromJsonAsync<JsonElement>());
        var id = made.GetProperty("id").GetGuid();
        Assert.Equal("None", (await StatusAsync(staff, id)).GetProperty("state").GetString());
        Assert.Equal(HttpStatusCode.NotFound, (await staff.PostAsync($"/api/v1/customers/{id}/sign-in/unlock", null)).StatusCode); // nothing to unlock

        var sent = await staff.PostAsync($"/api/v1/customers/{id}/sign-in/reset-link", null);
        Assert.Equal(HttpStatusCode.OK, sent.StatusCode);
        var result = await sent.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(result.GetProperty("sent").GetBoolean());
        Assert.Equal("Sent", result.GetProperty("outcome").GetString());

        var message = Assert.Single(factory.Email.To(email), m => m.TemplateKey == "customer.set-password");
        var code = (string)message.Variables["code"]!;
        Assert.Equal(HttpStatusCode.NoContent, (await factory.CreateClient().PostAsJsonAsync("/api/v1/storefront/auth/reset-password",
            new ResetPasswordInput { Email = email, Token = code, NewPassword = Password })).StatusCode);

        Assert.Equal("Active", (await StatusAsync(staff, id)).GetProperty("state").GetString());
        await AssertAcceptedAsync(WithToken(await SignInAsync(email)));
    }

    [Fact]
    public async Task Staff_are_told_why_no_link_was_sent()
    {
        var staff = await factory.CreateAuthenticatedApiClientAsync();

        // Switched off: nothing sent.
        var off = await RegisterAsync();
        await staff.PutAsJsonAsync($"/api/v1/customers/{off.Id}", new CustomerInput { Email = off.Email, Active = false });
        var inactive = await (await staff.PostAsync($"/api/v1/customers/{off.Id}/sign-in/reset-link", null)).Content.ReadFromJsonAsync<JsonElement>();
        Assert.False(inactive.GetProperty("sent").GetBoolean());
        Assert.Equal("Inactive", inactive.GetProperty("outcome").GetString());
        Assert.DoesNotContain(factory.Email.To(off.Email), m => m.TemplateKey == "customer.set-password");

        // Over the hourly limit: staff sends count towards it.
        var busy = await RegisterAsync();
        string outcome = "";
        for (var i = 0; i < 7; i++)
        {
            var r = await (await staff.PostAsync($"/api/v1/customers/{busy.Id}/sign-in/reset-link", null)).Content.ReadFromJsonAsync<JsonElement>();
            outcome = r.GetProperty("outcome").GetString()!;
            if (outcome != "Sent") break;
        }
        Assert.Equal("RateLimited", outcome);
        Assert.Equal(5, factory.Email.To(busy.Email).Count(m => m.TemplateKey == "customer.set-password"));

        // The mail server is down: the staff member is told, not shown a false "sent".
        var down = await RegisterAsync();
        factory.Email.Fail = true;
        try
        {
            var failed = await (await staff.PostAsync($"/api/v1/customers/{down.Id}/sign-in/reset-link", null)).Content.ReadFromJsonAsync<JsonElement>();
            Assert.False(failed.GetProperty("sent").GetBoolean());
            Assert.Equal("SendFailed", failed.GetProperty("outcome").GetString());
        }
        finally { factory.Email.Fail = false; }

        Assert.Equal(HttpStatusCode.NotFound, (await staff.PostAsync($"/api/v1/customers/{Guid.NewGuid()}/sign-in/reset-link", null)).StatusCode);
    }

    [Fact]
    public async Task Only_staff_who_may_write_can_act_and_the_status_never_carries_the_hash_or_version()
    {
        var staff = await factory.CreateAuthenticatedApiClientAsync();
        var s = await RegisterAsync();
        var (email, password) = await factory.CreateReadOnlyUserAsync();
        var reader = await factory.CreateAuthenticatedApiClientAsync(email, password);

        var raw = await (await reader.GetAsync($"/api/v1/customers/{s.Id}/sign-in")).Content.ReadAsStringAsync();
        Assert.Contains("\"state\":\"Active\"", raw);
        Assert.DoesNotContain("hash", raw, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("version", raw, StringComparison.OrdinalIgnoreCase);

        foreach (var path in new[] { "unlock", "sign-out", "reset-link" })
        {
            Assert.Equal(HttpStatusCode.Forbidden, (await reader.PostAsync($"/api/v1/customers/{s.Id}/sign-in/{path}", null)).StatusCode);
            Assert.Equal(HttpStatusCode.Unauthorized, (await factory.CreateClient().PostAsync($"/api/v1/customers/{s.Id}/sign-in/{path}", null)).StatusCode);
            Assert.Equal(HttpStatusCode.Unauthorized, (await s.Client.PostAsync($"/api/v1/customers/{s.Id}/sign-in/{path}", null)).StatusCode); // a shopper token is not staff
        }
        Assert.Equal(HttpStatusCode.Forbidden, (await reader.DeleteAsync($"/api/v1/customers/{s.Id}/sign-in")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await s.Client.GetAsync($"/api/v1/customers/{s.Id}/sign-in")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await staff.GetAsync($"/api/v1/customers/{Guid.NewGuid()}/sign-in")).StatusCode);

        // None of those attempts touched the shopper.
        await AssertAcceptedAsync(s.Client);
    }

    [Fact]
    public async Task Everything_staff_do_about_a_sign_in_is_recorded_with_who_did_it_and_goes_with_the_customer_on_erasure()
    {
        var staff = await factory.CreateAuthenticatedApiClientAsync();
        var s = await RegisterAsync();

        for (var i = 0; i < 5; i++)
            await factory.CreateClient().PostAsJsonAsync("/api/v1/storefront/auth/token", new SignInInput(s.Email, "wrong-password-" + i));
        await staff.PostAsync($"/api/v1/customers/{s.Id}/sign-in/unlock", null);
        await staff.PostAsync($"/api/v1/customers/{s.Id}/sign-in/sign-out", null);
        await staff.PostAsync($"/api/v1/customers/{s.Id}/sign-in/reset-link", null);
        await staff.PutAsJsonAsync($"/api/v1/customers/{s.Id}", new CustomerInput { Email = s.Email, Active = false });
        await staff.PutAsJsonAsync($"/api/v1/customers/{s.Id}", new CustomerInput { Email = s.Email, Active = true });
        await staff.PutAsJsonAsync($"/api/v1/customers/{s.Id}", new CustomerInput { Email = s.Email, FirstName = "Edited", Active = true }); // not a sign-in action
        await staff.DeleteAsync($"/api/v1/customers/{s.Id}/sign-in");
        await staff.PostAsync($"/api/v1/customers/{s.Id}/sign-in/unlock", null); // refused (no sign-in): nothing to record

        var history = (await staff.GetFromJsonAsync<List<JsonElement>>($"/api/v1/customers/{s.Id}/sign-in/history"))!;
        Assert.Equal(new[] { "sign-in-removed", "switched-on", "switched-off", "reset-link", "signed-out", "unlocked" },
            history.Select(e => e.GetProperty("action").GetString()!).ToArray()); // newest first
        Assert.All(history, e => Assert.Equal("admin@piranha.local", e.GetProperty("actor").GetString()));
        Assert.Equal("Sent", history.Single(e => e.GetProperty("action").GetString() == "reset-link").GetProperty("detail").GetString());

        // A read-only user may read the history, a shopper may not, and an unknown customer is not found.
        var (email, password) = await factory.CreateReadOnlyUserAsync();
        var reader = await factory.CreateAuthenticatedApiClientAsync(email, password);
        Assert.Equal(HttpStatusCode.OK, (await reader.GetAsync($"/api/v1/customers/{s.Id}/sign-in/history")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await s.Client.GetAsync($"/api/v1/customers/{s.Id}/sign-in/history")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await staff.GetAsync($"/api/v1/customers/{Guid.NewGuid()}/sign-in/history")).StatusCode);

        // It is the customer's data: in their export, and gone when they are erased.
        var export = await staff.GetFromJsonAsync<JsonElement>($"/api/v1/customers/{s.Id}/export");
        Assert.Equal(6, export.GetProperty("signInHistory").GetArrayLength());
        Assert.Equal(HttpStatusCode.OK, (await staff.PostAsJsonAsync($"/api/v1/customers/{s.Id}/erase", new EraseCustomerInput { ConfirmEmail = s.Email })).StatusCode);
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PiranhaCommerce.Data.PiranhaCommerceDbContext>();
        Assert.False(await Microsoft.EntityFrameworkCore.EntityFrameworkQueryableExtensions.AnyAsync(db.Set<CustomerSignInEvent>(), e => e.CustomerId == s.Id));
    }
}
