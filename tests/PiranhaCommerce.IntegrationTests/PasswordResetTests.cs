using System.Net;
using System.Net.Http.Json;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using PiranhaCommerce.Customers;
using PiranhaCommerce.Data;

namespace PiranhaCommerce.IntegrationTests;

public partial class PasswordResetTests(PiranhaCommerceFactory factory) : IClassFixture<PiranhaCommerceFactory>
{
    private const string Password = "a-long-enough-password";
    private const string NewPassword = "brand-new-password-1";

    [GeneratedRegex(@"[0-9a-f]{32}\.[A-Za-z0-9_-]+")]
    private static partial Regex CodeRegex();

    private static string NewEmail() => $"reset-{Guid.NewGuid():N}@example.com";

    private async Task RegisterAsync(string email)
    {
        var r = await factory.CreateClient().PostAsJsonAsync("/api/v1/storefront/auth/register", new RegisterInput { Email = email, Password = Password });
        Assert.Equal(HttpStatusCode.Created, r.StatusCode);
    }

    private async Task<HttpResponseMessage> ForgotAsync(string email) =>
        await factory.CreateClient().PostAsJsonAsync("/api/v1/storefront/auth/forgot-password", new ForgotPasswordInput { Email = email });

    private Task<HttpResponseMessage> ResetAsync(string email, string code, string password = NewPassword) =>
        factory.CreateClient().PostAsJsonAsync("/api/v1/storefront/auth/reset-password", new ResetPasswordInput { Email = email, Token = code, NewPassword = password });

    private async Task<string> CodeSentToAsync(string email)
    {
        var mails = factory.Email.To(email);
        Assert.NotEmpty(mails);
        Assert.Equal("customer.set-password", mails[^1].TemplateKey);
        EmailContract.AssertMatchesDescriptor(factory, mails[^1]);
        Assert.Equal(email, mails[^1].Variables["email"]);
        var m = CodeRegex().Match((string)mails[^1].Variables["code"]!);
        Assert.True(m.Success, "no code in the email");
        return m.Value;
    }

    private async Task<HttpStatusCode> SignInAsync(string email, string password) =>
        (await factory.CreateClient().PostAsJsonAsync("/api/v1/storefront/auth/token", new SignInInput(email, password))).StatusCode;

    [Fact]
    public async Task A_shopper_who_forgot_their_password_sets_a_new_one_from_the_emailed_code_and_the_old_one_stops_working()
    {
        var email = NewEmail();
        await RegisterAsync(email);
        Assert.Equal(HttpStatusCode.Accepted, (await ForgotAsync(email)).StatusCode);

        var code = await CodeSentToAsync(email);
        Assert.Equal(HttpStatusCode.NoContent, (await ResetAsync(email.ToUpperInvariant(), code)).StatusCode);

        Assert.Equal(HttpStatusCode.Unauthorized, await SignInAsync(email, Password));
        Assert.Equal(HttpStatusCode.OK, await SignInAsync(email, NewPassword));
    }

    [Fact]
    public async Task Asking_for_a_reset_looks_the_same_for_a_customer_and_for_a_stranger_and_sends_nothing_to_the_stranger()
    {
        var known = NewEmail();
        await RegisterAsync(known);
        var stranger = NewEmail();

        var a = await ForgotAsync(known);
        var b = await ForgotAsync(stranger);
        Assert.Equal(a.StatusCode, b.StatusCode);
        Assert.Equal(await a.Content.ReadAsStringAsync(), await b.Content.ReadAsStringAsync());
        Assert.Single(factory.Email.To(known));
        Assert.Empty(factory.Email.To(stranger));
    }

    [Fact]
    public async Task A_customer_made_by_staff_gets_a_sign_in_by_proving_they_own_the_address()
    {
        var staff = await factory.CreateAuthenticatedApiClientAsync();
        var email = NewEmail();
        Assert.Equal(HttpStatusCode.Created, (await staff.PostAsJsonAsync("/api/v1/customers", new CustomerInput { Email = email })).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, await SignInAsync(email, Password)); // no sign-in yet

        await ForgotAsync(email);
        Assert.Equal(HttpStatusCode.NoContent, (await ResetAsync(email, await CodeSentToAsync(email))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, await SignInAsync(email, NewPassword));
    }

    [Fact]
    public async Task A_code_works_once_only_and_only_for_its_own_address()
    {
        var email = NewEmail();
        var other = NewEmail();
        await RegisterAsync(email);
        await RegisterAsync(other);
        await ForgotAsync(email);
        var code = await CodeSentToAsync(email);

        Assert.Equal(HttpStatusCode.BadRequest, (await ResetAsync(other, code)).StatusCode);       // someone else's address
        Assert.Equal(HttpStatusCode.BadRequest, (await ResetAsync(email, "nonsense")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await ResetAsync(email, code[..^3] + "abc")).StatusCode); // altered secret
        Assert.Equal(HttpStatusCode.NoContent, (await ResetAsync(email, code)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await ResetAsync(email, code, "yet-another-password")).StatusCode); // already used
        Assert.Equal(HttpStatusCode.OK, await SignInAsync(email, NewPassword));
        Assert.Equal(HttpStatusCode.OK, await SignInAsync(other, Password)); // the other customer was never touched
    }

    [Fact]
    public async Task A_weak_new_password_is_refused_without_using_up_the_code()
    {
        var email = NewEmail();
        await RegisterAsync(email);
        await ForgotAsync(email);
        var code = await CodeSentToAsync(email);

        Assert.Equal(HttpStatusCode.BadRequest, (await ResetAsync(email, code, "short")).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await ResetAsync(email, code)).StatusCode);
    }

    [Fact]
    public async Task Using_the_same_code_many_times_at_once_succeeds_exactly_once()
    {
        var email = NewEmail();
        await RegisterAsync(email);
        await ForgotAsync(email);
        var code = await CodeSentToAsync(email);

        var results = await Task.WhenAll(Enumerable.Range(0, 5).Select(i => ResetAsync(email, code, $"password-number-{i}-long")));
        Assert.Equal(1, results.Count(r => r.StatusCode == HttpStatusCode.NoContent));
        Assert.Equal(4, results.Count(r => r.StatusCode == HttpStatusCode.BadRequest));
    }

    [Fact]
    public async Task An_expired_code_is_refused()
    {
        var email = NewEmail();
        await RegisterAsync(email);
        await ForgotAsync(email);
        var code = await CodeSentToAsync(email);

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<PiranhaCommerceDbContext>();
            var id = Guid.ParseExact(code.Split('.')[0], "N");
            await db.Set<CustomerToken>().Where(t => t.Id == id).ExecuteUpdateAsync(s => s.SetProperty(t => t.ExpiresUtc, DateTimeOffset.UtcNow.AddMinutes(-1)));
        }
        Assert.Equal(HttpStatusCode.BadRequest, (await ResetAsync(email, code)).StatusCode);
    }

    [Fact]
    public async Task A_reset_also_unlocks_an_account_that_was_locked_by_wrong_guesses()
    {
        var email = NewEmail();
        await RegisterAsync(email);
        for (var i = 0; i < 5; i++) await SignInAsync(email, "wrong-password-" + i);
        Assert.Equal(HttpStatusCode.Unauthorized, await SignInAsync(email, Password)); // locked

        await ForgotAsync(email);
        Assert.Equal(HttpStatusCode.NoContent, (await ResetAsync(email, await CodeSentToAsync(email))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, await SignInAsync(email, NewPassword));
    }

    [Fact]
    public async Task The_form_cannot_be_used_to_flood_someones_inbox()
    {
        var email = NewEmail();
        await RegisterAsync(email);
        for (var i = 0; i < 9; i++) Assert.Equal(HttpStatusCode.Accepted, (await ForgotAsync(email)).StatusCode);
        Assert.Equal(5, factory.Email.To(email).Count);
    }

    [Fact]
    public async Task A_broken_mail_server_does_not_change_the_answer()
    {
        var email = NewEmail();
        await RegisterAsync(email);
        factory.Email.Fail = true;
        try { Assert.Equal(HttpStatusCode.Accepted, (await ForgotAsync(email)).StatusCode); }
        finally { factory.Email.Fail = false; }
    }

    [Fact]
    public async Task A_deactivated_customer_gets_no_email_and_cannot_use_an_earlier_code()
    {
        var staff = await factory.CreateAuthenticatedApiClientAsync();
        var email = NewEmail();
        var reg = await factory.CreateClient().PostAsJsonAsync("/api/v1/storefront/auth/register", new RegisterInput { Email = email, Password = Password });
        var id = (await reg.Content.ReadFromJsonAsync<RegisteredDto>())!.Profile.Id;
        await ForgotAsync(email);
        var code = await CodeSentToAsync(email);

        Assert.Equal(HttpStatusCode.OK, (await staff.PutAsJsonAsync($"/api/v1/customers/{id}", new CustomerInput { Email = email, Active = false })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await ResetAsync(email, code)).StatusCode);
        await ForgotAsync(email);
        Assert.Single(factory.Email.To(email)); // only the first
    }
}
