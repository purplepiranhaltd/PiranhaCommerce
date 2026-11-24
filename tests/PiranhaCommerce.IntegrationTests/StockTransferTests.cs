using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using PiranhaCommerce.Catalog;
using PiranhaCommerce.Core.Paging;

namespace PiranhaCommerce.IntegrationTests;

/// <summary>Stock transfers: sent by one person, received by another, in transit between, every step in the ledger.</summary>
public class StockTransferTests(PiranhaCommerceFactory factory) : IClassFixture<PiranhaCommerceFactory>
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };
    private static string Tag() => Guid.NewGuid().ToString("N")[..8];
    private static async Task<T> Read<T>(HttpResponseMessage r) => (await r.Content.ReadFromJsonAsync<T>(Json))!;

    private sealed record Staff(Guid Id, HttpClient Client);

    private async Task<Staff> NewAdmin()
    {
        var (id, email, password) = await factory.CreateAdministratorAsync();
        return new Staff(id, await factory.CreateAuthenticatedApiClientAsync(email, password));
    }

    private static async Task<StockLocationDto> NewLocation(HttpClient c) =>
        await Read<StockLocationDto>(await c.PostAsJsonAsync("/api/v1/stock-locations", new StockLocationInput { Code = "L" + Tag(), Name = "Place " + Tag(), Active = true }));

    private static async Task<ProductDto> NewProduct(HttpClient c, StockLocationDto at, decimal quantity)
    {
        var p = await Read<ProductDto>(await c.PostAsJsonAsync("/api/v1/products", new ProductInput { Name = "T " + Tag(), Sku = "T-" + Tag(), Price = 5m }));
        var adj = await c.PostAsJsonAsync($"/api/v1/products/{p.Id}/stock/adjustments", new StockAdjustmentInput { Delta = quantity, LocationId = at.Id, Reason = "Delivery" });
        Assert.Equal(HttpStatusCode.OK, adj.StatusCode);
        return p;
    }

    private static async Task<decimal> At(HttpClient c, ProductDto p, StockLocationDto loc) =>
        (await Read<StockLevelDto>(await c.GetAsync($"/api/v1/products/{p.Id}/stock"))).Locations.FirstOrDefault(l => l.LocationId == loc.Id)?.Quantity ?? 0m;

    private static Task<HttpResponseMessage> Send(HttpClient c, StockLocationDto from, StockLocationDto to, params (ProductDto Product, decimal Qty)[] lines) =>
        c.PostAsJsonAsync("/api/v1/stock/transfers", new StockTransferInput
        {
            FromLocationId = from.Id, ToLocationId = to.Id, Note = "van",
            Lines = lines.Select(l => new StockTransferLineInput { ProductId = l.Product.Id, Quantity = l.Qty }).ToList(),
        });

    [Fact]
    public async Task A_transfer_is_sent_by_one_person_in_transit_then_received_by_another_and_the_ledger_explains_it()
    {
        var a = await NewAdmin(); var b = await NewAdmin();
        var x = await NewLocation(a.Client); var y = await NewLocation(a.Client);
        var p = await NewProduct(a.Client, x, 10);

        var sent = await Read<StockTransferDto>(await Send(a.Client, x, y, (p, 3m)));
        Assert.Equal(StockTransferStatus.InTransit, sent.Status);
        Assert.StartsWith("TRF-", sent.Reference);
        Assert.Equal(7m, await At(a.Client, p, x));
        Assert.Equal(0m, await At(a.Client, p, y));

        // In transit: on neither shelf, but visible per product and route, and on the product's level.
        var level = await Read<StockLevelDto>(await a.Client.GetAsync($"/api/v1/products/{p.Id}/stock"));
        Assert.Equal(3m, level.InTransit);
        Assert.Equal(7m, level.Quantity);
        var transit = await Read<List<InTransitDto>>(await a.Client.GetAsync("/api/v1/stock/in-transit"));
        Assert.Contains(transit, t => t.ProductId == p.Id && t.FromLocationId == x.Id && t.ToLocationId == y.Id && t.Quantity == 3m);

        var received = await Read<StockTransferDto>(await b.Client.PostAsync($"/api/v1/stock/transfers/{sent.Id}/receive", null));
        Assert.Equal(StockTransferStatus.Received, received.Status);
        Assert.False(received.HasDiscrepancy);
        Assert.Equal(3m, received.Lines.Single().QuantityReceived);
        Assert.Equal(7m, await At(a.Client, p, x));
        Assert.Equal(3m, await At(a.Client, p, y));
        Assert.Equal(0m, (await Read<StockLevelDto>(await a.Client.GetAsync($"/api/v1/products/{p.Id}/stock"))).InTransit);

        var moves = (await Read<PagedResult<StockMovementDto>>(await a.Client.GetAsync($"/api/v1/products/{p.Id}/stock/movements"))).Items;
        Assert.Contains(moves, m => m.Reason == "Transfer sent" && m.Delta == -3m && m.LocationId == x.Id && m.Reference == sent.Reference);
        Assert.Contains(moves, m => m.Reason == "Transfer received" && m.Delta == 3m && m.LocationId == y.Id && m.Reference == sent.Reference);
        Assert.Equal(sent.SentBy, moves.First(m => m.Reason == "Transfer sent").Actor);
        Assert.NotEqual(received.SentBy, received.ReceivedBy);
    }

    [Fact]
    public async Task Receiving_less_than_was_sent_is_recorded_and_flagged()
    {
        var a = await NewAdmin(); var b = await NewAdmin();
        var x = await NewLocation(a.Client); var y = await NewLocation(a.Client);
        var p = await NewProduct(a.Client, x, 10);
        var sent = await Read<StockTransferDto>(await Send(a.Client, x, y, (p, 4m)));

        var r = await b.Client.PostAsJsonAsync($"/api/v1/stock/transfers/{sent.Id}/receive",
            new StockTransferReceiveInput { Lines = [new StockTransferLineInput { ProductId = p.Id, Quantity = 3m }] });
        var received = await Read<StockTransferDto>(r);
        Assert.True(received.HasDiscrepancy);
        Assert.Equal(4m, received.Lines.Single().QuantitySent);
        Assert.Equal(3m, received.Lines.Single().QuantityReceived);
        Assert.Equal(3m, await At(a.Client, p, y));
        Assert.Equal(6m, await At(a.Client, p, x)); // the missing one is not put back: it is on the record, not on a shelf

        var flagged = await Read<PagedResult<StockTransferDto>>(await a.Client.GetAsync("/api/v1/stock/transfers?discrepancies=true&pageSize=200"));
        Assert.Contains(flagged.Items, t => t.Id == sent.Id);
    }

    [Fact]
    public async Task Sending_is_refused_for_short_stock_the_same_place_repeated_products_and_unknown_things()
    {
        var a = await NewAdmin();
        var x = await NewLocation(a.Client); var y = await NewLocation(a.Client);
        var p = await NewProduct(a.Client, x, 2);

        Assert.Equal(HttpStatusCode.Conflict, (await Send(a.Client, x, y, (p, 3m))).StatusCode);
        Assert.Equal(2m, await At(a.Client, p, x)); // nothing left the shelf
        Assert.Equal(HttpStatusCode.BadRequest, (await Send(a.Client, x, x, (p, 1m))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await Send(a.Client, x, y, (p, 1m), (p, 1m))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await Send(a.Client, x, y, (p, 0m))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await Send(a.Client, x, y, (p, -1m))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await Send(a.Client, x, y)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await a.Client.PostAsJsonAsync("/api/v1/stock/transfers", new StockTransferInput { FromLocationId = x.Id, ToLocationId = Guid.NewGuid(), Lines = [new() { ProductId = p.Id, Quantity = 1 }] })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await a.Client.PostAsJsonAsync("/api/v1/stock/transfers", new StockTransferInput { FromLocationId = x.Id, ToLocationId = y.Id, Lines = [new() { ProductId = Guid.NewGuid(), Quantity = 1 }] })).StatusCode);

        var viewer = await factory.CreateReadOnlyUserAsync();
        var read = await factory.CreateAuthenticatedApiClientAsync(viewer.Email, viewer.Password);
        Assert.Equal(HttpStatusCode.Forbidden, (await Send(read, x, y, (p, 1m))).StatusCode);
    }

    [Fact]
    public async Task The_person_who_sent_it_cannot_receive_it_unless_the_shop_allows_it()
    {
        var a = await NewAdmin();
        var x = await NewLocation(a.Client); var y = await NewLocation(a.Client);
        var p = await NewProduct(a.Client, x, 5);
        var sent = await Read<StockTransferDto>(await Send(a.Client, x, y, (p, 1m)));

        Assert.Equal(HttpStatusCode.Forbidden, (await a.Client.PostAsync($"/api/v1/stock/transfers/{sent.Id}/receive", null)).StatusCode);
        Assert.Equal(StockTransferStatus.InTransit, (await Read<StockTransferDto>(await a.Client.GetAsync($"/api/v1/stock/transfers/{sent.Id}"))).Status);

        await using var lenient = new LenientStockFactory();
        var staff = await lenient.CreateAuthenticatedApiClientAsync();
        var lx = await NewLocation(staff); var ly = await NewLocation(staff);
        var lp = await NewProduct(staff, lx, 5);
        var s2 = await Read<StockTransferDto>(await Send(staff, lx, ly, (lp, 1m)));
        Assert.Equal(HttpStatusCode.OK, (await staff.PostAsync($"/api/v1/stock/transfers/{s2.Id}/receive", null)).StatusCode);
    }

    private sealed class LenientStockFactory : PiranhaCommerceFactory
    {
        protected override IReadOnlyDictionary<string, string?> ExtraSettings => new Dictionary<string, string?> { ["PiranhaCommerce:Stock:AllowSameUserToReceive"] = "true" };
    }

    [Fact]
    public async Task A_transfer_can_be_cancelled_while_in_transit_and_the_stock_goes_back_but_never_after_it_is_received()
    {
        var a = await NewAdmin(); var b = await NewAdmin();
        var x = await NewLocation(a.Client); var y = await NewLocation(a.Client);
        var p = await NewProduct(a.Client, x, 5);

        var t1 = await Read<StockTransferDto>(await Send(a.Client, x, y, (p, 2m)));
        Assert.Equal(3m, await At(a.Client, p, x));
        var cancelled = await Read<StockTransferDto>(await a.Client.PostAsync($"/api/v1/stock/transfers/{t1.Id}/cancel", null));
        Assert.Equal(StockTransferStatus.Cancelled, cancelled.Status);
        Assert.Equal(5m, await At(a.Client, p, x));
        Assert.Equal(HttpStatusCode.Conflict, (await b.Client.PostAsync($"/api/v1/stock/transfers/{t1.Id}/receive", null)).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await a.Client.PostAsync($"/api/v1/stock/transfers/{t1.Id}/cancel", null)).StatusCode);

        var t2 = await Read<StockTransferDto>(await Send(a.Client, x, y, (p, 1m)));
        Assert.Equal(HttpStatusCode.OK, (await b.Client.PostAsync($"/api/v1/stock/transfers/{t2.Id}/receive", null)).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await a.Client.PostAsync($"/api/v1/stock/transfers/{t2.Id}/cancel", null)).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await b.Client.PostAsync($"/api/v1/stock/transfers/{t2.Id}/receive", null)).StatusCode);
        Assert.Equal(1m, await At(a.Client, p, y)); // received once
        Assert.Equal(HttpStatusCode.NotFound, (await a.Client.PostAsync($"/api/v1/stock/transfers/{Guid.NewGuid()}/cancel", null)).StatusCode);
    }

    [Fact]
    public async Task Receiving_and_cancelling_at_the_same_moment_move_the_stock_exactly_once()
    {
        var a = await NewAdmin(); var b = await NewAdmin(); var c = await NewAdmin();
        var x = await NewLocation(a.Client); var y = await NewLocation(a.Client);
        var p = await NewProduct(a.Client, x, 5);
        for (var i = 0; i < 3; i++)
        {
            var t = await Read<StockTransferDto>(await Send(a.Client, x, y, (p, 1m)));
            var results = await Task.WhenAll(
                b.Client.PostAsync($"/api/v1/stock/transfers/{t.Id}/receive", null),
                c.Client.PostAsync($"/api/v1/stock/transfers/{t.Id}/receive", null),
                a.Client.PostAsync($"/api/v1/stock/transfers/{t.Id}/cancel", null));
            Assert.Equal(1, results.Count(r => r.StatusCode == HttpStatusCode.OK));
        }
        // Whatever won each time, every unit is on a shelf: nothing lost, nothing doubled.
        Assert.Equal(5m, await At(a.Client, p, x) + await At(a.Client, p, y));
        Assert.Equal(0m, (await Read<StockLevelDto>(await a.Client.GetAsync($"/api/v1/products/{p.Id}/stock"))).InTransit);
    }

    [Fact]
    public async Task A_user_limited_to_locations_can_only_send_receive_and_adjust_there()
    {
        var admin = await factory.CreateAuthenticatedApiClientAsync();
        var sender = await NewAdmin(); var receiver = await NewAdmin();
        var x = await NewLocation(admin); var y = await NewLocation(admin); var z = await NewLocation(admin);
        var p = await NewProduct(admin, x, 10);

        // The sender works at X, the receiver at Y.
        Assert.Equal(HttpStatusCode.OK, (await admin.PutAsJsonAsync($"/api/v1/staff/{sender.Id}/locations", new StaffLocationsInput { LocationIds = [x.Id] })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await admin.PutAsJsonAsync($"/api/v1/staff/{receiver.Id}/locations", new StaffLocationsInput { LocationIds = [y.Id] })).StatusCode);
        var listed = await Read<List<StaffLocationsDto>>(await admin.GetAsync("/api/v1/staff/locations"));
        Assert.Equal([x.Id], listed.Single(u => u.UserId == sender.Id).LocationIds);

        // Not from a place they are not at, and not stock adjustments there either.
        var other = await NewProduct(admin, z, 5);
        Assert.Equal(HttpStatusCode.Forbidden, (await Send(sender.Client, z, y, (other, 1m))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await sender.Client.PostAsJsonAsync($"/api/v1/products/{p.Id}/stock/adjustments", new StockAdjustmentInput { Delta = 1, LocationId = y.Id, Reason = "x" })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await sender.Client.PostAsJsonAsync($"/api/v1/products/{p.Id}/stock/adjustments", new StockAdjustmentInput { Delta = 1, LocationId = x.Id, Reason = "x" })).StatusCode);

        var sent = await Read<StockTransferDto>(await Send(sender.Client, x, y, (p, 2m)));
        // A user at Z cannot receive at Y, but the user at Y can.
        var atZ = await NewAdmin();
        await admin.PutAsJsonAsync($"/api/v1/staff/{atZ.Id}/locations", new StaffLocationsInput { LocationIds = [z.Id] });
        Assert.Equal(HttpStatusCode.Forbidden, (await atZ.Client.PostAsync($"/api/v1/stock/transfers/{sent.Id}/receive", null)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await receiver.Client.PostAsync($"/api/v1/stock/transfers/{sent.Id}/receive", null)).StatusCode);

        // Removing the limit lets them act anywhere again; bad input is refused.
        Assert.Equal(HttpStatusCode.OK, (await admin.PutAsJsonAsync($"/api/v1/staff/{sender.Id}/locations", new StaffLocationsInput { LocationIds = [] })).StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await Send(sender.Client, z, y, (other, 1m))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PutAsJsonAsync($"/api/v1/staff/{sender.Id}/locations", new StaffLocationsInput { LocationIds = [Guid.NewGuid()] })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await admin.PutAsJsonAsync($"/api/v1/staff/{Guid.NewGuid()}/locations", new StaffLocationsInput())).StatusCode);

        var viewer = await factory.CreateReadOnlyUserAsync();
        var read = await factory.CreateAuthenticatedApiClientAsync(viewer.Email, viewer.Password);
        Assert.Equal(HttpStatusCode.Forbidden, (await read.GetAsync("/api/v1/staff/locations")).StatusCode);
    }
}
