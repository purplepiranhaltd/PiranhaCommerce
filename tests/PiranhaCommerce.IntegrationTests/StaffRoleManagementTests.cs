using System.Net;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;
using PiranhaCommerce.Core.Identity;
using PiranhaCommerce.Core.Validation;

namespace PiranhaCommerce.IntegrationTests;

public class StaffRoleManagementTests(PiranhaCommerceFactory factory) : IClassFixture<PiranhaCommerceFactory>
{
    private static string Unique(string p) => p + Guid.NewGuid().ToString("N")[..8];

    private async Task<T> WithServiceAsync<T>(Func<IStaffAdminService, Task<T>> action)
    {
        using var scope = factory.Services.CreateScope();
        return await action(scope.ServiceProvider.GetRequiredService<IStaffAdminService>());
    }

    [Fact]
    public async Task A_role_can_be_created_changed_renamed_and_deleted()
    {
        var name = Unique("Packer");
        var role = await WithServiceAsync(s => s.SaveRoleAsync(new StaffRoleInput(name, [Permissions.OrdersView, Permissions.OrdersFulfil])));
        Assert.Equal([Permissions.OrdersFulfil, Permissions.OrdersView], role.Permissions.OrderBy(k => k));

        var renamed = Unique("Dispatcher");
        var changed = await WithServiceAsync(s => s.SaveRoleAsync(new StaffRoleInput(renamed, [Permissions.OrdersView]), name));
        Assert.Equal(renamed, changed.Name);
        Assert.Equal([Permissions.OrdersView], changed.Permissions);
        Assert.Null(await WithServiceAsync(s => s.GetRoleAsync(name)));

        Assert.True(await WithServiceAsync(s => s.DeleteRoleAsync(renamed)));
        Assert.False(await WithServiceAsync(s => s.DeleteRoleAsync(renamed)));
    }

    [Fact]
    public async Task Role_rules_are_enforced()
    {
        await Assert.ThrowsAsync<PiranhaCommerceValidationException>(() => WithServiceAsync(s => s.SaveRoleAsync(new StaffRoleInput("  ", []))));
        await Assert.ThrowsAsync<PiranhaCommerceValidationException>(() => WithServiceAsync(s => s.SaveRoleAsync(new StaffRoleInput("Administrator", []))));
        await Assert.ThrowsAsync<PiranhaCommerceValidationException>(() => WithServiceAsync(s => s.SaveRoleAsync(new StaffRoleInput(Unique("X"), ["not.a.permission"]))));
        await Assert.ThrowsAsync<PiranhaCommerceConflictException>(() => WithServiceAsync(s => s.DeleteRoleAsync("Administrator")));

        var name = Unique("Dupe");
        await WithServiceAsync(s => s.SaveRoleAsync(new StaffRoleInput(name, [])));
        await Assert.ThrowsAsync<PiranhaCommerceValidationException>(() => WithServiceAsync(s => s.SaveRoleAsync(new StaffRoleInput(name, []))));
    }

    [Fact]
    public async Task A_role_with_people_in_it_cannot_be_deleted()
    {
        var staff = await factory.CreateStaffWithRoleAsync(Permissions.OrdersView);
        await Assert.ThrowsAsync<PiranhaCommerceConflictException>(() => WithServiceAsync(s => s.DeleteRoleAsync(staff.Role)));
    }

    [Fact]
    public async Task The_last_active_administrator_cannot_be_removed_switched_off_or_demoted()
    {
        // Work on a copy of the situation: make the seeded admin the only administrator by switching off any others first.
        using var scope = factory.Services.CreateScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<PiranhaCommerceUser>>();
        var admins = (await users.GetUsersInRoleAsync(PiranhaCommerceRoles.Administrator)).ToList();
        var only = admins.First(a => a.Email == PiranhaCommerceFactory.AdminEmail);
        var others = admins.Where(a => a.Id != only.Id && a.Active).ToList();
        foreach (var o in others) await WithServiceAsync(s => s.SetActiveAsync(o.Id, false));
        try
        {
            await Assert.ThrowsAsync<PiranhaCommerceConflictException>(() => WithServiceAsync(s => s.SetActiveAsync(only.Id, false)));
            await Assert.ThrowsAsync<PiranhaCommerceConflictException>(() => WithServiceAsync(s => s.SetRolesAsync(only.Id, [])));
            Assert.True((await WithServiceAsync(s => s.ListStaffAsync())).First(u => u.Id == only.Id).Active);

            // With a second administrator, the first may be demoted.
            var (_, _, second, _) = await factory.CreateStaffWithRoleAsync(Permissions.OrdersView);
            await WithServiceAsync(s => s.SetRolesAsync(second, [PiranhaCommerceRoles.Administrator]));
            var demoted = await WithServiceAsync(s => s.SetRolesAsync(only.Id, []));
            Assert.False(demoted!.IsAdministrator);
            await WithServiceAsync(s => s.SetRolesAsync(only.Id, [PiranhaCommerceRoles.Administrator]));
            await WithServiceAsync(s => s.SetRolesAsync(second, []));
        }
        finally
        {
            foreach (var o in others) await WithServiceAsync(s => s.SetActiveAsync(o.Id, true));
        }
    }

    [Fact]
    public async Task Changing_a_role_changes_what_the_person_can_do_straight_away()
    {
        var staff = await factory.CreateStaffWithRoleAsync(Permissions.OrdersView);
        var client = await factory.CreateAuthenticatedApiClientAsync(staff.Email, staff.Password);

        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/v1/orders")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync("/api/v1/products")).StatusCode);

        await WithServiceAsync(s => s.SaveRoleAsync(new StaffRoleInput(staff.Role, [Permissions.OrdersView, Permissions.CatalogView]), staff.Role));
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/v1/products")).StatusCode);

        await WithServiceAsync(s => s.SetActiveAsync(staff.UserId, false));
        Assert.NotEqual(HttpStatusCode.OK, (await client.GetAsync("/api/v1/products")).StatusCode);
    }

    [Fact]
    public async Task The_staff_page_is_for_people_who_manage_staff_only()
    {
        var (email, password) = await factory.CreateStaffAsync(Permissions.OrdersView);
        var browser = factory.CreateClient(new() { AllowAutoRedirect = false, HandleCookies = true });
        var form = await (await browser.GetAsync("/admin/login")).Content.ReadAsStringAsync();
        var token = System.Text.RegularExpressions.Regex.Match(form, "<input[^>]*name=\"__RequestVerificationToken\"[^>]*value=\"([^\"]+)\"").Groups[1].Value;
        await browser.PostAsync("/admin/login", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["__RequestVerificationToken"] = token, ["_handler"] = "login", ["Input.Email"] = email, ["Input.Password"] = password,
        }));
        var page = await browser.GetAsync("/admin/staff");
        Assert.Equal(HttpStatusCode.Redirect, page.StatusCode);
        Assert.Contains("/admin/not-allowed", page.Headers.Location!.OriginalString);
    }
}
