using System.Net;
using System.Net.Http.Json;
using PiranhaCommerce.Catalog;
using PiranhaCommerce.Core.Paging;

namespace PiranhaCommerce.IntegrationTests;

public class StockLocationTests(PiranhaFactory factory) : IClassFixture<PiranhaFactory>
{
    private static string Tag() => Guid.NewGuid().ToString("N")[..10];

    private static async Task<ProductDto> NewProduct(HttpClient c) =>
        (await (await c.PostAsJsonAsync("/api/v1/products", new ProductInput { Name = "Loc " + Tag(), Price = 1 })).Content.ReadFromJsonAsync<ProductDto>())!;

    private static async Task<StockLocationDto> NewLocation(HttpClient c, bool active = true)
    {
        var r = await c.PostAsJsonAsync("/api/v1/stock-locations", new StockLocationInput { Code = "t" + Tag(), Name = "Test " + Tag(), Active = active });
        Assert.Equal(HttpStatusCode.Created, r.StatusCode);
        return (await r.Content.ReadFromJsonAsync<StockLocationDto>())!;
    }

    private static Task<HttpResponseMessage> Adjust(HttpClient c, Guid product, decimal delta, Guid? location = null, bool allowNegative = false, string reason = "test") =>
        c.PostAsJsonAsync($"/api/v1/products/{product}/stock/adjustments",
            new StockAdjustmentInput { Delta = delta, LocationId = location, AllowNegative = allowNegative, Reason = reason });

    private static async Task<StockLevelDto> Level(HttpClient c, Guid product) =>
        (await c.GetFromJsonAsync<StockLevelDto>($"/api/v1/products/{product}/stock"))!;

    [Fact]
    public async Task A_default_location_always_exists()
    {
        var c = await factory.CreateAuthenticatedApiClientAsync();
        var all = (await c.GetFromJsonAsync<List<StockLocationDto>>("/api/v1/stock-locations"))!;
        var main = Assert.Single(all, l => l.IsDefault);
        Assert.Equal(StockLocations.DefaultId, main.Id);
        Assert.Equal("MAIN", main.Code);

        // it cannot be switched off
        var off = await c.PutAsJsonAsync($"/api/v1/stock-locations/{main.Id}", new StockLocationInput { Code = main.Code, Name = main.Name, Active = false });
        Assert.Equal(HttpStatusCode.BadRequest, off.StatusCode);
    }

    [Fact]
    public async Task Locations_are_created_with_unique_case_insensitive_codes_and_renamed_but_keep_their_code()
    {
        var c = await factory.CreateAuthenticatedApiClientAsync();
        var created = await NewLocation(c);
        Assert.Equal(created.Code, created.Code.ToUpperInvariant());

        var duplicate = await c.PostAsJsonAsync("/api/v1/stock-locations",
            new StockLocationInput { Code = created.Code.ToLowerInvariant(), Name = "Again" });
        Assert.Equal(HttpStatusCode.Conflict, duplicate.StatusCode);

        var bad = await c.PostAsJsonAsync("/api/v1/stock-locations", new StockLocationInput { Code = "no spaces!", Name = "x" });
        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);

        var renamed = await c.PutAsJsonAsync($"/api/v1/stock-locations/{created.Id}", new StockLocationInput { Code = "IGNORED", Name = "Renamed" });
        var dto = (await renamed.Content.ReadFromJsonAsync<StockLocationDto>())!;
        Assert.Equal("Renamed", dto.Name);
        Assert.Equal(created.Code, dto.Code); // the code never changes

        Assert.Equal(HttpStatusCode.NotFound, (await c.GetAsync($"/api/v1/stock-locations/{Guid.NewGuid()}")).StatusCode);
    }

    [Fact]
    public async Task Each_location_keeps_its_own_level_and_the_total_adds_up()
    {
        var c = await factory.CreateAuthenticatedApiClientAsync();
        var p = await NewProduct(c);
        var shop = await NewLocation(c);

        await Adjust(c, p.Id, 10m);                    // default location
        await Adjust(c, p.Id, 3m, shop.Id);

        var level = await Level(c, p.Id);
        Assert.Equal(13m, level.Quantity);
        Assert.Equal(10m, level.Locations.Single(l => l.LocationId == StockLocations.DefaultId).Quantity);
        Assert.Equal(3m, level.Locations.Single(l => l.LocationId == shop.Id).Quantity);

        // the "never below zero" rule applies per location, not to the total
        Assert.Equal(HttpStatusCode.Conflict, (await Adjust(c, p.Id, -4m, shop.Id)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await Adjust(c, p.Id, -3m, shop.Id)).StatusCode);
        Assert.Equal(10m, (await Level(c, p.Id)).Quantity);
    }

    [Fact]
    public async Task Unknown_and_inactive_locations_are_refused()
    {
        var c = await factory.CreateAuthenticatedApiClientAsync();
        var p = await NewProduct(c);
        var off = await NewLocation(c, active: false);

        Assert.Equal(HttpStatusCode.BadRequest, (await Adjust(c, p.Id, 1m, Guid.NewGuid())).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await Adjust(c, p.Id, 1m, off.Id)).StatusCode);
        Assert.Equal(0m, (await Level(c, p.Id)).Quantity);
    }

    [Fact]
    public async Task Movements_record_location_and_the_balance_after_each_one()
    {
        var c = await factory.CreateAuthenticatedApiClientAsync();
        var p = await NewProduct(c);
        var shop = await NewLocation(c);

        await Adjust(c, p.Id, 5m, shop.Id);
        await Adjust(c, p.Id, -2m, shop.Id);
        await Adjust(c, p.Id, 9m);

        var moves = (await c.GetFromJsonAsync<PagedResult<StockMovementDto>>($"/api/v1/products/{p.Id}/stock/movements"))!.Items;
        Assert.Equal([5m, 3m], moves.Where(m => m.LocationId == shop.Id).OrderBy(m => m.CreatedUtc).ThenBy(m => m.Balance).Select(m => m.Balance!.Value));
        Assert.Equal(9m, moves.Single(m => m.LocationId == StockLocations.DefaultId).Balance); // independent of the other location
    }

    [Fact]
    public async Task Concurrent_adjustments_at_different_locations_do_not_interfere()
    {
        var c = await factory.CreateAuthenticatedApiClientAsync();
        var p = await NewProduct(c);
        var shop = await NewLocation(c);

        var tasks = Enumerable.Range(0, 8).SelectMany(_ => new[] { Adjust(c, p.Id, 1m), Adjust(c, p.Id, 2m, shop.Id) });
        Assert.All(await Task.WhenAll(tasks), r => Assert.Equal(HttpStatusCode.OK, r.StatusCode));

        var level = await Level(c, p.Id);
        Assert.Equal(8m, level.Locations.Single(l => l.LocationId == StockLocations.DefaultId).Quantity);
        Assert.Equal(16m, level.Locations.Single(l => l.LocationId == shop.Id).Quantity);
    }

    [Fact]
    public async Task Negative_stock_report_lists_a_product_until_it_is_corrected()
    {
        var c = await factory.CreateAuthenticatedApiClientAsync();
        var p = await NewProduct(c);
        var till = await NewLocation(c);

        await Adjust(c, p.Id, 1m, till.Id, reason: "Goods in");
        // EPOS sells two of an item the system thinks it has one of: refused by default, recorded when the channel allows it
        Assert.Equal(HttpStatusCode.Conflict, (await Adjust(c, p.Id, -2m, till.Id)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await Adjust(c, p.Id, -2m, till.Id, allowNegative: true, reason: "EPOS sale")).StatusCode);

        var report = (await c.GetFromJsonAsync<List<NegativeStockDto>>("/api/v1/stock/negative"))!;
        var row = Assert.Single(report, n => n.ProductId == p.Id);
        Assert.Equal(-1m, row.Quantity);
        Assert.Equal(till.Id, row.LocationId);
        Assert.Equal(p.Name, row.ProductName);

        // the recount: record the true quantity and it drops off the report
        await Adjust(c, p.Id, 1m, till.Id, reason: "Recount");
        report = (await c.GetFromJsonAsync<List<NegativeStockDto>>("/api/v1/stock/negative"))!;
        Assert.DoesNotContain(report, n => n.ProductId == p.Id);
    }

    [Fact]
    public async Task Read_only_users_can_read_locations_and_the_report_but_not_change_them()
    {
        var (email, password) = await factory.CreateReadOnlyUserAsync();
        var reader = await factory.CreateAuthenticatedApiClientAsync(email, password);

        Assert.Equal(HttpStatusCode.OK, (await reader.GetAsync("/api/v1/stock-locations")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await reader.GetAsync("/api/v1/stock/negative")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden,
            (await reader.PostAsJsonAsync("/api/v1/stock-locations", new StockLocationInput { Code = "X" + Tag(), Name = "n" })).StatusCode);
    }
}
