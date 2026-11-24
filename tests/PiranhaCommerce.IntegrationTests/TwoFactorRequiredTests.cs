using System.Net;
using Microsoft.AspNetCore.Mvc.Testing;
using System.Net.Http.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.DependencyInjection;
using PiranhaCommerce.Core.Identity;
using PiranhaCommerce.Core.Validation;

namespace PiranhaCommerce.IntegrationTests;

/// <summary>Two-factor sign-in is compulsory for staff (the default outside development). The rest of the suite runs with it off.</summary>
public class TwoFactorRequiredTests(PiranhaCommerceFactory factory) : IClassFixture<PiranhaCommerceFactory>
{
    private WebApplicationFactory<Program> Required() =>
        factory.WithWebHostBuilder(b => b.UseSetting("PiranhaCommerce:Staff:RequireTwoFactor", "true"));

    private static async Task<HttpClient> BrowserSignedInAsync(WebApplicationFactory<Program> host, string email, string password)
    {
        var browser = host.CreateClient(new() { AllowAutoRedirect = false, HandleCookies = true });
        var form = await (await browser.GetAsync("/admin/login")).Content.ReadAsStringAsync();
        var token = Regex.Match(form, "<input[^>]*name=\"__RequestVerificationToken\"[^>]*value=\"([^\"]+)\"").Groups[1].Value;
        var login = await browser.PostAsync("/admin/login", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["__RequestVerificationToken"] = token, ["_handler"] = "login", ["Input.Email"] = email, ["Input.Password"] = password,
        }));
        Assert.Equal(HttpStatusCode.Redirect, login.StatusCode);
        return browser;
    }

    private static async Task<string> EnrolAsync(WebApplicationFactory<Program> host, Guid userId)
    {
        using var scope = host.Services.CreateScope();
        var security = scope.ServiceProvider.GetRequiredService<IStaffSecurityService>();
        var setup = await security.BeginSetupAsync(userId);
        var key = Regex.Match(setup.Uri, "secret=([A-Z2-7]+)").Groups[1].Value;
        await security.ConfirmSetupAsync(userId, TwoFactorTests.CodeFor(key));
        return key;
    }

    [Fact]
    public async Task The_api_gives_no_token_until_two_factor_is_set_up()
    {
        var staff = await factory.CreateStaffWithRoleAsync(Permissions.OrdersView);
        using var host = Required();
        var client = host.CreateClient();

        var refused = await client.PostAsJsonAsync("/api/v1/auth/token", new { email = staff.Email, password = staff.Password });
        Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
        Assert.Contains("two_factor_setup_required", await refused.Content.ReadAsStringAsync());

        var key = await EnrolAsync(host, staff.UserId);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.PostAsJsonAsync("/api/v1/auth/token", new { email = staff.Email, password = staff.Password })).StatusCode);
        var ok = await client.PostAsJsonAsync("/api/v1/auth/token", new { email = staff.Email, password = staff.Password, code = TwoFactorTests.CodeFor(key) });
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
    }

    [Fact]
    public async Task In_the_admin_someone_without_it_can_only_reach_the_page_that_sets_it_up()
    {
        var staff = await factory.CreateStaffWithRoleAsync(Permissions.OrdersView, Permissions.CatalogView);
        using var host = Required();
        var browser = await BrowserSignedInAsync(host, staff.Email, staff.Password);

        var orders = await browser.GetAsync("/admin/orders");
        Assert.Equal(HttpStatusCode.Redirect, orders.StatusCode);
        Assert.Contains("/admin/not-allowed", orders.Headers.Location!.OriginalString);

        // The dead end sends them to set it up.
        var denied = await browser.GetAsync("/admin/not-allowed");
        Assert.Contains("/admin/account/security", denied.Headers.Location?.OriginalString ?? "");

        var security = await browser.GetAsync("/admin/account/security");
        Assert.Equal(HttpStatusCode.OK, security.StatusCode);
        var html = await security.Content.ReadAsStringAsync();
        Assert.Contains("Two-factor sign-in is required", html);
        Assert.DoesNotContain("href=\"/admin/orders\"", html); // the menu shows nothing else

        // Once set up they are let in straight away, in the same session.
        await EnrolAsync(host, staff.UserId);
        Assert.Equal(HttpStatusCode.OK, (await browser.GetAsync("/admin/orders")).StatusCode);
    }

    [Fact]
    public async Task It_cannot_be_turned_off_but_an_administrator_can_reset_it_and_the_person_must_set_it_up_again()
    {
        var staff = await factory.CreateStaffWithRoleAsync(Permissions.OrdersView);
        using var host = Required();
        var key = await EnrolAsync(host, staff.UserId);

        using (var scope = host.Services.CreateScope())
        {
            var security = scope.ServiceProvider.GetRequiredService<IStaffSecurityService>();
            Assert.True((await security.GetStatusAsync(staff.UserId)).Required);
            await Assert.ThrowsAsync<PiranhaCommerceConflictException>(() => security.DisableAsync(staff.UserId, TwoFactorTests.CodeFor(key)));
            await scope.ServiceProvider.GetRequiredService<IStaffAdminService>().ResetTwoFactorAsync(staff.UserId);
        }

        var refused = await host.CreateClient().PostAsJsonAsync("/api/v1/auth/token", new { email = staff.Email, password = staff.Password });
        Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode); // reset, so they have to enrol again
    }

    [Fact]
    public async Task An_api_key_is_not_a_person_and_needs_no_two_factor()
    {
        var staff = await factory.CreateStaffWithRoleAsync(Permissions.OrdersView);
        using var host = Required();
        ApiKeyCreated made;
        using (var scope = host.Services.CreateScope())
            made = await scope.ServiceProvider.GetRequiredService<IApiKeyService>().CreateAsync(new ApiKeyInput("No second factor " + Guid.NewGuid().ToString("N")[..6], [staff.Role]));

        var client = host.CreateClient();
        client.DefaultRequestHeaders.Authorization = new("Bearer", made.Secret);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/v1/orders")).StatusCode);
    }

    [Fact]
    public async Task Switching_it_off_in_settings_lets_people_sign_in_with_a_password_alone()
    {
        var staff = await factory.CreateStaffWithRoleAsync(Permissions.OrdersView); // the suite's host has it off
        var ok = await factory.CreateClient().PostAsJsonAsync("/api/v1/auth/token", new { email = staff.Email, password = staff.Password });
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
    }
}
