using System.Net;
using System.Net.Http.Json;
using PiranhaCommerce.Catalog;
using PiranhaCommerce.Core.Paging;

namespace PiranhaCommerce.IntegrationTests;

public class StockTests(PiranhaFactory factory) : IClassFixture<PiranhaFactory>
{
    private static string Tag() => Guid.NewGuid().ToString("N")[..10];

    private static async Task<ProductDto> NewProduct(HttpClient c) =>
        (await (await c.PostAsJsonAsync("/api/v1/products", new ProductInput { Name = "Stock " + Tag(), Price = 1 })).Content.ReadFromJsonAsync<ProductDto>())!;

    private static Task<HttpResponseMessage> Adjust(HttpClient c, Guid id, decimal delta, string reason = "test", bool allowNegative = false) =>
        c.PostAsJsonAsync($"/api/v1/products/{id}/stock/adjustments", new StockAdjustmentInput { Delta = delta, Reason = reason, AllowNegative = allowNegative });

    private static async Task<decimal> Level(HttpClient c, Guid id) =>
        (await c.GetFromJsonAsync<StockLevelDto>($"/api/v1/products/{id}/stock"))!.Quantity;

    [Fact]
    public async Task New_product_has_zero_stock_and_adjustments_are_recorded()
    {
        var c = await factory.CreateAuthenticatedApiClientAsync();
        var p = await NewProduct(c);
        Assert.Equal(0m, await Level(c, p.Id));

        var r = await Adjust(c, p.Id, 10.5m, "Delivery");
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        var result = (await r.Content.ReadFromJsonAsync<StockAdjustmentResult>())!;
        Assert.Equal(10.5m, result.Quantity);
        Assert.Equal(PiranhaFactory.AdminEmail, result.Movement.Actor);

        await Adjust(c, p.Id, -0.25m, "Sample");
        Assert.Equal(10.25m, await Level(c, p.Id));

        var moves = (await c.GetFromJsonAsync<PagedResult<StockMovementDto>>($"/api/v1/products/{p.Id}/stock/movements"))!;
        Assert.Equal(2, moves.TotalCount);
        Assert.Equal(10.25m, moves.Items.Sum(m => m.Delta)); // the ledger always explains the level
    }

    [Fact]
    public async Task Cannot_go_below_zero_unless_the_caller_allows_it()
    {
        var c = await factory.CreateAuthenticatedApiClientAsync();
        var p = await NewProduct(c);
        await Adjust(c, p.Id, 2m);

        Assert.Equal(HttpStatusCode.Conflict, (await Adjust(c, p.Id, -3m)).StatusCode);
        Assert.Equal(2m, await Level(c, p.Id));
        Assert.Equal(1, (await c.GetFromJsonAsync<PagedResult<StockMovementDto>>($"/api/v1/products/{p.Id}/stock/movements"))!.Items.Count(m => m.Delta == 2m));
        Assert.Equal(1, (await c.GetFromJsonAsync<PagedResult<StockMovementDto>>($"/api/v1/products/{p.Id}/stock/movements"))!.TotalCount); // refused attempt left no movement

        Assert.Equal(HttpStatusCode.OK, (await Adjust(c, p.Id, -3m, "EPOS sale", allowNegative: true)).StatusCode);
        Assert.Equal(-1m, await Level(c, p.Id));
    }

    [Fact]
    public async Task Concurrent_adjustments_all_apply_and_the_level_equals_the_ledger()
    {
        var c = await factory.CreateAuthenticatedApiClientAsync();
        var p = await NewProduct(c);

        var results = await Task.WhenAll(Enumerable.Range(1, 20).Select(i => Adjust(c, p.Id, i)));
        Assert.All(results, r => Assert.Equal(HttpStatusCode.OK, r.StatusCode));

        var expected = Enumerable.Range(1, 20).Sum(); // 210
        Assert.Equal(expected, await Level(c, p.Id));
        var moves = (await c.GetFromJsonAsync<PagedResult<StockMovementDto>>($"/api/v1/products/{p.Id}/stock/movements?pageSize=100"))!;
        Assert.Equal(20, moves.TotalCount);
        Assert.Equal(expected, moves.Items.Sum(m => m.Delta));
    }

    [Fact]
    public async Task Concurrent_decrements_never_oversell()
    {
        var c = await factory.CreateAuthenticatedApiClientAsync();
        var p = await NewProduct(c);
        await Adjust(c, p.Id, 5m);

        var results = await Task.WhenAll(Enumerable.Range(0, 12).Select(_ => Adjust(c, p.Id, -1m, "Sale")));

        Assert.Equal(5, results.Count(r => r.StatusCode == HttpStatusCode.OK));
        Assert.Equal(7, results.Count(r => r.StatusCode == HttpStatusCode.Conflict));
        Assert.Equal(0m, await Level(c, p.Id));
        Assert.Equal(6, (await c.GetFromJsonAsync<PagedResult<StockMovementDto>>($"/api/v1/products/{p.Id}/stock/movements"))!.TotalCount); // 1 receipt + 5 sales
    }

    [Fact]
    public async Task Validation_and_missing_products()
    {
        var c = await factory.CreateAuthenticatedApiClientAsync();
        var p = await NewProduct(c);

        Assert.Equal(HttpStatusCode.BadRequest, (await Adjust(c, p.Id, 0m)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await Adjust(c, p.Id, 1.00001m)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await Adjust(c, p.Id, 1m, reason: "")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await Adjust(c, Guid.NewGuid(), 1m)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await c.GetAsync($"/api/v1/products/{Guid.NewGuid()}/stock")).StatusCode);
    }

    [Fact]
    public async Task Read_only_users_cannot_adjust()
    {
        var (email, password) = await factory.CreateReadOnlyUserAsync();
        var reader = await factory.CreateAuthenticatedApiClientAsync(email, password);
        var admin = await factory.CreateAuthenticatedApiClientAsync();
        var p = await NewProduct(admin);

        Assert.Equal(HttpStatusCode.OK, (await reader.GetAsync($"/api/v1/products/{p.Id}/stock")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await Adjust(reader, p.Id, 1m)).StatusCode);
    }

    [Fact]
    public async Task A_product_with_stock_history_cannot_be_deleted()
    {
        var c = await factory.CreateAuthenticatedApiClientAsync();
        var withHistory = await NewProduct(c);
        await Adjust(c, withHistory.Id, 1m);
        Assert.Equal(HttpStatusCode.Conflict, (await c.DeleteAsync($"/api/v1/products/{withHistory.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await c.GetAsync($"/api/v1/products/{withHistory.Id}")).StatusCode);

        var clean = await NewProduct(c);
        Assert.Equal(HttpStatusCode.NoContent, (await c.DeleteAsync($"/api/v1/products/{clean.Id}")).StatusCode);
    }
}
