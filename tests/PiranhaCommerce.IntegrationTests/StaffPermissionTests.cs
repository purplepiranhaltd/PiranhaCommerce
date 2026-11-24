using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using PiranhaCommerce.Core.Identity;

namespace PiranhaCommerce.IntegrationTests;

public class StaffPermissionTests(PiranhaCommerceFactory factory) : IClassFixture<PiranhaCommerceFactory>
{
    private static readonly Guid Nobody = Guid.NewGuid();

    private async Task<HttpClient> StaffAsync(params string[] permissions)
    {
        var (email, password) = await factory.CreateStaffAsync(permissions);
        return await factory.CreateAuthenticatedApiClientAsync(email, password);
    }

    private static async Task<HttpStatusCode> Status(Task<HttpResponseMessage> response) => (await response).StatusCode;

    [Fact]
    public void Every_staff_endpoint_names_a_permission_and_none_fall_back_to_administrators_only()
    {
        var endpoints = factory.Services.GetServices<EndpointDataSource>().SelectMany(d => d.Endpoints).OfType<RouteEndpoint>()
            .Where(e => e.RoutePattern.RawText is { } raw && raw.StartsWith("/api/v1", StringComparison.Ordinal))
            .Where(e => !e.RoutePattern.RawText!.StartsWith("/api/v1/storefront", StringComparison.Ordinal))
            .Where(e => !e.RoutePattern.RawText!.StartsWith("/api/v1/auth", StringComparison.Ordinal))
            .Where(e => e.Metadata.GetMetadata<IAllowAnonymous>() is null)
            .ToList();
        Assert.True(endpoints.Count > 150, $"Only {endpoints.Count} staff endpoints found; the check is not looking in the right place.");

        var unguarded = new List<string>();
        foreach (var e in endpoints)
        {
            var methods = string.Join(",", e.Metadata.GetMetadata<IHttpMethodMetadata>()?.HttpMethods ?? ["?"]);
            var policies = e.Metadata.GetOrderedMetadata<IAuthorizeData>().Select(a => a.Policy).Where(p => p is not null).ToList();
            var named = policies.Where(p => p!.StartsWith(PermissionPolicies.Prefix, StringComparison.Ordinal)).ToList();
            if (named.Count == 0) unguarded.Add($"{methods} {e.RoutePattern.RawText}: names no permission");
            else if (named.All(p => p == PermissionPolicies.For(Permissions.Administrator))) unguarded.Add($"{methods} {e.RoutePattern.RawText}: administrators only by default");
        }
        Assert.True(unguarded.Count == 0, "Staff endpoints without a permission:\n" + string.Join("\n", unguarded));
    }

    [Fact]
    public async Task A_user_with_no_role_can_sign_in_but_reaches_nothing()
    {
        var (email, password) = await factory.CreateUserWithNoRoleAsync();
        var client = await factory.CreateAuthenticatedApiClientAsync(email, password);
        Assert.Equal(HttpStatusCode.Forbidden, await Status(client.GetAsync("/api/v1/products")));
        Assert.Equal(HttpStatusCode.Forbidden, await Status(client.GetAsync("/api/v1/orders")));
        Assert.Equal(HttpStatusCode.Forbidden, await Status(client.GetAsync("/api/v1/customers")));
        Assert.Equal(HttpStatusCode.Forbidden, await Status(client.GetAsync("/api/v1/shipping-zones"))); // even reference data needs some role
    }

    [Fact]
    public async Task A_role_that_can_fulfil_can_ship_and_move_an_order_on_but_not_refund_or_see_the_catalogue()
    {
        var client = await StaffAsync(Permissions.OrdersFulfil);

        // Authorised calls get past the permission check (and then find no such order), so never 403.
        Assert.NotEqual(HttpStatusCode.Forbidden, await Status(client.PostAsJsonAsync($"/api/v1/orders/{Nobody}/stage", new { Stage = "Shipped" })));
        Assert.Equal(HttpStatusCode.OK, await Status(client.GetAsync("/api/v1/orders"))); // fulfil implies view

        Assert.Equal(HttpStatusCode.Forbidden, await Status(client.PostAsJsonAsync($"/api/v1/orders/{Nobody}/refunds", new { })));
        Assert.Equal(HttpStatusCode.Forbidden, await Status(client.PostAsJsonAsync($"/api/v1/orders/{Nobody}/payments/{Nobody}/refund", new { })));
        Assert.Equal(HttpStatusCode.Forbidden, await Status(client.GetAsync("/api/v1/products")));
        Assert.Equal(HttpStatusCode.Forbidden, await Status(client.GetAsync("/api/v1/customers")));
    }

    [Fact]
    public async Task A_role_that_can_refund_cannot_move_orders_on()
    {
        var client = await StaffAsync(Permissions.OrdersRefund);
        Assert.Equal(HttpStatusCode.Forbidden, await Status(client.PostAsJsonAsync($"/api/v1/orders/{Nobody}/stage", new { Stage = "Shipped" })));
        Assert.NotEqual(HttpStatusCode.Forbidden, await Status(client.PostAsJsonAsync($"/api/v1/orders/{Nobody}/refunds", new { })));
    }

    [Fact]
    public async Task Editing_the_catalogue_includes_viewing_it_but_not_changing_stock_or_prices()
    {
        var client = await StaffAsync(Permissions.CatalogEdit);
        Assert.Equal(HttpStatusCode.OK, await Status(client.GetAsync("/api/v1/products")));
        Assert.Equal(HttpStatusCode.Forbidden, await Status(client.PostAsJsonAsync($"/api/v1/products/{Nobody}/stock/adjustments", new { })));
        Assert.Equal(HttpStatusCode.Forbidden, await Status(client.PostAsJsonAsync("/api/v1/sales", new { })));

        var stock = await StaffAsync(Permissions.StockAdjust);
        Assert.Equal(HttpStatusCode.OK, await Status(stock.GetAsync("/api/v1/stock/low")));
        Assert.Equal(HttpStatusCode.Forbidden, await Status(stock.PostAsJsonAsync("/api/v1/products", new { Name = "Nope", Price = 1m })));
    }

    [Fact]
    public async Task Viewing_a_report_needs_its_own_permission_and_so_does_the_audit_log()
    {
        var orders = await StaffAsync(Permissions.OrdersView);
        Assert.Equal(HttpStatusCode.Forbidden, await Status(orders.GetAsync("/api/v1/reports/sales")));
        Assert.Equal(HttpStatusCode.Forbidden, await Status(orders.GetAsync("/api/v1/audit")));

        var reports = await StaffAsync(Permissions.ReportsView);
        Assert.Equal(HttpStatusCode.OK, await Status(reports.GetAsync("/api/v1/reports/sales")));
        var audit = await StaffAsync(Permissions.AuditView);
        Assert.Equal(HttpStatusCode.OK, await Status(audit.GetAsync("/api/v1/audit")));
    }

    [Fact]
    public async Task Changing_a_role_takes_effect_on_the_same_token_without_signing_in_again()
    {
        var (email, password, userId, role) = await factory.CreateStaffWithRoleAsync(Permissions.OrdersView);
        var client = await factory.CreateAuthenticatedApiClientAsync(email, password);
        Assert.Equal(HttpStatusCode.OK, await Status(client.GetAsync("/api/v1/orders")));

        using (var scope = factory.Services.CreateScope())
        {
            var roles = scope.ServiceProvider.GetRequiredService<RoleManager<PiranhaCommerceRole>>();
            var r = (await roles.FindByNameAsync(role))!;
            Assert.True((await roles.RemoveClaimAsync(r, new Claim(StaffPermissionService.ClaimType, Permissions.OrdersView))).Succeeded);
        }
        factory.Services.GetRequiredService<StaffPermissionService>().Invalidate();
        Assert.Equal(HttpStatusCode.Forbidden, await Status(client.GetAsync("/api/v1/orders")));

        // And a user who is switched off loses everything at once, token or not.
        using (var scope = factory.Services.CreateScope())
        {
            var users = scope.ServiceProvider.GetRequiredService<UserManager<PiranhaCommerceUser>>();
            var roles = scope.ServiceProvider.GetRequiredService<RoleManager<PiranhaCommerceRole>>();
            var r = (await roles.FindByNameAsync(role))!;
            await roles.AddClaimAsync(r, new Claim(StaffPermissionService.ClaimType, Permissions.OrdersView));
            var u = (await users.FindByIdAsync(userId.ToString()))!;
            u.Active = false;
            Assert.True((await users.UpdateAsync(u)).Succeeded);
        }
        factory.Services.GetRequiredService<StaffPermissionService>().Invalidate();
        Assert.Equal(HttpStatusCode.Forbidden, await Status(client.GetAsync("/api/v1/orders")));
    }

    [Fact]
    public async Task An_administrator_holds_every_permission_including_ones_a_package_adds()
    {
        var catalog = factory.Services.GetRequiredService<PermissionCatalog>();
        Assert.True(catalog.Exists("warranty.edit")); // contributed by the example plugin
        Assert.Contains(catalog.All, p => p.Key == Permissions.OrdersRefund);

        var service = factory.Services.GetRequiredService<StaffPermissionService>();
        var admin = await Principal(PiranhaCommerceFactory.AdminEmail);
        Assert.True(await service.HasAsync(admin, "warranty.edit"));
        Assert.True(await service.HasAsync(admin, Permissions.Administrator));
        Assert.True(await service.HasAsync(admin, "something.nobody.defined.yet"));
    }

    [Fact]
    public async Task A_plugins_own_permission_guards_its_endpoint_and_the_permission_it_implies_is_included()
    {
        var viewer = await StaffAsync(Permissions.CatalogView);
        Assert.Equal(HttpStatusCode.Forbidden, await Status(viewer.PutAsJsonAsync($"/api/v1/products/{Nobody}/warranty", new { })));

        var editor = await StaffAsync("warranty.edit");
        Assert.NotEqual(HttpStatusCode.Forbidden, await Status(editor.PutAsJsonAsync($"/api/v1/products/{Nobody}/warranty", new { })));
        Assert.Equal(HttpStatusCode.NotFound, await Status(editor.GetAsync($"/api/v1/products/{Nobody}/warranty"))); // warranty.edit implies catalog.view
    }

    [Fact]
    public void Implied_permissions_are_followed_all_the_way_and_unknown_keys_are_ignored()
    {
        var catalog = factory.Services.GetRequiredService<PermissionCatalog>();
        var set = catalog.Expand([Permissions.OrdersRefund, "no.such.permission"]);
        Assert.Contains(Permissions.OrdersView, set);
        Assert.DoesNotContain("no.such.permission", set);
        Assert.DoesNotContain(Permissions.OrdersFulfil, set);
    }

    [Fact]
    public async Task An_endpoint_that_names_no_permission_is_for_administrators_only()
    {
        // What a plugin written before permissions (or one that forgot) looks like: only "a signed-in member of staff".
        using var host = factory.WithWebHostBuilder(b => b.ConfigureTestServices(s => s.AddSingleton<PiranhaCommerce.Api.IApiEndpointContributor, LegacyEndpoint>()));
        var admin = await factory.CreateAuthenticatedApiClientAsync();
        var (email, password) = await factory.CreateStaffAsync(Permissions.CatalogEdit, Permissions.OrdersFulfil, Permissions.StaffManage);

        var adminClient = host.CreateClient();
        adminClient.DefaultRequestHeaders.Authorization = admin.DefaultRequestHeaders.Authorization;
        Assert.Equal(HttpStatusCode.OK, await Status(adminClient.GetAsync("/api/v1/legacy-check")));

        var staff = await factory.CreateAuthenticatedApiClientAsync(email, password);
        var staffClient = host.CreateClient();
        staffClient.DefaultRequestHeaders.Authorization = staff.DefaultRequestHeaders.Authorization;
        Assert.Equal(HttpStatusCode.Forbidden, await Status(staffClient.GetAsync("/api/v1/legacy-check")));
    }

    private sealed class LegacyEndpoint : PiranhaCommerce.Api.IApiEndpointContributor
    {
        public void MapEndpoints(IEndpointRouteBuilder api) =>
            api.MapGet("/legacy-check", () => Results.Ok("ok")).RequireAuthorization(PiranhaCommerce.Api.ApiPolicies.Read);
    }

    private async Task<ClaimsPrincipal> Principal(string email)
    {
        using var scope = factory.Services.CreateScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<PiranhaCommerceUser>>();
        var user = (await users.FindByEmailAsync(email))!;
        return new ClaimsPrincipal(new ClaimsIdentity([new Claim("sub", user.Id.ToString())], "test"));
    }
}
