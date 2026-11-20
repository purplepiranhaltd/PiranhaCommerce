using System.Net;
using System.Net.Http.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using PiranhaCommerce.Core.Admin;
using PiranhaCommerce.Catalog.Admin;

namespace PiranhaCommerce.IntegrationTests;

public partial class AdminTests(PiranhaFactory factory) : IClassFixture<PiranhaFactory>
{
    [GeneratedRegex(@"<input[^>]*name=""__RequestVerificationToken""[^>]*value=""([^""]+)""")]
    private static partial Regex TokenRegex();

    private HttpClient NewBrowser() => factory.CreateClient(new() { AllowAutoRedirect = false, HandleCookies = true });

    private static async Task<string> AntiforgeryTokenAsync(HttpResponseMessage page)
    {
        var m = TokenRegex().Match(await page.Content.ReadAsStringAsync());
        Assert.True(m.Success, "antiforgery token not found in login form");
        return m.Groups[1].Value;
    }

    private static async Task<HttpResponseMessage> PostLoginAsync(HttpClient browser, string email, string password, string? returnUrl = null)
    {
        var form = await browser.GetAsync("/admin/login");
        var token = await AntiforgeryTokenAsync(form);
        return await browser.PostAsync("/admin/login" + (returnUrl is null ? "" : $"?returnUrl={Uri.EscapeDataString(returnUrl)}"),
            new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["__RequestVerificationToken"] = token,
                ["_handler"] = "login",
                ["Input.Email"] = email,
                ["Input.Password"] = password,
            }));
    }

    [Fact]
    public async Task Plugin_page_assembly_is_registered_with_the_admin()
    {
        var assemblies = factory.Services.GetServices<AdminPageAssembly>().Select(a => a.Assembly).ToList();
        Assert.Contains(typeof(CatalogAdminPlugin).Assembly, assemblies);

        // ...and its @page routes are actually mapped as endpoints, not just its menu entry.
        var routes = factory.Services.GetServices<EndpointDataSource>()
            .SelectMany(d => d.Endpoints).OfType<RouteEndpoint>()
            .Select(e => e.RoutePattern.RawText).ToList();
        Assert.Contains("/admin/catalog/products", routes);
        Assert.Contains("/admin/catalog/products/new", routes);
        await Task.CompletedTask;
    }

    [Fact]
    public async Task Blazor_script_is_served_so_the_admin_can_become_interactive()
    {
        var response = await NewBrowser().GetAsync("/_framework/blazor.web.js");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("javascript", response.Content.Headers.ContentType?.MediaType);
    }

    [Fact]
    public async Task Admin_pages_redirect_anonymous_users_to_login()
    {
        var response = await NewBrowser().GetAsync("/admin/catalog/products");
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        var location = response.Headers.Location!;
        Assert.Equal("/admin/login", location.IsAbsoluteUri ? location.AbsolutePath : location.OriginalString.Split('?')[0]);
        Assert.Contains("returnUrl=%2Fadmin%2Fcatalog%2Fproducts", location.ToString());
    }

    [Fact]
    public async Task Login_page_renders_for_anonymous_users()
    {
        var response = await NewBrowser().GetAsync("/admin/login");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("Sign in", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Wrong_password_shows_error_and_does_not_sign_in()
    {
        var browser = NewBrowser();
        var response = await PostLoginAsync(browser, PiranhaFactory.AdminEmail, "wrong");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("Invalid email or password", await response.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.Redirect, (await browser.GetAsync("/admin")).StatusCode);
    }

    [Fact]
    public async Task Admin_can_sign_in_and_see_plugin_pages_and_menu()
    {
        var browser = NewBrowser();
        var login = await PostLoginAsync(browser, PiranhaFactory.AdminEmail, PiranhaFactory.AdminPassword, "/admin/catalog/products");
        Assert.Equal(HttpStatusCode.Redirect, login.StatusCode);
        Assert.Equal("/admin/catalog/products", login.Headers.Location?.ToString().Replace("http://localhost", ""));

        var products = await browser.GetAsync("/admin/catalog/products");
        Assert.Equal(HttpStatusCode.OK, products.StatusCode);
        var html = await products.Content.ReadAsStringAsync();
        Assert.Contains("<h1>Products</h1>", html);

        var dashboard = await (await browser.GetAsync("/admin")).Content.ReadAsStringAsync();
        Assert.Contains("Ecommerce Admin", dashboard);          // plugin listed
        Assert.Contains("/admin/catalog/products", dashboard);  // plugin menu entry rendered
    }

    [Fact]
    public async Task Product_edit_page_shows_the_ecommerce_panel_only_for_saved_products()
    {
        var api = await factory.CreateAuthenticatedApiClientAsync();
        var created = await api.PostAsJsonAsync("/api/v1/products", new PiranhaCommerce.Catalog.ProductInput { Name = "Panel test", Price = 1m });
        var product = (await created.Content.ReadFromJsonAsync<PiranhaCommerce.Catalog.ProductDto>())!;

        var browser = NewBrowser();
        await PostLoginAsync(browser, PiranhaFactory.AdminEmail, PiranhaFactory.AdminPassword);

        var existing = await (await browser.GetAsync($"/admin/catalog/products/{product.Id}")).Content.ReadAsStringAsync();
        Assert.Contains("Online store", existing);
        Assert.Contains("<h2 style=\"margin-top:0\">Categories</h2>", existing); // Catalog panel in its own slot           // Ecommerce panel rendered inside the Catalog page
        Assert.Contains("Published on the online store", existing);

        var isNew = await (await browser.GetAsync("/admin/catalog/products/new")).Content.ReadAsStringAsync();
        Assert.DoesNotContain("Published on the online store", isNew);
        Assert.Contains("Save the product to configure", isNew);
    }

    [Fact]
    public async Task Non_admin_user_cannot_use_the_admin_area()
    {
        var (email, password) = await factory.CreateReadOnlyUserAsync();
        var browser = NewBrowser();
        Assert.Equal(HttpStatusCode.Redirect, (await PostLoginAsync(browser, email, password)).StatusCode);

        var page = await browser.GetAsync("/admin/catalog/products");
        Assert.Contains("does not have permission", await page.Content.ReadAsStringAsync());
    }

    [Theory]
    [InlineData("//evil.example/x")]
    [InlineData("https://evil.example/x")]
    [InlineData("/\\evil.example")]
    public async Task Login_ignores_non_local_return_urls(string returnUrl)
    {
        var login = await PostLoginAsync(NewBrowser(), PiranhaFactory.AdminEmail, PiranhaFactory.AdminPassword, returnUrl);
        Assert.Equal(HttpStatusCode.Redirect, login.StatusCode);
        Assert.DoesNotContain("evil.example", login.Headers.Location?.ToString());
    }

    [Fact]
    public async Task Categories_page_renders_and_is_in_the_menu()
    {
        var browser = NewBrowser();
        await PostLoginAsync(browser, PiranhaFactory.AdminEmail, PiranhaFactory.AdminPassword);
        var page = await browser.GetAsync("/admin/catalog/categories");
        Assert.Equal(HttpStatusCode.OK, page.StatusCode);
        Assert.Contains("<h1>Categories</h1>", await page.Content.ReadAsStringAsync());
    }
}
