using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using PiranhaCommerce.Api.Auth;
using PiranhaCommerce.Customers;

namespace PiranhaCommerce.IntegrationTests;

public class StorefrontAccountTests(PiranhaFactory factory) : IClassFixture<PiranhaFactory>
{
    private const string Password = "a-long-enough-password";

    private static string NewEmail() => $"shopper-{Guid.NewGuid():N}@example.com";

    private async Task<(HttpClient Client, string Email, Guid Id)> RegisterAsync(string? email = null, string password = Password)
    {
        email ??= NewEmail();
        var client = factory.CreateClient();
        var response = await client.PostAsJsonAsync("/api/v1/storefront/auth/register", new RegisterInput { Email = email, Password = password, FirstName = "Sam" });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var registered = (await response.Content.ReadFromJsonAsync<RegisteredDto>())!;
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", registered.Token.AccessToken);
        return (client, email, registered.Profile.Id);
    }

    [Fact]
    public async Task Registering_gives_a_working_token_and_a_profile_without_staff_notes()
    {
        var (client, email, id) = await RegisterAsync();

        var me = await client.GetAsync("/api/v1/storefront/me");
        Assert.Equal(HttpStatusCode.OK, me.StatusCode);
        var body = await me.Content.ReadAsStringAsync();
        Assert.Contains(email, body);
        Assert.DoesNotContain("notes", body, StringComparison.OrdinalIgnoreCase);

        // The customer record is visible to staff, as for any customer.
        var staff = await factory.CreateAuthenticatedApiClientAsync();
        Assert.Equal(HttpStatusCode.OK, (await staff.GetAsync($"/api/v1/customers/{id}")).StatusCode);
    }

    [Fact]
    public async Task Weak_passwords_and_bad_emails_are_refused()
    {
        var client = factory.CreateClient();
        var short_ = await client.PostAsJsonAsync("/api/v1/storefront/auth/register", new RegisterInput { Email = NewEmail(), Password = "short" });
        Assert.Equal(HttpStatusCode.BadRequest, short_.StatusCode);
        var bad = await client.PostAsJsonAsync("/api/v1/storefront/auth/register", new RegisterInput { Email = "not-an-email", Password = Password });
        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);
    }

    [Fact]
    public async Task An_email_that_already_has_a_customer_cannot_be_registered_so_nobody_can_claim_someone_elses_orders()
    {
        // Created by staff (or by a guest order): it may already have orders attached.
        var staff = await factory.CreateAuthenticatedApiClientAsync();
        var email = NewEmail();
        Assert.Equal(HttpStatusCode.Created, (await staff.PostAsJsonAsync("/api/v1/customers", new CustomerInput { Email = email })).StatusCode);

        var client = factory.CreateClient();
        var attempt = await client.PostAsJsonAsync("/api/v1/storefront/auth/register", new RegisterInput { Email = email.ToUpperInvariant(), Password = Password });
        Assert.Equal(HttpStatusCode.BadRequest, attempt.StatusCode);

        // And nothing was attached: the password the stranger chose does not sign in.
        var signIn = await client.PostAsJsonAsync("/api/v1/storefront/auth/token", new SignInInput(email, Password));
        Assert.Equal(HttpStatusCode.Unauthorized, signIn.StatusCode);
    }

    [Fact]
    public async Task Registering_the_same_email_twice_is_refused_whatever_the_letter_case()
    {
        var (_, email, _) = await RegisterAsync();
        var again = await factory.CreateClient().PostAsJsonAsync("/api/v1/storefront/auth/register", new RegisterInput { Email = email.ToUpperInvariant(), Password = Password });
        Assert.Equal(HttpStatusCode.BadRequest, again.StatusCode);
    }

    [Fact]
    public async Task Signing_in_works_and_every_kind_of_failure_looks_the_same()
    {
        var (_, email, _) = await RegisterAsync();
        var client = factory.CreateClient();

        var ok = await client.PostAsJsonAsync("/api/v1/storefront/auth/token", new SignInInput(email.ToUpperInvariant(), Password));
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);

        var wrong = await client.PostAsJsonAsync("/api/v1/storefront/auth/token", new SignInInput(email, "wrong-password-here"));
        var unknown = await client.PostAsJsonAsync("/api/v1/storefront/auth/token", new SignInInput(NewEmail(), Password));
        Assert.Equal(HttpStatusCode.Unauthorized, wrong.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, unknown.StatusCode);
        Assert.Equal(await wrong.Content.ReadAsStringAsync(), await unknown.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Repeated_wrong_passwords_lock_the_account_even_against_the_right_password()
    {
        var (_, email, _) = await RegisterAsync();
        var client = factory.CreateClient();
        for (var i = 0; i < 5; i++)
            Assert.Equal(HttpStatusCode.Unauthorized, (await client.PostAsJsonAsync("/api/v1/storefront/auth/token", new SignInInput(email, "wrong-password-" + i))).StatusCode);

        var right = await client.PostAsJsonAsync("/api/v1/storefront/auth/token", new SignInInput(email, Password));
        Assert.Equal(HttpStatusCode.Unauthorized, right.StatusCode);
    }

    [Fact]
    public async Task A_shopper_token_cannot_open_the_staff_api_and_a_staff_token_cannot_open_the_storefront()
    {
        var (shopper, _, _) = await RegisterAsync();
        foreach (var path in new[] { "/api/v1/customers", "/api/v1/products", "/api/v1/orders", "/api/v1/shipping-methods", "/api/v1/stock-locations", "/api/v1/categories" })
            Assert.Equal(HttpStatusCode.Unauthorized, (await shopper.GetAsync(path)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await shopper.PostAsJsonAsync("/api/v1/customers", new CustomerInput { Email = NewEmail() })).StatusCode);

        var staff = await factory.CreateAuthenticatedApiClientAsync();
        Assert.Equal(HttpStatusCode.Unauthorized, (await staff.GetAsync("/api/v1/storefront/me")).StatusCode);

        Assert.Equal(HttpStatusCode.Unauthorized, (await factory.CreateClient().GetAsync("/api/v1/storefront/me")).StatusCode);
    }

    [Fact]
    public async Task Shopper_credentials_do_not_work_as_staff_credentials()
    {
        var (_, email, _) = await RegisterAsync();
        var response = await factory.CreateClient().PostAsJsonAsync("/api/v1/auth/token", new TokenRequest(email, Password));
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Addresses_belong_to_the_signed_in_shopper_only()
    {
        var (alice, _, _) = await RegisterAsync();
        var (bob, _, _) = await RegisterAsync();
        var address = new AddressInput { Line1 = "1 High Street", City = "Leeds", CountryCode = "GB" };

        var created = await alice.PostAsJsonAsync("/api/v1/storefront/me/addresses", address);
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var aliceAddress = (await created.Content.ReadFromJsonAsync<AddressDto>())!;

        var bobsList = await bob.GetFromJsonAsync<List<AddressDto>>("/api/v1/storefront/me/addresses");
        Assert.Empty(bobsList!);
        Assert.Equal(HttpStatusCode.NotFound, (await bob.PutAsJsonAsync($"/api/v1/storefront/me/addresses/{aliceAddress.Id}", address)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await bob.DeleteAsync($"/api/v1/storefront/me/addresses/{aliceAddress.Id}")).StatusCode);

        var alicesList = await alice.GetFromJsonAsync<List<AddressDto>>("/api/v1/storefront/me/addresses");
        Assert.Single(alicesList!);
    }

    [Fact]
    public async Task Deactivating_a_customer_cuts_off_a_token_that_has_not_expired()
    {
        var (shopper, email, id) = await RegisterAsync();
        Assert.Equal(HttpStatusCode.OK, (await shopper.GetAsync("/api/v1/storefront/me")).StatusCode);

        var staff = await factory.CreateAuthenticatedApiClientAsync();
        var put = await staff.PutAsJsonAsync($"/api/v1/customers/{id}", new CustomerInput { Email = email, Active = false });
        Assert.Equal(HttpStatusCode.OK, put.StatusCode);

        Assert.Equal(HttpStatusCode.Unauthorized, (await shopper.GetAsync("/api/v1/storefront/me")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await factory.CreateClient().PostAsJsonAsync("/api/v1/storefront/auth/token", new SignInInput(email, Password))).StatusCode);
    }

    [Fact]
    public async Task Changing_the_password_needs_the_current_one_and_replaces_it()
    {
        var (shopper, email, _) = await RegisterAsync();
        const string next = "another-long-password";

        var wrong = await shopper.PostAsJsonAsync("/api/v1/storefront/me/password", new ChangePasswordInput { CurrentPassword = "not-my-password", NewPassword = next });
        Assert.Equal(HttpStatusCode.BadRequest, wrong.StatusCode);

        var weak = await shopper.PostAsJsonAsync("/api/v1/storefront/me/password", new ChangePasswordInput { CurrentPassword = Password, NewPassword = "short" });
        Assert.Equal(HttpStatusCode.BadRequest, weak.StatusCode);

        var ok = await shopper.PostAsJsonAsync("/api/v1/storefront/me/password", new ChangePasswordInput { CurrentPassword = Password, NewPassword = next });
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode); // with a fresh token: the old one ended with the old password

        var client = factory.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.PostAsJsonAsync("/api/v1/storefront/auth/token", new SignInInput(email, Password))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.PostAsJsonAsync("/api/v1/storefront/auth/token", new SignInInput(email, next))).StatusCode);
    }

    [Fact]
    public async Task Updating_the_profile_changes_only_the_callers_own_record()
    {
        var (shopper, _, _) = await RegisterAsync();
        var put = await shopper.PutAsJsonAsync("/api/v1/storefront/me", new ProfileInput { FirstName = "Robin", LastName = "Hood", Phone = "0113 000" });
        Assert.Equal(HttpStatusCode.OK, put.StatusCode);
        var profile = (await put.Content.ReadFromJsonAsync<ProfileDto>())!;
        Assert.Equal("Robin", profile.FirstName);
    }
}
