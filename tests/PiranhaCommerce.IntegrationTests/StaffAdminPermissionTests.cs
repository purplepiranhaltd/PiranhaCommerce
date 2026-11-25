using System.Net;
using System.Net.Http.Json;
using System.Reflection;
using System.Text.RegularExpressions;
using Microsoft.Extensions.DependencyInjection;
using PiranhaCommerce.Core.Identity;
using PiranhaCommerce.Core.Validation;

namespace PiranhaCommerce.IntegrationTests;

public partial class StaffAdminPermissionTests(PiranhaCommerceFactory factory) : IClassFixture<PiranhaCommerceFactory>
{
    private const string NotAllowed = "does not have permission to open this page";

    private HttpClient NewBrowser() => factory.CreateClient(new() { AllowAutoRedirect = false, HandleCookies = true });

    private async Task<HttpClient> SignInAsync(string email, string password)
    {
        var browser = NewBrowser();
        var form = await browser.GetAsync("/admin/login");
        var token = TokenRegex().Match(await form.Content.ReadAsStringAsync()).Groups[1].Value;
        var login = await browser.PostAsync("/admin/login", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["__RequestVerificationToken"] = token, ["_handler"] = "login", ["Input.Email"] = email, ["Input.Password"] = password,
        }));
        Assert.Equal(HttpStatusCode.Redirect, login.StatusCode);
        return browser;
    }

    /// <summary>Opens a page the way a browser would, following the redirect to the "not allowed" page, and says whether the person was let in.</summary>
    private static async Task<(bool Allowed, string Body, HttpStatusCode Status)> OpenAsync(HttpClient browser, string url)
    {
        var response = await browser.GetAsync(url);
        if (response.StatusCode == HttpStatusCode.Redirect && response.Headers.Location?.OriginalString.Contains("/admin/not-allowed") == true)
        {
            var denied = await browser.GetAsync("/admin/not-allowed");
            return (false, await denied.Content.ReadAsStringAsync(), response.StatusCode);
        }
        return (response.StatusCode == HttpStatusCode.OK, await response.Content.ReadAsStringAsync(), response.StatusCode);
    }

    private static List<string> MenuLinks(string html)
    {
        var start = html.IndexOf("<nav class=\"pc-admin-nav\"", StringComparison.Ordinal);
        if (start < 0) return [];
        var nav = html[start..html.IndexOf("</nav>", start, StringComparison.Ordinal)];
        return LinkRegex().Matches(nav).Select(m => m.Groups[1].Value).Where(h => h != "/admin").Distinct().ToList();
    }

    [Fact]
    public async Task The_menu_shows_only_what_the_role_can_open_and_a_hidden_page_says_so_when_opened_by_address()
    {
        var (email, password) = await factory.CreateStaffAsync(Permissions.OrdersFulfil);
        var browser = await SignInAsync(email, password);

        var links = MenuLinks(await (await browser.GetAsync("/admin")).Content.ReadAsStringAsync());
        Assert.Contains("/admin/orders", links);
        Assert.Contains("/admin/orders/return-requests", links);
        Assert.DoesNotContain("/admin/catalog/products", links);
        Assert.DoesNotContain("/admin/customers", links);
        Assert.DoesNotContain("/admin/webhooks", links);
        Assert.DoesNotContain("/admin/audit", links);
        Assert.DoesNotContain("/admin/orders/reports", links); // reports are their own permission

        var hidden = await OpenAsync(browser, "/admin/catalog/products");
        Assert.False(hidden.Allowed);
        Assert.Contains(NotAllowed, hidden.Body);
    }

    [Fact]
    public async Task A_user_with_no_role_can_sign_in_but_is_told_they_may_not_open_anything()
    {
        var (email, password) = await factory.CreateUserWithNoRoleAsync();
        var browser = await SignInAsync(email, password);
        var dashboard = await OpenAsync(browser, "/admin");
        Assert.False(dashboard.Allowed);
        Assert.Contains(NotAllowed, dashboard.Body);
        Assert.False((await OpenAsync(browser, "/admin/orders")).Allowed);
    }

    [Fact]
    public async Task An_administrator_sees_every_entry_and_opens_every_page()
    {
        var browser = await SignInAsync(PiranhaCommerceFactory.AdminEmail, PiranhaCommerceFactory.AdminPassword);
        var links = MenuLinks(await (await browser.GetAsync("/admin")).Content.ReadAsStringAsync());
        Assert.True(links.Count >= 30, $"Only {links.Count} menu links for an administrator.");
        foreach (var link in links)
            Assert.True((await OpenAsync(browser, link)).Allowed, $"An administrator could not open {link}.");
    }

    [Fact]
    public async Task Every_entry_a_permission_puts_in_the_menu_opens_for_a_role_that_holds_only_that_permission()
    {
        var catalog = factory.Services.GetRequiredService<PermissionCatalog>();
        var problems = new List<string>();
        foreach (var permission in catalog.All)
        {
            var (email, password) = await factory.CreateStaffAsync(permission.Key);
            var browser = await SignInAsync(email, password);
            foreach (var link in MenuLinks(await (await browser.GetAsync("/admin")).Content.ReadAsStringAsync()))
            {
                var opened = await OpenAsync(browser, link);
                if (!opened.Allowed)
                    problems.Add($"{permission.Key}: the menu shows {link}, but the page is not allowed ({(int)opened.Status})");
            }
        }
        Assert.True(problems.Count == 0, string.Join("\n", problems));
    }

    [Fact]
    public async Task The_order_page_hides_the_buttons_a_person_could_not_use()
    {
        var api = await factory.CreateAuthenticatedApiClientAsync();
        var product = (await (await api.PostAsJsonAsync("/api/v1/products",
            new PiranhaCommerce.Catalog.ProductInput { Name = "Hidden " + Guid.NewGuid().ToString("N")[..8], Price = 9m })).Content
            .ReadFromJsonAsync<PiranhaCommerce.Catalog.ProductDto>())!;
        await api.PutAsJsonAsync($"/api/v1/products/{product.Id}/listing", new PiranhaCommerce.Ecommerce.ProductListingInput { Published = true });
        await api.PostAsJsonAsync($"/api/v1/products/{product.Id}/stock/adjustments", new PiranhaCommerce.Catalog.StockAdjustmentInput { Delta = 5, Reason = "Delivery" });
        var json = new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web) { Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() } };
        var basket = (await (await api.PostAsJsonAsync("/api/v1/baskets", new PiranhaCommerce.Ecommerce.BasketCreateInput())).Content
            .ReadFromJsonAsync<PiranhaCommerce.Ecommerce.BasketDto>(json))!;
        await api.PutAsJsonAsync($"/api/v1/baskets/{basket.Id}/lines/{product.Id}", new PiranhaCommerce.Ecommerce.BasketLineInput { Quantity = 1 });
        var placed = await api.PostAsJsonAsync($"/api/v1/baskets/{basket.Id}/checkout", new PiranhaCommerce.Ecommerce.CheckoutInput
        {
            Email = $"hide-{Guid.NewGuid():N}@example.com",
            ShippingAddress = new PiranhaCommerce.Customers.AddressInput { Recipient = "Ada", Line1 = "1 High St", City = "Leeds", CountryCode = "GB" },
        });
        var order = (await placed.Content.ReadFromJsonAsync<PiranhaCommerce.Ecommerce.OrderDto>(json))!;

        async Task<string> PageFor(params string[] permissions)
        {
            var (email, password) = await factory.CreateStaffAsync(permissions);
            var browser = await SignInAsync(email, password);
            return await (await browser.GetAsync($"/admin/orders/{order.Id}")).Content.ReadAsStringAsync();
        }

        var viewer = await PageFor(Permissions.OrdersView);
        Assert.Contains($"Order {order.Reference}", viewer);
        Assert.DoesNotContain("Mark as processing", viewer);
        Assert.DoesNotContain("Record payment", viewer);
        Assert.DoesNotContain("Record refund", viewer);

        var fulfiller = await PageFor(Permissions.OrdersFulfil);
        Assert.Contains("Mark as processing", fulfiller);
        Assert.Contains("Record payment", fulfiller);
        Assert.DoesNotContain("Record refund", fulfiller);

        var refunder = await PageFor(Permissions.OrdersRefund);
        Assert.DoesNotContain("Mark as processing", refunder);
        Assert.Contains("Record refund", refunder);
    }

    [Fact]
    public async Task List_pages_show_edit_and_delete_only_to_people_who_can_use_them()
    {
        var brand = Unique("Brand");
        var api = await factory.CreateAuthenticatedApiClientAsync();
        Assert.True((await api.PostAsJsonAsync("/api/v1/brands", new { name = brand })).IsSuccessStatusCode);

        async Task<string> PageFor(string url, params string[] permissions)
        {
            var (email, password) = await factory.CreateStaffAsync(permissions);
            var browser = await SignInAsync(email, password);
            return await (await browser.GetAsync(url)).Content.ReadAsStringAsync();
        }

        var viewer = await PageFor("/admin/catalog/brands", Permissions.CatalogView);
        Assert.Contains(brand, viewer);
        Assert.DoesNotContain(">Edit</button>", viewer);
        Assert.DoesNotContain(">Delete</button>", viewer);
        Assert.DoesNotContain(">Add</button>", viewer);

        var editor = await PageFor("/admin/catalog/brands", Permissions.CatalogEdit);
        Assert.Contains(">Edit</button>", editor);
        Assert.Contains(">Delete</button>", editor);
    }

    private static string Unique(string p) => p + " " + Guid.NewGuid().ToString("N")[..8];

    // ---- the guard that stops a service call from the admin ----

    public interface ISampleService
    {
        [RequiresPermission(Permissions.OrdersRefund)]
        Task<string> RefundAsync();

        Task<string> ListAsync();

        [RequiresPermission(Permissions.OrdersRefund)]
        Task<string> FailAsync();
    }

    private sealed class SampleService : ISampleService
    {
        public Task<string> RefundAsync() => Task.FromResult("refunded");
        public Task<string> ListAsync() => Task.FromResult("listed");
        public Task<string> FailAsync() => throw new PiranhaCommerceConflictException("It cannot be done.");
    }

    [Fact]
    public async Task The_guard_refuses_a_marked_method_without_the_permission_and_lets_everything_else_through()
    {
        var plain = PermissionGuard.Wrap<ISampleService>(new SampleService(), new StaffAccess(false, new HashSet<string> { Permissions.OrdersView }));
        Assert.Equal("listed", await plain.ListAsync());
        await Assert.ThrowsAsync<PiranhaCommerceForbiddenException>(() => plain.RefundAsync());

        var refunder = PermissionGuard.Wrap<ISampleService>(new SampleService(), new StaffAccess(false, new HashSet<string> { Permissions.OrdersRefund }));
        Assert.Equal("refunded", await refunder.RefundAsync());

        var admin = PermissionGuard.Wrap<ISampleService>(new SampleService(), new StaffAccess(true, new HashSet<string>()));
        Assert.Equal("refunded", await admin.RefundAsync());

        // The service's own errors reach the page unchanged, so its message and handling still work.
        var ex = await Assert.ThrowsAsync<PiranhaCommerceConflictException>(() => admin.FailAsync());
        Assert.Equal("It cannot be done.", ex.Message);
    }

    // ---- nothing that changes data is left unguarded by accident ----

    /// <summary>The service interfaces the admin pages call.</summary>
    private static readonly string[] AdminServices =
    [
        "IBarcodeService", "IBrandService", "ICatalogTransferService", "ICategoryService", "ICustomerErasureService", "ICustomerGroupService", "ICustomerService",
        "ICustomerSignInAdminService", "ISocialSignInAdminService", "IDiscountRuleService", "IDisputeService", "IEmailTemplateService", "IFulfilmentService", "IGatewayService",
        "IGroupPricingService", "IOrderService", "IPaymentService", "IPriceBreakService", "IProductImageService", "IProductListingService", "IProductRelationService",
        "IProductService", "IProductVariantService", "IPromotionService", "IReportService", "IReturnRequestService", "IReviewPhotoService", "IReviewService",
        "ISaleService", "IShippingCarrierService", "IShippingService", "IShippingZoneService", "ISpecificationService", "IStaffLocationService", "IStockAlertService",
        "IStockLocationService", "IStockService", "IStockTransferService", "ITagService", "ITaxService", "IUnreconciledPaymentService", "IVoucherService",
        "IWarrantyService", "IStaffAdminService", "IStaffSecurityService", "IApiKeyService", "IWebhookDispatcher", "IWebhookService", "IWishlistService", "IAuditService",
    ];

    private static readonly string[] ReadPrefixes = ["Get", "List", "Find", "Count", "Export", "Preview", "Providers"];

    /// <summary>
    /// Methods on those services that need no permission of their own, with the reason: they only read, or only the shop front or the system calls them.
    /// A new method is not here, so it fails the test below until someone decides which it is.
    /// </summary>
    private static readonly HashSet<string> NeedNoPermission =
    [
        // reads whose names do not start with Get or List
        "IBrandService.NamesForProductsAsync", "ITagService.NamesForProductsAsync", "ISpecificationService.LabelsForProductsAsync", "ICustomerGroupService.GroupsOfAsync",
        "ICustomerSignInAdminService.HistoryAsync", "IGroupPricingService.PricesAsync", "IDisputeService.OpenCountAsync", "IDisputeService.OpenFileAsync",
        "IUnreconciledPaymentService.OpenCountAsync", "IReturnRequestService.WaitingCountAsync", "IProductImageService.OpenAsync", "IProductRelationService.ForProductAsync",
        "IProductRelationService.CrossSellForBasketAsync", "IReportService.SalesAsync", "IReportService.ProductsAsync", "IReportService.CategoriesAsync",
        "IReportService.CustomersAsync", "IReportService.TaxAsync", "IReviewPhotoService.OpenAnyAsync", "IReviewPhotoService.OpenOwnAsync",
        "IReviewPhotoService.OpenPublicAsync", "IStockAlertService.DemandAsync", "IWishlistService.DemandAsync", "IStockTransferService.InTransitCountAsync",
        "IStaffLocationService.CanActAtAsync", "IReviewService.SummaryAsync", "IReviewService.SummariesAsync", "IWebhookService.EventTypes",
        // called by the shop front for a signed-in shopper, never from the admin
        "IReviewService.CreateAsync", "IReviewService.UpdateOwnAsync", "IReviewService.DeleteOwnAsync", "IReviewService.SetHelpfulAsync", "IReviewPhotoService.AddAsync",
        "IReviewPhotoService.DeleteOwnAsync", "IReturnRequestService.RequestAsync", "IReturnRequestService.CancelAsync", "IStockAlertService.SubscribeAsync",
        "IStockAlertService.UnsubscribeAsync", "IWishlistService.AddAsync", "IWishlistService.RemoveAsync", "IWishlistService.ClearAsync",
        // called by the system (gateways, the background sender, other services), never from the admin
        "IGatewayService.HandleWebhookAsync", "IWebhookDispatcher.DispatchDueAsync", "IWebhookDispatcher.PurgeAsync", "IStockService.EnsureLevelsAsync",
        "IStockService.ApplyInTransactionAsync",
        // the signed-in person looks after their own sign-in security; the page passes their own id
        "IStaffSecurityService.BeginSetupAsync", "IStaffSecurityService.ConfirmSetupAsync", "IStaffSecurityService.DisableAsync", "IStaffSecurityService.NewRecoveryCodesAsync",
        // the sample plugin guards this one with its own permission on the interface method (see the next test)
    ];

    [Fact]
    public void Every_method_that_changes_data_on_a_service_the_admin_calls_names_its_permission()
    {
        var types = typeof(PiranhaCommerceFactory).Assembly.GetReferencedAssemblies().Where(a => a.Name!.StartsWith("PiranhaCommerce", StringComparison.Ordinal))
            .Select(Assembly.Load).Concat(AppDomain.CurrentDomain.GetAssemblies().Where(a => a.GetName().Name!.StartsWith("PiranhaCommerce", StringComparison.Ordinal)))
            .Distinct().SelectMany(a => { try { return a.GetExportedTypes(); } catch { return []; } })
            .Where(t => t.IsInterface && AdminServices.Contains(t.Name)).GroupBy(t => t.Name).Select(g => g.First()).ToList();
        Assert.Equal(AdminServices.Length, types.Count);

        var unguarded = new List<string>();
        foreach (var type in types)
            foreach (var method in type.GetMethods())
            {
                if (PermissionGuard.Required(method) is not null) continue;
                if (ReadPrefixes.Any(p => method.Name.StartsWith(p, StringComparison.Ordinal))) continue;
                if (NeedNoPermission.Contains($"{type.Name}.{method.Name}")) continue;
                unguarded.Add($"{type.Name}.{method.Name}");
            }
        Assert.True(unguarded.Count == 0,
            "These service methods are not marked [RequiresPermission(...)] and are not listed as needing none:\n" + string.Join("\n", unguarded));
    }

    [Fact]
    public void A_plugins_service_can_name_its_own_permission()
    {
        var method = typeof(PiranhaCommerce.Example.Warranty.IWarrantyService).GetMethod("SetAsync")!;
        Assert.Equal("warranty.edit", PermissionGuard.Required(method));
    }

    [System.Text.RegularExpressions.GeneratedRegex("<input[^>]*name=\"__RequestVerificationToken\"[^>]*value=\"([^\"]+)\"")]
    private static partial Regex TokenRegex();

    [System.Text.RegularExpressions.GeneratedRegex("href=\"(/admin[^\"]*)\"")]
    private static partial Regex LinkRegex();
}
