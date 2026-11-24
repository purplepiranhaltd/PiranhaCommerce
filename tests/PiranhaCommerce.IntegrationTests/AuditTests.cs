using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using PiranhaCommerce.Audit;
using PiranhaCommerce.Catalog;
using PiranhaCommerce.Core.Paging;
using PiranhaCommerce.Customers;

namespace PiranhaCommerce.IntegrationTests;

public class AuditTests(PiranhaCommerceFactory factory) : IClassFixture<PiranhaCommerceFactory>
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };
    private static string Tag() => Guid.NewGuid().ToString("N")[..8];

    private static async Task<List<AuditEntryDto>> Log(HttpClient c, string type, Guid id) =>
        (await c.GetFromJsonAsync<PagedResult<AuditEntryDto>>($"/api/v1/audit?entityType={type}&entityId={id}&pageSize=100", Json))!.Items.OrderBy(e => e.WhenUtc).ToList();

    [Fact]
    public async Task Creating_changing_and_deleting_a_product_is_recorded_with_who_and_the_old_and_new_values()
    {
        var c = await factory.CreateAuthenticatedApiClientAsync();
        var p = (await (await c.PostAsJsonAsync("/api/v1/products", new ProductInput { Name = "Audit " + Tag(), Sku = "AU-" + Tag(), Price = 10m })).Content.ReadFromJsonAsync<ProductDto>(Json))!;
        Assert.Equal(HttpStatusCode.OK, (await c.PutAsJsonAsync($"/api/v1/products/{p.Id}", new ProductInput { Name = p.Name, Sku = p.Sku, Price = 12.5m })).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await c.DeleteAsync($"/api/v1/products/{p.Id}")).StatusCode);

        var log = await Log(c, "Product", p.Id);
        Assert.Equal([AuditAction.Created, AuditAction.Updated, AuditAction.Deleted], log.Select(e => e.Action));
        Assert.All(log, e => { Assert.Equal(PiranhaCommerceFactory.AdminEmail, e.Actor); Assert.Equal(p.Name, e.Label); });
        Assert.Contains(log[0].Changes, ch => ch is { Property: "Price", From: null, To: "10" });
        var update = Assert.Single(log[1].Changes);
        Assert.Equal(("Price", "10", "12.5"), (update.Property, update.From, update.To));
        Assert.Contains(log[2].Changes, ch => ch is { Property: "Name", From: not null, To: null });

        // Saving with nothing different records nothing.
        var q = (await (await c.PostAsJsonAsync("/api/v1/products", new ProductInput { Name = "Same " + Tag(), Price = 1m })).Content.ReadFromJsonAsync<ProductDto>(Json))!;
        await c.PutAsJsonAsync($"/api/v1/products/{q.Id}", new ProductInput { Name = q.Name, Price = 1m });
        Assert.Single(await Log(c, "Product", q.Id));
    }

    [Fact]
    public async Task Personal_details_are_never_written_to_the_log()
    {
        var c = await factory.CreateAuthenticatedApiClientAsync();
        var secret = "secret-" + Tag();
        var created = await c.PostAsJsonAsync("/api/v1/customers", new CustomerInput { Email = $"{secret}@example.com", FirstName = "Hidden", LastName = secret });
        var customer = (await created.Content.ReadFromJsonAsync<CustomerDto>(Json))!;
        await c.PutAsJsonAsync($"/api/v1/customers/{customer.Id}", new CustomerInput { Email = $"{secret}@example.com", FirstName = "Hidden2", LastName = secret });

        var log = await Log(c, "Customer", customer.Id);
        Assert.NotEmpty(log);
        Assert.Contains(log.SelectMany(e => e.Changes), ch => ch is { Property: "FirstName", Hidden: true, From: null, To: null });
        var raw = await c.GetStringAsync($"/api/v1/audit?entityType=Customer&entityId={customer.Id}");
        Assert.DoesNotContain(secret, raw);
        Assert.DoesNotContain("Hidden", raw.Replace("\"hidden\"", ""), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_dry_run_import_leaves_no_trace_and_a_real_one_is_recorded()
    {
        var c = await factory.CreateAuthenticatedApiClientAsync();
        var sku = "AUI-" + Tag();
        async Task Import(string query)
        {
            var form = new MultipartFormDataContent();
            var file = new ByteArrayContent(Encoding.UTF8.GetBytes($"sku,name,price\n{sku},Imported,3\n"));
            file.Headers.ContentType = new MediaTypeHeaderValue("text/csv");
            form.Add(file, "file", "p.csv");
            Assert.Equal(HttpStatusCode.OK, (await c.PostAsync("/api/v1/products/import" + query, form)).StatusCode);
        }
        // Looked up by this import's own SKU, so other tests creating products as the same user at the same time cannot change the answer.
        async Task<List<ProductDto>> Found() => (await c.GetFromJsonAsync<PagedResult<ProductDto>>($"/api/v1/products?search={sku}", Json))!.Items.Where(p => p.Sku == sku).ToList();

        await Import("?dryRun=true");
        Assert.Empty(await Found());
        await Import("");
        var product = Assert.Single(await Found());
        Assert.Contains(await Log(c, "Product", product.Id), e => e.Action == AuditAction.Created);
    }

    [Fact]
    public async Task Deletes_and_updates_made_with_set_based_sql_are_recorded_too()
    {
        var c = await factory.CreateAuthenticatedApiClientAsync();

        // A category deleted, a sale changed and deleted.
        var cat = (await (await c.PostAsJsonAsync("/api/v1/categories", new CategoryInput { Name = "Cat " + Tag() })).Content.ReadFromJsonAsync<CategoryDto>(Json))!;
        Assert.Equal(HttpStatusCode.NoContent, (await c.DeleteAsync($"/api/v1/categories/{cat.Id}")).StatusCode);
        Assert.Equal([AuditAction.Created, AuditAction.Deleted], (await Log(c, "Category", cat.Id)).Select(e => e.Action));

        var starts = DateTimeOffset.UtcNow.AddDays(1);
        var sale = (await (await c.PostAsJsonAsync("/api/v1/sales", new PiranhaCommerce.Ecommerce.SaleInput { Name = "Sale " + Tag(), StartsUtc = starts, EndsUtc = starts.AddDays(2), Percent = 10m }, Json)).Content.ReadFromJsonAsync<PiranhaCommerce.Ecommerce.SaleDto>(Json))!;
        Assert.Equal(HttpStatusCode.OK, (await c.PutAsJsonAsync($"/api/v1/sales/{sale.Id}", new PiranhaCommerce.Ecommerce.SaleInput { Name = sale.Name, StartsUtc = starts, EndsUtc = starts.AddDays(2), Percent = 25m }, Json)).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await c.DeleteAsync($"/api/v1/sales/{sale.Id}")).StatusCode);
        var log = await Log(c, "Sale", sale.Id);
        Assert.Equal([AuditAction.Created, AuditAction.Updated, AuditAction.Deleted], log.Select(e => e.Action));
        Assert.Contains(log[1].Changes, ch => ch is { Property: "Percent", From: "10", To: "25" });

        // An order moved on (a set-based update that says so itself).
        var p = (await (await c.PostAsJsonAsync("/api/v1/products", new ProductInput { Name = "Ord " + Tag(), Price = 5m })).Content.ReadFromJsonAsync<ProductDto>(Json))!;
        await c.PutAsJsonAsync($"/api/v1/products/{p.Id}/listing", new PiranhaCommerce.Ecommerce.ProductListingInput { Published = true });
        await c.PostAsJsonAsync($"/api/v1/products/{p.Id}/stock/adjustments", new StockAdjustmentInput { Delta = 5, Reason = "Delivery" });
        var basket = (await (await c.PostAsJsonAsync("/api/v1/baskets", new PiranhaCommerce.Ecommerce.BasketCreateInput())).Content.ReadFromJsonAsync<PiranhaCommerce.Ecommerce.BasketDto>(Json))!;
        await c.PutAsJsonAsync($"/api/v1/baskets/{basket.Id}/lines/{p.Id}", new PiranhaCommerce.Ecommerce.BasketLineInput { Quantity = 1 });
        var order = (await (await c.PostAsJsonAsync($"/api/v1/baskets/{basket.Id}/checkout", new PiranhaCommerce.Ecommerce.CheckoutInput
        {
            Email = $"aud-{Tag()}@example.com", ShippingAddress = new PiranhaCommerce.Customers.AddressInput { Line1 = "1 High St", City = "Leeds", CountryCode = "GB" },
        })).Content.ReadFromJsonAsync<PiranhaCommerce.Ecommerce.OrderDto>(Json))!;
        await c.PostAsJsonAsync($"/api/v1/orders/{order.Id}/stage", new PiranhaCommerce.Ecommerce.OrderStageInput { Stage = PiranhaCommerce.Ecommerce.OrderStage.Processing }, Json);
        var orderLog = await Log(c, "Order", order.Id);
        Assert.Contains(orderLog, e => e.Action == AuditAction.Created);
        var moved = Assert.Single(orderLog, e => e.Action == AuditAction.Updated && e.Changes.Any(ch => ch.Property == "Stage"));
        Assert.Equal(PiranhaCommerceFactory.AdminEmail, moved.Actor);
        Assert.Contains(moved.Changes, ch => ch is { Property: "Stage", From: "Placed", To: "Processing" });
        Assert.DoesNotContain("example.com", await c.GetStringAsync($"/api/v1/audit?entityType=Order&entityId={order.Id}")); // the buyer's email is private
    }

    [Fact]
    public async Task Only_administrators_can_read_the_log_and_bad_filters_are_refused()
    {
        var (email, password) = await factory.CreateReadOnlyUserAsync();
        var viewer = await factory.CreateAuthenticatedApiClientAsync(email, password);
        Assert.Equal(HttpStatusCode.Forbidden, (await viewer.GetAsync("/api/v1/audit")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await factory.CreateClient().GetAsync("/api/v1/audit")).StatusCode);
        var c = await factory.CreateAuthenticatedApiClientAsync();
        Assert.Equal(HttpStatusCode.BadRequest, (await c.GetAsync("/api/v1/audit?from=2026-02-01&to=2026-01-01")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await c.GetAsync("/api/v1/audit?from=2026-01-01&to=2026-12-31&actor=nobody")).StatusCode);
    }
}
