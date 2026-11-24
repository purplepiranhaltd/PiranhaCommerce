using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using PiranhaCommerce.Core.Identity;
using PiranhaCommerce.Core.Validation;
using PiranhaCommerce.Data;

namespace PiranhaCommerce.IntegrationTests;

public class ApiKeyTests(PiranhaCommerceFactory factory) : IClassFixture<PiranhaCommerceFactory>
{
    private async Task<T> WithAsync<T>(Func<IApiKeyService, Task<T>> action)
    {
        using var scope = factory.Services.CreateScope();
        return await action(scope.ServiceProvider.GetRequiredService<IApiKeyService>());
    }

    private async Task<(ApiKeyCreated Made, string Role)> KeyWithAsync(params string[] permissions)
    {
        var staff = await factory.CreateStaffWithRoleAsync(permissions);
        var made = await WithAsync(s => s.CreateAsync(new ApiKeyInput("Test " + Guid.NewGuid().ToString("N")[..6], [staff.Role])));
        return (made, staff.Role);
    }

    private HttpClient WithKey(string secret, bool header = false)
    {
        var client = factory.CreateClient();
        if (header) client.DefaultRequestHeaders.Add("X-Api-Key", secret);
        else client.DefaultRequestHeaders.Authorization = new("Bearer", secret);
        return client;
    }

    [Fact]
    public async Task A_key_does_what_its_roles_allow_and_no_more()
    {
        var (made, _) = await KeyWithAsync(Permissions.OrdersView);
        Assert.StartsWith("pck_", made.Secret);

        var bearer = WithKey(made.Secret);
        Assert.Equal(HttpStatusCode.OK, (await bearer.GetAsync("/api/v1/orders")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await bearer.GetAsync("/api/v1/products")).StatusCode);

        // The other header works too.
        Assert.Equal(HttpStatusCode.OK, (await WithKey(made.Secret, header: true).GetAsync("/api/v1/orders")).StatusCode);
    }

    [Fact]
    public async Task A_wrong_or_made_up_key_is_refused()
    {
        var (made, _) = await KeyWithAsync(Permissions.OrdersView);
        Assert.Equal(HttpStatusCode.Unauthorized, (await WithKey("pck_00000000_" + new string('a', 48)).GetAsync("/api/v1/orders")).StatusCode);
        // Right prefix, wrong secret.
        var forged = made.Secret[..13] + new string('b', 48);
        Assert.Equal(HttpStatusCode.Unauthorized, (await WithKey(forged).GetAsync("/api/v1/orders")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await WithKey("pck_").GetAsync("/api/v1/orders")).StatusCode);
    }

    [Fact]
    public async Task Changing_the_roles_changes_the_key_at_once_and_revoking_stops_it()
    {
        var (made, _) = await KeyWithAsync(Permissions.OrdersView);
        var client = WithKey(made.Secret);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/v1/orders")).StatusCode);

        var none = await WithAsync(s => s.SetRolesAsync(made.Key.Id, []));
        Assert.Empty(none!.Roles);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync("/api/v1/orders")).StatusCode);

        var other = await factory.CreateStaffWithRoleAsync(Permissions.OrdersView);
        await WithAsync(s => s.SetRolesAsync(made.Key.Id, [other.Role]));
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/v1/orders")).StatusCode);

        Assert.True(await WithAsync(s => s.RevokeAsync(made.Key.Id)));
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/v1/orders")).StatusCode);
        Assert.False(await WithAsync(s => s.RevokeAsync(Guid.NewGuid())));
    }

    [Fact]
    public async Task An_expired_key_is_refused()
    {
        var staff = await factory.CreateStaffWithRoleAsync(Permissions.OrdersView);
        await Assert.ThrowsAsync<PiranhaCommerceValidationException>(() =>
            WithAsync(s => s.CreateAsync(new ApiKeyInput("Past", [staff.Role], DateTimeOffset.UtcNow.AddDays(-1)))));

        var made = await WithAsync(s => s.CreateAsync(new ApiKeyInput("Short", [staff.Role], DateTimeOffset.UtcNow.AddHours(1))));
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<PiranhaCommerceDbContext>();
            await db.Set<ApiKey>().Where(k => k.Id == made.Key.Id).ExecuteUpdateAsync(s => s.SetProperty(k => k.ExpiresUtc, DateTimeOffset.UtcNow.AddMinutes(-1)));
        }
        Assert.Equal(HttpStatusCode.Unauthorized, (await WithKey(made.Secret).GetAsync("/api/v1/orders")).StatusCode);
    }

    [Fact]
    public async Task Only_a_hash_is_stored_and_the_secret_cannot_be_listed_again()
    {
        var (made, _) = await KeyWithAsync(Permissions.OrdersView);
        using var scope = factory.Services.CreateScope();
        var row = await scope.ServiceProvider.GetRequiredService<PiranhaCommerceDbContext>().Set<ApiKey>().AsNoTracking().FirstAsync(k => k.Id == made.Key.Id);
        Assert.DoesNotContain(made.Secret, row.SecretHash);
        Assert.NotEqual(made.Secret, row.SecretHash);
        var listed = (await WithAsync(s => s.ListAsync())).First(k => k.Id == made.Key.Id);
        Assert.DoesNotContain(made.Secret, listed.Prefix);
        Assert.Equal(made.Secret[..12], listed.Prefix[..12]);
    }

    [Fact]
    public async Task A_key_is_not_a_person_it_cannot_sign_in_and_is_left_out_of_the_staff_list()
    {
        var (made, _) = await KeyWithAsync(Permissions.OrdersView);
        using var scope = factory.Services.CreateScope();
        var staff = await scope.ServiceProvider.GetRequiredService<IStaffAdminService>().ListStaffAsync();
        Assert.DoesNotContain(staff, u => u.Email.EndsWith(ApiKeyFormat.ServiceDomain));

        var users = scope.ServiceProvider.GetRequiredService<UserManager<PiranhaCommerceUser>>();
        var account = users.Users.First(u => u.DisplayName == "API key: " + made.Key.Name);
        var check = await scope.ServiceProvider.GetRequiredService<ICredentialChecker>().CheckAsync(account.Email!, "anything at all 1!");
        Assert.False(check.Succeeded);
    }

    [Fact]
    public async Task A_key_cannot_open_the_shopper_api()
    {
        var (made, _) = await KeyWithAsync(Permissions.OrdersView);
        Assert.Equal(HttpStatusCode.Unauthorized, (await WithKey(made.Secret).GetAsync("/api/v1/storefront/me")).StatusCode);
    }

    [Fact]
    public async Task Making_a_key_needs_a_name_and_real_roles()
    {
        await Assert.ThrowsAsync<PiranhaCommerceValidationException>(() => WithAsync(s => s.CreateAsync(new ApiKeyInput(" ", []))));
        await Assert.ThrowsAsync<PiranhaCommerceValidationException>(() => WithAsync(s => s.CreateAsync(new ApiKeyInput("Bad", ["No such role"]))));
    }

    [Fact]
    public async Task Staff_who_cannot_manage_staff_do_not_get_the_page()
    {
        var (email, password) = await factory.CreateStaffAsync(Permissions.OrdersView);
        var browser = factory.CreateClient(new() { AllowAutoRedirect = false, HandleCookies = true });
        var form = await (await browser.GetAsync("/admin/login")).Content.ReadAsStringAsync();
        var token = System.Text.RegularExpressions.Regex.Match(form, "<input[^>]*name=\"__RequestVerificationToken\"[^>]*value=\"([^\"]+)\"").Groups[1].Value;
        await browser.PostAsync("/admin/login", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["__RequestVerificationToken"] = token, ["_handler"] = "login", ["Input.Email"] = email, ["Input.Password"] = password,
        }));
        var page = await browser.GetAsync("/admin/staff/api-keys");
        Assert.Equal(HttpStatusCode.Redirect, page.StatusCode);
        Assert.Contains("/admin/not-allowed", page.Headers.Location!.OriginalString);
    }
}
