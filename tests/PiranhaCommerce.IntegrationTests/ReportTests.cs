using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using PiranhaCommerce.Catalog;
using PiranhaCommerce.Customers;
using PiranhaCommerce.Ecommerce;

namespace PiranhaCommerce.IntegrationTests;

[Collection(OrderSettings.Name)]
public class ReportTests(PiranhaCommerceFactory factory) : IClassFixture<PiranhaCommerceFactory>
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };
    private static string Tag() => Guid.NewGuid().ToString("N")[..8];
    private static async Task<T> Read<T>(HttpResponseMessage r) { Assert.True(r.IsSuccessStatusCode, r.StatusCode.ToString()); return (await r.Content.ReadFromJsonAsync<T>(Json))!; }

    private static async Task<(ProductDto Product, OrderDto Order)> SellAsync(HttpClient c, decimal price, int quantity, string? categoryId = null)
    {
        var p = await Read<ProductDto>(await c.PostAsJsonAsync("/api/v1/products", new ProductInput { Name = "Report " + Tag(), Sku = "RP-" + Tag(), Price = price }));
        await c.PutAsJsonAsync($"/api/v1/products/{p.Id}/listing", new ProductListingInput { Published = true });
        await c.PostAsJsonAsync($"/api/v1/products/{p.Id}/stock/adjustments", new StockAdjustmentInput { Delta = 50, Reason = "Delivery" });
        var basket = await Read<BasketDto>(await c.PostAsJsonAsync("/api/v1/baskets", new BasketCreateInput()));
        await c.PutAsJsonAsync($"/api/v1/baskets/{basket.Id}/lines/{p.Id}", new BasketLineInput { Quantity = quantity });
        var order = await Read<OrderDto>(await c.PostAsJsonAsync($"/api/v1/baskets/{basket.Id}/checkout", new CheckoutInput
        {
            Email = $"rep-{Tag()}@example.com",
            ShippingAddress = new AddressInput { Line1 = "1 High St", City = "Leeds", CountryCode = "GB" },
        }));
        return (p, order);
    }

    [Fact]
    public async Task Sales_best_sellers_and_cancellation_are_counted_correctly()
    {
        var c = await factory.CreateAuthenticatedApiClientAsync();
        var before = await Read<SalesReportDto>(await c.GetAsync("/api/v1/reports/sales"));

        var (kept, keptOrder) = await SellAsync(c, 12.50m, 3);
        var (gone, goneOrder) = await SellAsync(c, 40m, 1);
        Assert.Equal(HttpStatusCode.OK, (await c.PostAsJsonAsync($"/api/v1/orders/{goneOrder.Id}/stage", new OrderStageInput { Stage = OrderStage.Cancelled }, Json)).StatusCode);

        var after = await Read<SalesReportDto>(await c.GetAsync("/api/v1/reports/sales"));
        Assert.Equal(before.Totals.Orders + 1, after.Totals.Orders);
        Assert.Equal(before.Totals.Units + 3, after.Totals.Units);
        Assert.Equal(before.Totals.Goods + 37.50m, after.Totals.Goods);
        Assert.Equal(before.Totals.Total + keptOrder.Total, after.Totals.Total);
        Assert.Equal(30, after.Buckets.Count);
        Assert.Equal(after.Totals.Total, after.Buckets.Sum(b => b.Total));

        var products = await Read<ProductSalesReportDto>(await c.GetAsync("/api/v1/reports/products?top=1000"));
        var row = Assert.Single(products.Rows, r => r.ProductId == kept.Id);
        Assert.Equal((3, 37.50m, 1), (row.Quantity, row.Revenue, row.Orders));
        Assert.DoesNotContain(products.Rows, r => r.ProductId == gone.Id);

        var byMonth = await Read<SalesReportDto>(await c.GetAsync("/api/v1/reports/sales?interval=Month"));
        Assert.Equal(after.Totals.Total, byMonth.Totals.Total);
        var byWeek = await Read<SalesReportDto>(await c.GetAsync("/api/v1/reports/sales?interval=Week"));
        Assert.All(byWeek.Buckets, b => Assert.Equal(DayOfWeek.Monday, b.Period.DayOfWeek));
        Assert.Equal(after.Totals.Total, byWeek.Totals.Total);
    }

    [Fact]
    public async Task Refunds_reduce_net_sales_and_the_other_reports_answer()
    {
        var c = await factory.CreateAuthenticatedApiClientAsync();
        var before = await Read<SalesReportDto>(await c.GetAsync("/api/v1/reports/sales"));
        var (_, order) = await SellAsync(c, 20m, 2);
        await c.PostAsJsonAsync($"/api/v1/orders/{order.Id}/payments", new PaymentInput { Amount = order.Total });
        Assert.Equal(HttpStatusCode.Created, (await c.PostAsJsonAsync($"/api/v1/orders/{order.Id}/refunds", new PaymentInput { Amount = 5m })).StatusCode);

        var after = await Read<SalesReportDto>(await c.GetAsync("/api/v1/reports/sales"));
        Assert.Equal(before.Totals.Refunded + 5m, after.Totals.Refunded);
        Assert.Equal(before.Totals.Net + order.Total - 5m, after.Totals.Net);

        Assert.NotEmpty((await Read<CategorySalesReportDto>(await c.GetAsync("/api/v1/reports/categories"))).Rows);
        var customers = await Read<CustomerReportDto>(await c.GetAsync("/api/v1/reports/customers"));
        Assert.True(customers.GuestOrders >= 1);
        Assert.Equal(customers.Currency, (await Read<TaxReportDto>(await c.GetAsync("/api/v1/reports/tax"))).Currency);
    }

    [Fact]
    public async Task Bad_requests_are_refused_and_csv_is_offered()
    {
        var c = await factory.CreateAuthenticatedApiClientAsync();
        Assert.Equal(HttpStatusCode.BadRequest, (await c.GetAsync("/api/v1/reports/sales?from=2026-02-01&to=2026-01-01")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await c.GetAsync("/api/v1/reports/sales?from=2020-01-01&to=2026-01-01&interval=Week")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await c.GetAsync("/api/v1/reports/products?top=0")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await c.GetAsync("/api/v1/reports/tax?format=xml")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await factory.CreateClient().GetAsync("/api/v1/reports/sales")).StatusCode);

        var csv = await c.GetAsync("/api/v1/reports/sales?format=csv");
        Assert.Equal(HttpStatusCode.OK, csv.StatusCode);
        Assert.Equal("text/csv", csv.Content.Headers.ContentType?.MediaType);
        var text = await csv.Content.ReadAsStringAsync();
        Assert.Contains("Orders,Units,Goods", text);
        Assert.Contains("Total,", text);
    }

    [Fact]
    public async Task Low_stock_lists_products_at_or_below_the_level_lowest_first()
    {
        var c = await factory.CreateAuthenticatedApiClientAsync();
        var low = await Read<ProductDto>(await c.PostAsJsonAsync("/api/v1/products", new ProductInput { Name = "Low " + Tag(), Price = 1m }));
        var high = await Read<ProductDto>(await c.PostAsJsonAsync("/api/v1/products", new ProductInput { Name = "High " + Tag(), Price = 1m }));
        await c.PostAsJsonAsync($"/api/v1/products/{low.Id}/stock/adjustments", new StockAdjustmentInput { Delta = 2, Reason = "Delivery" });
        await c.PostAsJsonAsync($"/api/v1/products/{high.Id}/stock/adjustments", new StockAdjustmentInput { Delta = 60, Reason = "Delivery" });

        var list = (await c.GetFromJsonAsync<List<LowStockDto>>("/api/v1/stock/low?threshold=5", Json))!;
        Assert.Contains(list, p => p.ProductId == low.Id && p.Quantity == 2);
        Assert.DoesNotContain(list, p => p.ProductId == high.Id);
        Assert.Equal(list.OrderBy(p => p.Quantity).Select(p => p.Quantity), list.Select(p => p.Quantity));
        Assert.Equal(HttpStatusCode.BadRequest, (await c.GetAsync("/api/v1/stock/low?threshold=-1")).StatusCode);
    }
}
