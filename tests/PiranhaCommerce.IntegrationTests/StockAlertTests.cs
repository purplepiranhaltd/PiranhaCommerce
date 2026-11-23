using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using PiranhaCommerce.Catalog;
using PiranhaCommerce.Core.Email;
using PiranhaCommerce.Core.Paging;
using PiranhaCommerce.Customers;
using PiranhaCommerce.Data;
using PiranhaCommerce.Ecommerce;

namespace PiranhaCommerce.IntegrationTests;

/// <summary>"Tell me when it is back": requests from shoppers and guests, one email each when the product can be bought again, nothing sent twice.</summary>
public class StockAlertTests(PiranhaFactory factory) : IClassFixture<PiranhaFactory>
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };
    private static string Tag() => Guid.NewGuid().ToString("N")[..8];
    private static async Task<T> Read<T>(HttpResponseMessage r) { Assert.True(r.IsSuccessStatusCode, r.StatusCode + " " + await r.Content.ReadAsStringAsync()); return (await r.Content.ReadFromJsonAsync<T>(Json))!; }

    private sealed class Clock : TimeProvider
    {
        private DateTimeOffset _now = DateTimeOffset.UtcNow;
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan by) => _now += by;
    }

    private sealed class FailingSender : IEmailSender
    {
        public Task SendAsync(EmailMessage message, CancellationToken ct = default) => throw new InvalidOperationException("The mail server is down.");
    }

    /// <summary>A product with no stock, published unless said otherwise.</summary>
    private static async Task<ProductDto> SoldOut(HttpClient staff, bool published = true)
    {
        var p = await Read<ProductDto>(await staff.PostAsJsonAsync("/api/v1/products", new ProductInput { Name = "Alert " + Tag(), Sku = "AL-" + Tag(), Price = 10m }));
        Assert.True((await staff.PutAsJsonAsync($"/api/v1/products/{p.Id}/listing", new ProductListingInput { Published = published })).IsSuccessStatusCode);
        return p;
    }

    private static Task<HttpResponseMessage> Restock(HttpClient staff, Guid productId, decimal delta = 3, Guid? location = null) =>
        staff.PostAsJsonAsync($"/api/v1/products/{productId}/stock/adjustments", new StockAdjustmentInput { Delta = delta, Reason = "Delivery", LocationId = location });

    private static Task<HttpResponseMessage> Ask(HttpClient c, Guid productId, string email) =>
        c.PostAsJsonAsync("/api/v1/storefront/stock-alerts", new StockAlertInput { ProductId = productId, Email = email });

    private async Task<int> Run()
    {
        using var scope = factory.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<IStockAlertNotifier>().RunOnceAsync();
    }

    private IReadOnlyList<EmailMessage> Mail(string to) => factory.Email.To(to).Where(m => m.TemplateKey == EcommerceEmailTemplates.BackInStock).ToList();

    private async Task<string> TokenAsync(Guid productId, string email)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PiranhaDbContext>();
        var normalized = email.ToLowerInvariant();
        return await db.Set<StockAlertSubscription>().Where(s => s.ProductId == productId && s.NormalizedEmail == normalized).Select(s => s.UnsubscribeToken).SingleAsync();
    }

    [Fact]
    public async Task A_guest_is_emailed_once_when_the_product_can_be_bought_again()
    {
        var staff = await factory.CreateAuthenticatedApiClientAsync();
        var anonymous = factory.CreateClient();
        var p = await SoldOut(staff);
        var email = $"wait-{Tag()}@example.com";

        Assert.Equal(HttpStatusCode.NoContent, (await Ask(anonymous, p.Id, email)).StatusCode);
        await Run();
        Assert.Empty(Mail(email)); // still sold out

        Assert.True((await Restock(staff, p.Id)).IsSuccessStatusCode);
        await Run();
        var mail = Assert.Single(Mail(email));
        Assert.Equal(p.Name, mail.Variables["productName"]);
        Assert.Equal(p.Sku, mail.Variables["sku"]);
        Assert.Equal(p.Id.ToString(), mail.Variables["productId"]);
        Assert.Equal(await TokenAsync(p.Id, email), mail.Variables["unsubscribeToken"]);

        // Spent: later runs, and more stock, send nothing more.
        await Run();
        Assert.True((await Restock(staff, p.Id)).IsSuccessStatusCode);
        await Run();
        Assert.Single(Mail(email));
    }

    [Fact]
    public async Task Asking_again_changes_nothing_in_any_letter_case_but_asking_after_being_told_asks_afresh()
    {
        var staff = await factory.CreateAuthenticatedApiClientAsync();
        var anonymous = factory.CreateClient();
        var p = await SoldOut(staff);
        var email = $"Again-{Tag()}@example.com";

        Assert.Equal(HttpStatusCode.NoContent, (await Ask(anonymous, p.Id, email)).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await Ask(anonymous, p.Id, email.ToUpperInvariant())).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await Ask(anonymous, p.Id, "  " + email + " ")).StatusCode);
        Assert.Equal(1, (await Read<PagedResult<StockAlertDto>>(await staff.GetAsync($"/api/v1/stock-alerts?productId={p.Id}"))).TotalCount);

        Assert.True((await Restock(staff, p.Id, 1)).IsSuccessStatusCode);
        await Run();
        Assert.Single(Mail(email));

        // It sells out again; the same person asks again and is told again.
        Assert.True((await Restock(staff, p.Id, -1)).IsSuccessStatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await Ask(anonymous, p.Id, email)).StatusCode);
        await Run();
        Assert.Single(Mail(email));
        Assert.True((await Restock(staff, p.Id, 2)).IsSuccessStatusCode);
        await Run();
        Assert.Equal(2, Mail(email).Count);
    }

    [Fact]
    public async Task Requests_are_refused_for_products_that_cannot_be_asked_about_and_for_bad_addresses()
    {
        var staff = await factory.CreateAuthenticatedApiClientAsync();
        var anonymous = factory.CreateClient();
        var inStock = await SoldOut(staff);
        await Restock(staff, inStock.Id, 5);
        var hidden = await SoldOut(staff, published: false);
        var parent = await SoldOut(staff);
        Assert.True((await staff.PostAsJsonAsync($"/api/v1/products/{parent.Id}/options", new ProductOptionInput { Name = "Size", Values = ["S", "M"] })).IsSuccessStatusCode);
        var set = await Read<ProductVariantsDto>(await staff.PostAsJsonAsync($"/api/v1/products/{parent.Id}/variants/generate", new GenerateVariantsInput { Price = 10m }));
        var email = $"refuse-{Tag()}@example.com";

        Assert.Equal(HttpStatusCode.Conflict, (await Ask(anonymous, inStock.Id, email)).StatusCode);   // can be bought now
        Assert.Equal(HttpStatusCode.BadRequest, (await Ask(anonymous, hidden.Id, email)).StatusCode);  // not published
        Assert.Equal(HttpStatusCode.BadRequest, (await Ask(anonymous, Guid.NewGuid(), email)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await Ask(anonymous, parent.Id, email)).StatusCode);  // a parent: choose a variant
        Assert.Equal(HttpStatusCode.BadRequest, (await Ask(anonymous, inStock.Id, "not-an-email")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await Ask(anonymous, inStock.Id, "")).StatusCode);

        // A variant is asked about on its own (once it is published).
        var variant = set.Variants[0].ProductId;
        await staff.PutAsJsonAsync($"/api/v1/products/{variant}/listing", new ProductListingInput { Published = false });
        Assert.Equal(HttpStatusCode.BadRequest, (await Ask(anonymous, variant, email)).StatusCode);
        await staff.PutAsJsonAsync($"/api/v1/products/{variant}/listing", new ProductListingInput { Published = true });
        Assert.Equal(HttpStatusCode.NoContent, (await Ask(anonymous, variant, email)).StatusCode);
        await Restock(staff, variant, 2);
        await Run();
        Assert.Equal(set.Variants[0].Name, Assert.Single(Mail(email)).Variables["productName"]);
    }

    [Fact]
    public async Task One_address_can_wait_for_only_so_many_products_at_once()
    {
        var staff = await factory.CreateAuthenticatedApiClientAsync();
        var anonymous = factory.CreateClient();
        var email = $"flood-{Tag()}@example.com";
        for (var i = 0; i < 25; i++)
            Assert.Equal(HttpStatusCode.NoContent, (await Ask(anonymous, (await SoldOut(staff)).Id, email)).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await Ask(anonymous, (await SoldOut(staff)).Id, email)).StatusCode);
    }

    [Fact]
    public async Task Only_stock_at_the_location_online_orders_take_from_counts_and_an_unpublished_product_waits()
    {
        var staff = await factory.CreateAuthenticatedApiClientAsync();
        var anonymous = factory.CreateClient();
        var p = await SoldOut(staff);
        var email = $"where-{Tag()}@example.com";
        Assert.Equal(HttpStatusCode.NoContent, (await Ask(anonymous, p.Id, email)).StatusCode);

        var shop = await Read<StockLocationDto>(await staff.PostAsJsonAsync("/api/v1/stock-locations", new StockLocationInput { Code = "t" + Tag(), Name = "Shop floor " + Tag() }));
        Assert.True((await Restock(staff, p.Id, 4, shop.Id)).IsSuccessStatusCode); // not where checkout looks
        await Run();
        Assert.Empty(Mail(email));

        Assert.True((await Restock(staff, p.Id, 1)).IsSuccessStatusCode);
        await staff.PutAsJsonAsync($"/api/v1/products/{p.Id}/listing", new ProductListingInput { Published = false });
        await Run();
        Assert.Empty(Mail(email)); // in stock but not on sale

        await staff.PutAsJsonAsync($"/api/v1/products/{p.Id}/listing", new ProductListingInput { Published = true });
        await Run();
        Assert.Single(Mail(email));
    }

    [Fact]
    public async Task Two_senders_at_once_send_each_request_exactly_once()
    {
        var staff = await factory.CreateAuthenticatedApiClientAsync();
        var anonymous = factory.CreateClient();
        var p = await SoldOut(staff);
        var emails = Enumerable.Range(0, 6).Select(_ => $"race-{Tag()}@example.com").ToList();
        foreach (var e in emails) Assert.Equal(HttpStatusCode.NoContent, (await Ask(anonymous, p.Id, e)).StatusCode);
        Assert.True((await Restock(staff, p.Id, 9)).IsSuccessStatusCode);

        await Task.WhenAll(Run(), Run(), Run());
        Assert.All(emails, e => Assert.Single(Mail(e)));
    }

    [Fact]
    public async Task A_failed_send_is_tried_again_after_the_claim_runs_out_and_never_twice_once_it_works()
    {
        var staff = await factory.CreateAuthenticatedApiClientAsync();
        var anonymous = factory.CreateClient();
        var p = await SoldOut(staff);
        var email = $"retry-{Tag()}@example.com";
        Assert.Equal(HttpStatusCode.NoContent, (await Ask(anonymous, p.Id, email)).StatusCode);
        Assert.True((await Restock(staff, p.Id, 2)).IsSuccessStatusCode);

        var clock = new Clock();
        async Task<int> RunWith(IEmailSender sender)
        {
            using var scope = factory.Services.CreateScope();
            var sp = scope.ServiceProvider;
            var notifier = new StockAlertNotifier(sp.GetRequiredService<PiranhaDbContext>(), sender, sp.GetRequiredService<IOptions<EcommerceOptions>>(), NullLogger<StockAlertNotifier>.Instance, clock);
            return await notifier.RunOnceAsync();
        }

        Assert.Equal(0, await RunWith(new FailingSender()));
        Assert.Equal(0, await RunWith(factory.Email));       // still claimed: not tried again at once
        Assert.Empty(Mail(email));
        clock.Advance(TimeSpan.FromMinutes(11));
        Assert.True(await RunWith(factory.Email) >= 1);
        Assert.Single(Mail(email));
        clock.Advance(TimeSpan.FromHours(1));
        await RunWith(factory.Email);
        Assert.Single(Mail(email));
    }

    [Fact]
    public async Task The_unsubscribe_secret_removes_only_that_request()
    {
        var staff = await factory.CreateAuthenticatedApiClientAsync();
        var anonymous = factory.CreateClient();
        var p = await SoldOut(staff);
        var leaving = $"leave-{Tag()}@example.com";
        var staying = $"stay-{Tag()}@example.com";
        await Ask(anonymous, p.Id, leaving);
        await Ask(anonymous, p.Id, staying);

        var token = await TokenAsync(p.Id, leaving);
        Assert.Equal(HttpStatusCode.NotFound, (await anonymous.DeleteAsync("/api/v1/storefront/stock-alerts/not-a-token")).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await anonymous.DeleteAsync($"/api/v1/storefront/stock-alerts/{token}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await anonymous.DeleteAsync($"/api/v1/storefront/stock-alerts/{token}")).StatusCode);

        Assert.True((await Restock(staff, p.Id)).IsSuccessStatusCode);
        await Run();
        Assert.Empty(Mail(leaving));
        Assert.Single(Mail(staying));
    }

    [Fact]
    public async Task Staff_see_what_people_are_waiting_for_and_only_staff_who_may_write_can_remove_requests()
    {
        var staff = await factory.CreateAuthenticatedApiClientAsync();
        var anonymous = factory.CreateClient();
        var popular = await SoldOut(staff);
        var quiet = await SoldOut(staff);
        foreach (var i in new[] { 1, 2, 3 }) await Ask(anonymous, popular.Id, $"p{i}-{Tag()}@example.com");
        await Ask(anonymous, quiet.Id, $"q-{Tag()}@example.com");

        var demand = await Read<List<StockAlertDemandDto>>(await staff.GetAsync("/api/v1/stock-alerts/demand"));
        var mine = demand.Where(d => d.ProductId == popular.Id || d.ProductId == quiet.Id).ToList();
        Assert.Equal([(popular.Id, 3), (quiet.Id, 1)], mine.Select(d => (d.ProductId, d.Waiting)));
        Assert.All(mine, d => Assert.Equal(0m, d.InStock));

        var list = await Read<PagedResult<StockAlertDto>>(await staff.GetAsync($"/api/v1/stock-alerts?productId={quiet.Id}"));
        var one = Assert.Single(list.Items);
        Assert.DoesNotContain("token", await (await staff.GetAsync($"/api/v1/stock-alerts?productId={quiet.Id}")).Content.ReadAsStringAsync(), StringComparison.OrdinalIgnoreCase);

        var (email, password) = await factory.CreateReadOnlyUserAsync();
        var reader = await factory.CreateAuthenticatedApiClientAsync(email, password);
        Assert.Equal(HttpStatusCode.OK, (await reader.GetAsync("/api/v1/stock-alerts")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await reader.DeleteAsync($"/api/v1/stock-alerts/{one.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("/api/v1/stock-alerts")).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await staff.DeleteAsync($"/api/v1/stock-alerts/{one.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await staff.DeleteAsync($"/api/v1/stock-alerts/{one.Id}")).StatusCode);
    }

    [Fact]
    public async Task Requests_are_exported_with_the_customer_and_erased_with_them_whether_made_signed_in_or_as_a_guest()
    {
        var staff = await factory.CreateAuthenticatedApiClientAsync();
        var email = $"erase-{Tag()}@example.com";
        var shopper = factory.CreateClient();
        var registered = await Read<RegisteredDto>(await shopper.PostAsJsonAsync("/api/v1/storefront/auth/register", new RegisterInput { Email = email, Password = "a-long-enough-password" }));
        shopper.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", registered.Token.AccessToken);
        var signedIn = await SoldOut(staff);
        var asGuest = await SoldOut(staff);
        Assert.Equal(HttpStatusCode.NoContent, (await Ask(shopper, signedIn.Id, email)).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await Ask(factory.CreateClient(), asGuest.Id, email.ToUpperInvariant())).StatusCode);

        var export = await (await staff.GetAsync($"/api/v1/customers/{registered.Profile.Id}/export")).Content.ReadAsStringAsync();
        Assert.Contains("\"stockAlerts\"", export);
        Assert.Contains(signedIn.Id.ToString(), export);
        Assert.Contains(asGuest.Id.ToString(), export);

        Assert.Equal(HttpStatusCode.OK, (await staff.PostAsJsonAsync($"/api/v1/customers/{registered.Profile.Id}/erase", new EraseCustomerInput { ConfirmEmail = email })).StatusCode);
        Assert.Empty((await Read<PagedResult<StockAlertDto>>(await staff.GetAsync($"/api/v1/stock-alerts?productId={signedIn.Id}"))).Items);
        Assert.Empty((await Read<PagedResult<StockAlertDto>>(await staff.GetAsync($"/api/v1/stock-alerts?productId={asGuest.Id}"))).Items);
    }
}
