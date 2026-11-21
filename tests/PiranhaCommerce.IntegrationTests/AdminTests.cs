using System.Net;
using System.Net.Http.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using PiranhaCommerce.Core.Admin;
using PiranhaCommerce.Catalog.Admin;

namespace PiranhaCommerce.IntegrationTests;

[Collection(OrderSettings.Name)]
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

    private async Task<string> DescribeAdminAsync()
    {
        using var scope = factory.Services.CreateScope();
        var users = scope.ServiceProvider.GetRequiredService<Microsoft.AspNetCore.Identity.UserManager<PiranhaCommerce.Core.Identity.PiranhaUser>>();
        var u = await users.FindByEmailAsync(PiranhaFactory.AdminEmail);
        if (u is null) return "user NOT FOUND";
        return $"active={u.Active} lockoutEnd={u.LockoutEnd?.ToString() ?? "none"} failedCount={u.AccessFailedCount} " +
               $"passwordOk={await users.CheckPasswordAsync(u, PiranhaFactory.AdminPassword)} inRole={await users.IsInRoleAsync(u, PiranhaCommerce.Core.Identity.PiranhaRoles.Administrator)}";
    }

    /// <summary>Signs in as the seeded admin and fails with the server's answer if that did not work (rather than a later, confusing 302).</summary>
    private async Task SignInAsync(HttpClient browser)
    {
        var login = await PostLoginAsync(browser, PiranhaFactory.AdminEmail, PiranhaFactory.AdminPassword);
        if (login.StatusCode != HttpStatusCode.Redirect)
        {
            var body = await login.Content.ReadAsStringAsync();
            var error = System.Text.RegularExpressions.Regex.Match(body, "pc-error[^>]*>([^<]+)").Groups[1].Value;
            Assert.Fail($"Sign-in did not succeed: HTTP {(int)login.StatusCode}. Page said: '{error}'. Account state: {await DescribeAdminAsync()}");
        }
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
        await SignInAsync(browser);

        var existing = await (await browser.GetAsync($"/admin/catalog/products/{product.Id}")).Content.ReadAsStringAsync();
        Assert.Contains("Online store", existing);
        Assert.Contains("<h2 style=\"margin-top:0\">Categories</h2>", existing); // Catalog panel in its own slot           // Ecommerce panel rendered inside the Catalog page
        Assert.Contains("<h2 style=\"margin-top:0\">Barcodes</h2>", existing);
        Assert.Contains("<h2 style=\"margin-top:0\">Stock</h2>", existing);
        Assert.Contains("<h2 style=\"margin-top:0\">Warranty</h2>", existing); // third-party plugin panel in a core page
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
        await SignInAsync(browser);
        var page = await browser.GetAsync("/admin/catalog/categories");
        Assert.Equal(HttpStatusCode.OK, page.StatusCode);
        Assert.Contains("<h1>Categories</h1>", await page.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Stock_location_and_negative_stock_pages_render()
    {
        var browser = NewBrowser();
        await SignInAsync(browser);

        var locations = await browser.GetAsync("/admin/catalog/stock-locations");
        Assert.Equal(HttpStatusCode.OK, locations.StatusCode);
        Assert.Contains("<h1>Stock locations</h1>", await locations.Content.ReadAsStringAsync());

        var negative = await browser.GetAsync("/admin/catalog/negative-stock");
        Assert.Equal(HttpStatusCode.OK, negative.StatusCode);
        Assert.Contains("<h1>Negative stock</h1>", await negative.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Customer_pages_render_and_the_edit_page_shows_the_address_panel_only_for_saved_customers()
    {
        var api = await factory.CreateAuthenticatedApiClientAsync();
        var created = await api.PostAsJsonAsync("/api/v1/customers",
            new PiranhaCommerce.Customers.CustomerInput { Email = $"admin-{Guid.NewGuid():N}@example.com", LastName = "Panel" });
        var customer = (await created.Content.ReadFromJsonAsync<PiranhaCommerce.Customers.CustomerDto>())!;

        var browser = NewBrowser();
        await SignInAsync(browser);

        var list = await browser.GetAsync("/admin/customers");
        Assert.Equal(HttpStatusCode.OK, list.StatusCode);
        var listHtml = await list.Content.ReadAsStringAsync();
        Assert.Contains("<h1>Customers</h1>", listHtml);
        Assert.Contains("<th>Sign-in</th>", listHtml);

        var existing = await (await browser.GetAsync($"/admin/customers/{customer.Id}")).Content.ReadAsStringAsync();
        Assert.Contains("<h2 style=\"margin-top:0\">Addresses</h2>", existing);   // contributed through the customer slot
        Assert.Contains("<h2 style=\"margin-top:0\">Privacy</h2>", existing);
        Assert.Contains("<h2 style=\"margin-top:0\">Sign-in</h2>", existing);
        Assert.Contains("Erase personal data", existing);

        var fresh = await (await browser.GetAsync("/admin/customers/new")).Content.ReadAsStringAsync();
        Assert.DoesNotContain("<h2 style=\"margin-top:0\">Addresses</h2>", fresh);
        Assert.DoesNotContain("Erase personal data", fresh);
        Assert.Contains("Save the customer to manage their addresses.", fresh);
    }

    [Fact]
    public async Task Order_pages_render_with_a_copy_of_the_sale_and_the_allowed_stage_buttons()
    {
        var api = await factory.CreateAuthenticatedApiClientAsync();
        var product = (await (await api.PostAsJsonAsync("/api/v1/products",
            new PiranhaCommerce.Catalog.ProductInput { Name = "Admin order " + Guid.NewGuid().ToString("N")[..8], Price = 12.5m })).Content
            .ReadFromJsonAsync<PiranhaCommerce.Catalog.ProductDto>())!;
        await api.PutAsJsonAsync($"/api/v1/products/{product.Id}/listing", new PiranhaCommerce.Ecommerce.ProductListingInput { Published = true });
        await api.PostAsJsonAsync($"/api/v1/products/{product.Id}/stock/adjustments", new PiranhaCommerce.Catalog.StockAdjustmentInput { Delta = 5, Reason = "Delivery" });
        var basket = (await (await api.PostAsJsonAsync("/api/v1/baskets", new PiranhaCommerce.Ecommerce.BasketCreateInput())).Content
            .ReadFromJsonAsync<PiranhaCommerce.Ecommerce.BasketDto>(Json))!;
        await api.PutAsJsonAsync($"/api/v1/baskets/{basket.Id}/lines/{product.Id}", new PiranhaCommerce.Ecommerce.BasketLineInput { Quantity = 2 });
        var email = $"adminorder-{Guid.NewGuid():N}@example.com";
        var placed = await api.PostAsJsonAsync($"/api/v1/baskets/{basket.Id}/checkout", new PiranhaCommerce.Ecommerce.CheckoutInput
        {
            Email = email,
            ShippingAddress = new PiranhaCommerce.Customers.AddressInput { Recipient = "Ada", Line1 = "1 High St", City = "Leeds", CountryCode = "GB" },
        });
        var order = (await placed.Content.ReadFromJsonAsync<PiranhaCommerce.Ecommerce.OrderDto>(Json))!;

        var browser = NewBrowser();
        await SignInAsync(browser);

        var list = await (await browser.GetAsync("/admin/orders")).Content.ReadAsStringAsync();
        Assert.Contains("<h1>Orders</h1>", list);
        Assert.Contains(order.Reference, list);

        var detail = await browser.GetAsync($"/admin/orders/{order.Id}");
        Assert.Equal(HttpStatusCode.OK, detail.StatusCode);
        var html = await detail.Content.ReadAsStringAsync();
        Assert.Contains($"Order {order.Reference}", html);
        Assert.Contains(product.Name, html);
        Assert.Contains("25.00", html);                // 2 x 12.50
        Assert.Contains("1 High St", html);
        Assert.Contains("Mark as processing", html);
        Assert.Contains("Cancel order", html);
        Assert.DoesNotContain("Mark as completed", html); // cannot skip a stage
        Assert.Contains("<h2 style=\"margin-top:0\">Sending</h2>", html);
        Assert.Contains("<h2 style=\"margin-top:0\">Returns</h2>", html);
        Assert.Contains("Move the order to Processing", html);

        Assert.Contains("Payment: Unpaid", html);
        Assert.Contains("Record payment", html);
        Assert.Contains("Shipping", html);
        Assert.Contains("Tax", html);
        Assert.Contains("<h1>Shipping methods</h1>", await (await browser.GetAsync("/admin/orders/shipping")).Content.ReadAsStringAsync());
        Assert.Contains("<h1>Tax</h1>", await (await browser.GetAsync("/admin/orders/tax")).Content.ReadAsStringAsync());
        Assert.Contains("<h1>Promotions</h1>", await (await browser.GetAsync("/admin/orders/promotions")).Content.ReadAsStringAsync());
        Assert.Contains("<h1>Gift vouchers</h1>", await (await browser.GetAsync("/admin/orders/vouchers")).Content.ReadAsStringAsync());
        Assert.Contains("<h1>Unreconciled payments</h1>", await (await browser.GetAsync("/admin/orders/unreconciled")).Content.ReadAsStringAsync());
        Assert.Contains("<h1>Email templates</h1>", await (await browser.GetAsync("/admin/email/templates")).Content.ReadAsStringAsync());
        Assert.Contains("Order confirmation", await (await browser.GetAsync("/admin/email/templates/order.confirmation")).Content.ReadAsStringAsync());

        var missing = await (await browser.GetAsync($"/admin/orders/{Guid.NewGuid()}")).Content.ReadAsStringAsync();
        Assert.Contains("That order no longer exists.", missing);
    }

    private static readonly System.Text.Json.JsonSerializerOptions Json =
        new(System.Text.Json.JsonSerializerDefaults.Web) { Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() } };

    /// <summary>
    /// ASP.NET Identity refuses a CORRECT password when resetting the failed-attempt counter loses a concurrency race against another
    /// simultaneous sign-in (an anti-guessing measure). A real user must not be told their password is wrong because of that.
    /// </summary>
    [Fact]
    public async Task Simultaneous_correct_sign_ins_after_a_typo_all_succeed()
    {
        var (email, password) = await factory.CreateReadOnlyUserAsync(); // own account: the shared admin must not be disturbed

        var typo = await PostLoginAsync(NewBrowser(), email, password + "x"); // leaves one failed attempt recorded
        Assert.Equal(HttpStatusCode.OK, typo.StatusCode);

        var results = await Task.WhenAll(Enumerable.Range(0, 15).Select(_ => PostLoginAsync(NewBrowser(), email, password)));

        Assert.All(results, r => Assert.Equal(HttpStatusCode.Redirect, r.StatusCode));
    }

    [Fact]
    public async Task Wrong_passwords_still_count_towards_lockout_and_never_succeed()
    {
        var (email, password) = await factory.CreateReadOnlyUserAsync();
        for (var i = 0; i < 5; i++)
            Assert.Equal(HttpStatusCode.OK, (await PostLoginAsync(NewBrowser(), email, "wrong-" + i)).StatusCode);

        var afterLockout = await PostLoginAsync(NewBrowser(), email, password); // even the right password is refused while locked out
        Assert.Equal(HttpStatusCode.OK, afterLockout.StatusCode);
        Assert.Contains("temporarily locked", await afterLockout.Content.ReadAsStringAsync());
    }
}
