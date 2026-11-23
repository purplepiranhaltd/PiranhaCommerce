using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using PiranhaCommerce.Catalog;
using PiranhaCommerce.Customers;
using PiranhaCommerce.Ecommerce;

namespace PiranhaCommerce.IntegrationTests;

/// <summary>Shipping zones (countries and postcode areas), rates set by zone, and carriers with tracking links. An active shipping method changes every checkout, so these run alone and remove what they make.</summary>
[Collection(OrderSettings.Name)]
public class ShippingZoneTests(PiranhaFactory factory) : IClassFixture<PiranhaFactory>, IAsyncLifetime
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };
    private static string Tag() => Guid.NewGuid().ToString("N")[..6];
    private static async Task<T> Read<T>(HttpResponseMessage r) { Assert.True(r.IsSuccessStatusCode, r.StatusCode + " " + await r.Content.ReadAsStringAsync()); return (await r.Content.ReadFromJsonAsync<T>(Json))!; }

    private HttpClient _c = default!;
    private readonly List<Guid> _methods = [], _zones = [], _carriers = [];

    public async Task InitializeAsync() => _c = await factory.CreateAuthenticatedApiClientAsync();

    public async Task DisposeAsync()
    {
        foreach (var id in _methods) await _c.DeleteAsync($"/api/v1/shipping-methods/{id}"); // first: rates hold the zones
        foreach (var id in _zones) await _c.DeleteAsync($"/api/v1/shipping-zones/{id}");
        foreach (var id in _carriers) await _c.DeleteAsync($"/api/v1/shipping-carriers/{id}");
    }

    private static ShippingZoneRegionInput Region(string country, params string[] prefixes) => new() { CountryCode = country, PostcodePrefixes = [.. prefixes] };

    private async Task<HttpResponseMessage> TryZone(string name, params ShippingZoneRegionInput[] regions) =>
        await _c.PostAsJsonAsync("/api/v1/shipping-zones", new ShippingZoneInput { Name = name + " " + Tag(), Regions = [.. regions] }, Json);

    private async Task<ShippingZoneDto> Zone(string name, params ShippingZoneRegionInput[] regions)
    {
        var z = await Read<ShippingZoneDto>(await TryZone(name, regions));
        _zones.Add(z.Id);
        return z;
    }

    private async Task<ShippingMethodDto> Method(string name = "Express")
    {
        var m = await Read<ShippingMethodDto>(await _c.PostAsJsonAsync("/api/v1/shipping-methods", new ShippingMethodInput { Name = name + " " + Tag(), Price = 99m }));
        _methods.Add(m.Id);
        return m;
    }

    private static ShippingRateInput ZoneRate(Guid zone, decimal price) => new() { ZoneId = zone, Price = price };

    private Task<HttpResponseMessage> SetRates(Guid method, params ShippingRateInput[] rates) =>
        _c.PutAsJsonAsync($"/api/v1/shipping-methods/{method}/rates", new ShippingRatesInput { Rates = [.. rates] }, Json);

    private async Task<BasketDto> Basket()
    {
        var p = await Read<ProductDto>(await _c.PostAsJsonAsync("/api/v1/products", new ProductInput { Name = "Z " + Tag(), Sku = "Z-" + Tag(), Price = 10m }));
        await _c.PutAsJsonAsync($"/api/v1/products/{p.Id}/listing", new ProductListingInput { Published = true });
        await _c.PostAsJsonAsync($"/api/v1/products/{p.Id}/stock/adjustments", new StockAdjustmentInput { Delta = 50, Reason = "Delivery" });
        var basket = await Read<BasketDto>(await _c.PostAsJsonAsync("/api/v1/baskets", new BasketCreateInput()));
        await _c.PutAsJsonAsync($"/api/v1/baskets/{basket.Id}/lines/{p.Id}", new BasketLineInput { Quantity = 1 });
        return basket;
    }

    private Task<HttpResponseMessage> Quote(Guid basket, Guid method, string country, string? postcode) =>
        _c.PostAsJsonAsync($"/api/v1/baskets/{basket}/quote", new QuoteInput { ShippingMethodId = method, CountryCode = country, PostalCode = postcode });

    private async Task<decimal> Shipping(Guid basket, Guid method, string country, string? postcode) => (await Read<QuoteDto>(await Quote(basket, method, country, postcode))).ShippingTotal;

    [Fact]
    public void A_destination_is_in_the_zone_with_the_longest_matching_postcode_start_then_its_country_then_everywhere_else()
    {
        Guid mainland = Guid.NewGuid(), highlands = Guid.NewGuid(), islands = Guid.NewGuid(), world = Guid.NewGuid(), birmingham = Guid.NewGuid();
        ShippingZoneRegion R(Guid zone, string country, string prefix = "") => new() { ZoneId = zone, CountryCode = country, Prefix = prefix };
        List<ShippingZoneRegion> regions = [R(mainland, "GB"), R(highlands, "GB", "IV"), R(islands, "GB", "IV9"), R(birmingham, "GB", "B"), R(world, "*")];

        Guid? Where(string country, string? postcode) => ShippingZoneRules.Resolve(regions, country, postcode);
        Assert.Equal(mainland, Where("GB", "LS1 1AA"));
        Assert.Equal(highlands, Where("GB", "iv1 1aa"));       // case and spaces do not matter
        Assert.Equal(islands, Where("GB", "IV9 8XX"));          // the longer start wins
        Assert.Equal(birmingham, Where("GB", "B23 4AB"));
        Assert.Equal(mainland, Where("GB", "BA1 1AA"));         // B means B1, B2... not BA, BB, BT
        Assert.Equal(mainland, Where("GB", "BT1 1AA"));         // (no BT area in these regions)
        Assert.Equal(mainland, Where("GB", null));              // no postcode: the country as a whole
        Assert.Equal(mainland, Where("gb", "  "));
        Assert.Equal(world, Where("FR", "75001"));              // everywhere else
        Assert.Null(ShippingZoneRules.Resolve([R(mainland, "GB")], "FR", null));  // nowhere
    }

    [Fact]
    public async Task Zones_are_made_changed_and_removed_and_a_destination_can_only_be_in_one()
    {
        var mainland = await Zone("Mainland", Region("GB"));
        var highlands = await Zone("Highlands", Region("GB", "iv", "HS", "KW 1"));
        Assert.Equal(["HS", "IV", "KW1"], Assert.Single(highlands.Regions).PostcodePrefixes);

        // The same country and postcode start cannot be in two zones, nor listed twice in one.
        Assert.Equal(HttpStatusCode.BadRequest, (await TryZone("Clash", Region("GB"))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await TryZone("Clash", Region("GB", "IV"))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await TryZone("Twice", Region("DE"), Region("DE"))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await TryZone("Bad", Region("GBR"))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await TryZone("Bad", Region("*", "AB"))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await TryZone("Bad", Region("DE", "--"))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await _c.PostAsJsonAsync("/api/v1/shipping-zones", new ShippingZoneInput { Name = "" }, Json)).StatusCode);

        // Changing a zone replaces its regions, and frees what it let go.
        var put = await Read<ShippingZoneDto>(await _c.PutAsJsonAsync($"/api/v1/shipping-zones/{highlands.Id}", new ShippingZoneInput { Name = "Scotland", Regions = [Region("GB", "IV")] }, Json));
        Assert.Equal("Scotland", put.Name);
        Assert.Equal(["IV"], Assert.Single(put.Regions).PostcodePrefixes);
        var freed = await Zone("Western Isles", Region("GB", "HS"));
        Assert.Equal(HttpStatusCode.NotFound, (await _c.PutAsJsonAsync($"/api/v1/shipping-zones/{Guid.NewGuid()}", new ShippingZoneInput { Name = "x" }, Json)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await _c.GetAsync($"/api/v1/shipping-zones/{Guid.NewGuid()}")).StatusCode);
        Assert.Contains(await Read<List<ShippingZoneDto>>(await _c.GetAsync("/api/v1/shipping-zones")), z => z.Id == freed.Id);

        // A zone that rates use cannot be deleted until they are changed.
        var method = await Method();
        Assert.True((await SetRates(method.Id, ZoneRate(mainland.Id, 5m))).IsSuccessStatusCode);
        Assert.Equal(1, (await Read<ShippingZoneDto>(await _c.GetAsync($"/api/v1/shipping-zones/{mainland.Id}"))).RateCount);
        Assert.Equal(HttpStatusCode.Conflict, (await _c.DeleteAsync($"/api/v1/shipping-zones/{mainland.Id}")).StatusCode);
        Assert.True((await SetRates(method.Id)).IsSuccessStatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await _c.DeleteAsync($"/api/v1/shipping-zones/{mainland.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await _c.DeleteAsync($"/api/v1/shipping-zones/{mainland.Id}")).StatusCode);

        var (email, password) = await factory.CreateReadOnlyUserAsync();
        var reader = await factory.CreateAuthenticatedApiClientAsync(email, password);
        Assert.Equal(HttpStatusCode.OK, (await reader.GetAsync("/api/v1/shipping-zones")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await reader.PostAsJsonAsync("/api/v1/shipping-zones", new ShippingZoneInput { Name = "x", Regions = [Region("NL")] }, Json)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await reader.DeleteAsync($"/api/v1/shipping-zones/{freed.Id}")).StatusCode);
    }

    [Fact]
    public async Task A_method_priced_by_zone_charges_the_destinations_zone_and_serves_only_the_zones_it_has_rates_for()
    {
        var mainland = await Zone("Mainland", Region("GB"));
        var highlands = await Zone("Highlands", Region("GB", "IV", "HS"));
        var ni = await Zone("Northern Ireland", Region("GB", "BT"));
        var world = await Zone("World", Region("*"));
        var express = await Method("Express");
        var basket = await Basket();
        Assert.True((await SetRates(express.Id, ZoneRate(mainland.Id, 5m), ZoneRate(highlands.Id, 15m), ZoneRate(ni.Id, 10m))).IsSuccessStatusCode);

        Assert.Equal(5m, await Shipping(basket.Id, express.Id, "GB", "LS1 1AA"));
        Assert.Equal(15m, await Shipping(basket.Id, express.Id, "GB", "iv1 1aa"));
        Assert.Equal(10m, await Shipping(basket.Id, express.Id, "GB", "BT1 1AA"));
        Assert.Equal(5m, await Shipping(basket.Id, express.Id, "GB", null)); // no postcode: the country as a whole
        Assert.Equal(HttpStatusCode.BadRequest, (await Quote(basket.Id, express.Id, "FR", "75001")).StatusCode); // no rate for the zone "everywhere else" lands in

        // Not served means not served: a method with a mainland rate only never gives the Highlands the mainland price.
        var standard = await Method("Standard");
        Assert.True((await SetRates(standard.Id, ZoneRate(mainland.Id, 3m))).IsSuccessStatusCode);
        Assert.Equal(3m, await Shipping(basket.Id, standard.Id, "GB", "LS1 1AA"));
        Assert.Equal(HttpStatusCode.BadRequest, (await Quote(basket.Id, standard.Id, "GB", "IV1 1AA")).StatusCode);

        // The options for a destination list the methods that serve it.
        async Task<List<string>> Options(string country, string? postcode) =>
            (await Read<List<ShippingOptionDto>>(await _c.GetAsync($"/api/v1/baskets/{basket.Id}/shipping-options?countryCode={country}" + (postcode is null ? "" : "&postalCode=" + Uri.EscapeDataString(postcode)))))
                .Where(o => o.Id == express.Id || o.Id == standard.Id).Select(o => o.Name[..4]).Order().ToList();
        Assert.Equal(["Expr", "Stan"], await Options("GB", "LS1 1AA"));
        Assert.Equal(["Expr"], await Options("GB", "IV1 1AA"));
        Assert.Empty(await Options("FR", null));

        // Everywhere else becomes available once the method has a rate for it.
        Assert.True((await SetRates(express.Id, ZoneRate(mainland.Id, 5m), ZoneRate(world.Id, 25m))).IsSuccessStatusCode);
        Assert.Equal(25m, await Shipping(basket.Id, express.Id, "FR", "75001"));
        Assert.Equal(HttpStatusCode.BadRequest, (await Quote(basket.Id, express.Id, "GB", "IV1 1AA")).StatusCode); // Highlands has no rate now
    }

    [Fact]
    public async Task Rates_set_by_zone_are_checked_and_cannot_be_mixed_with_rates_set_by_country()
    {
        var zone = await Zone("Mainland", Region("GB"));
        var method = await Method();
        Assert.Equal(HttpStatusCode.BadRequest, (await SetRates(method.Id, new ShippingRateInput { Price = 1m })).StatusCode);                                            // neither
        Assert.Equal(HttpStatusCode.BadRequest, (await SetRates(method.Id, new ShippingRateInput { CountryCode = "GB", ZoneId = zone.Id, Price = 1m })).StatusCode);   // both
        Assert.Equal(HttpStatusCode.BadRequest, (await SetRates(method.Id, ZoneRate(Guid.NewGuid(), 1m))).StatusCode);                                                  // no such zone
        Assert.Equal(HttpStatusCode.BadRequest, (await SetRates(method.Id, ZoneRate(zone.Id, 1m), new ShippingRateInput { CountryCode = "FR", Price = 2m })).StatusCode); // mixed
        Assert.Equal(HttpStatusCode.BadRequest, (await SetRates(method.Id,
            new ShippingRateInput { ZoneId = zone.Id, MinWeightGrams = 0, MaxWeightGrams = 500, Price = 1m },
            new ShippingRateInput { ZoneId = zone.Id, MinWeightGrams = 400, Price = 2m })).StatusCode);                                                                   // overlapping bands

        // Weight bands work within a zone, and the rates show the zone's name.
        Assert.True((await SetRates(method.Id,
            new ShippingRateInput { ZoneId = zone.Id, MinWeightGrams = 0, MaxWeightGrams = 500, Price = 3m },
            new ShippingRateInput { ZoneId = zone.Id, MinWeightGrams = 500, Price = 6m })).IsSuccessStatusCode);
        var rates = await Read<List<ShippingRateDto>>(await _c.GetAsync($"/api/v1/shipping-methods/{method.Id}/rates"));
        Assert.Equal([(zone.Id, zone.Name, 3m), (zone.Id, zone.Name, 6m)], rates.OrderBy(r => r.Price).Select(r => (r.ZoneId, r.ZoneName, r.Price)));
        Assert.All(rates, r => Assert.Equal("", r.CountryCode));

        // Rates by country still work as before.
        Assert.True((await SetRates(method.Id, new ShippingRateInput { CountryCode = "gb", Price = 4m })).IsSuccessStatusCode);
    }

    [Fact]
    public async Task A_quote_with_a_postcode_and_the_checkout_with_the_same_postcode_agree()
    {
        var mainland = await Zone("Mainland", Region("GB"));
        var highlands = await Zone("Highlands", Region("GB", "IV"));
        var method = await Method();
        Assert.True((await SetRates(method.Id, ZoneRate(mainland.Id, 5m), ZoneRate(highlands.Id, 15m))).IsSuccessStatusCode);

        foreach (var (postcode, expected) in new[] { ("IV2 3AA", 15m), ("LS1 4AP", 5m) })
        {
            var basket = await Basket();
            var quoted = await Shipping(basket.Id, method.Id, "GB", postcode);
            var order = await Read<OrderDto>(await _c.PostAsJsonAsync($"/api/v1/baskets/{basket.Id}/checkout", new CheckoutInput
            {
                Email = $"z-{Tag()}@example.com", ShippingMethodId = method.Id,
                ShippingAddress = new AddressInput { Line1 = "1 High St", City = "Somewhere", PostalCode = postcode, CountryCode = "GB" },
            }, Json));
            Assert.Equal((expected, expected), (quoted, order.ShippingTotal));
        }
    }

    [Fact]
    public async Task A_carrier_gives_a_shipment_its_name_and_a_tracking_link_unless_a_link_is_given()
    {
        async Task<HttpStatusCode> Try(string name, string? template) => (await _c.PostAsJsonAsync("/api/v1/shipping-carriers", new ShippingCarrierInput { Name = name, TrackingUrlTemplate = template }, Json)).StatusCode;
        Assert.Equal(HttpStatusCode.BadRequest, await Try("C", "https://track.example/"));                  // no {tracking}
        Assert.Equal(HttpStatusCode.BadRequest, await Try("C", "ftp://track.example/{tracking}"));
        Assert.Equal(HttpStatusCode.BadRequest, await Try("C", "not a link {tracking}"));
        Assert.Equal(HttpStatusCode.BadRequest, await Try("", null));

        var post = await Read<ShippingCarrierDto>(await _c.PostAsJsonAsync("/api/v1/shipping-carriers",
            new ShippingCarrierInput { Name = "Post " + Tag(), TrackingUrlTemplate = "https://track.example/find?n={tracking}" }, Json));
        _carriers.Add(post.Id);
        var off = await Read<ShippingCarrierDto>(await _c.PostAsJsonAsync("/api/v1/shipping-carriers", new ShippingCarrierInput { Name = "Off " + Tag(), Active = false }, Json));
        _carriers.Add(off.Id);

        async Task<ShipmentDto> Ship(ShipmentInput input)
        {
            var basket = await Basket();
            var order = await Read<OrderDto>(await _c.PostAsJsonAsync($"/api/v1/baskets/{basket.Id}/checkout", new CheckoutInput
            {
                Email = $"c-{Tag()}@example.com", ShippingAddress = new AddressInput { Line1 = "1 High St", City = "Leeds", CountryCode = "GB" },
            }, Json));
            Assert.True((await _c.PostAsJsonAsync($"/api/v1/orders/{order.Id}/stage", new OrderStageInput { Stage = OrderStage.Processing }, Json)).IsSuccessStatusCode);
            return await Read<ShipmentDto>(await _c.PostAsJsonAsync($"/api/v1/orders/{order.Id}/shipments", input, Json));
        }

        var built = await Ship(new ShipmentInput { CarrierId = post.Id, TrackingNumber = "AB 12/34" });
        Assert.Equal((post.Name, "AB 12/34", "https://track.example/find?n=AB%2012%2F34"), (built.Carrier, built.TrackingNumber, built.TrackingUrl));

        var explicitLink = await Ship(new ShipmentInput { CarrierId = post.Id, TrackingNumber = "X1", TrackingUrl = "https://elsewhere.example/x1" });
        Assert.Equal("https://elsewhere.example/x1", explicitLink.TrackingUrl);

        var noNumber = await Ship(new ShipmentInput { CarrierId = post.Id });
        Assert.Equal((post.Name, null), (noNumber.Carrier, noNumber.TrackingUrl));

        // Free text still works, and an unknown or switched-off carrier is refused.
        Assert.Equal(("Pigeon", null), ((await Ship(new ShipmentInput { Carrier = "Pigeon" })).Carrier, (string?)null));
        var basket2 = await Basket();
        var order2 = await Read<OrderDto>(await _c.PostAsJsonAsync($"/api/v1/baskets/{basket2.Id}/checkout", new CheckoutInput
        {
            Email = $"c-{Tag()}@example.com", ShippingAddress = new AddressInput { Line1 = "1 High St", City = "Leeds", CountryCode = "GB" },
        }, Json));
        await _c.PostAsJsonAsync($"/api/v1/orders/{order2.Id}/stage", new OrderStageInput { Stage = OrderStage.Processing }, Json);
        Assert.Equal(HttpStatusCode.BadRequest, (await _c.PostAsJsonAsync($"/api/v1/orders/{order2.Id}/shipments", new ShipmentInput { CarrierId = off.Id }, Json)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await _c.PostAsJsonAsync($"/api/v1/orders/{order2.Id}/shipments", new ShipmentInput { CarrierId = Guid.NewGuid() }, Json)).StatusCode);

        // Parcels keep their copies when the carrier is changed or deleted.
        Assert.True((await _c.PutAsJsonAsync($"/api/v1/shipping-carriers/{post.Id}", new ShippingCarrierInput { Name = "Renamed", TrackingUrlTemplate = "https://new.example/{tracking}" }, Json)).IsSuccessStatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await _c.DeleteAsync($"/api/v1/shipping-carriers/{post.Id}")).StatusCode);
        var kept = await Read<List<ShipmentDto>>(await _c.GetAsync($"/api/v1/orders/{built.OrderId}/shipments"));
        Assert.Equal((post.Name, "https://track.example/find?n=AB%2012%2F34"), (kept.Single().Carrier, kept.Single().TrackingUrl));
        Assert.Equal(HttpStatusCode.NotFound, (await _c.DeleteAsync($"/api/v1/shipping-carriers/{post.Id}")).StatusCode);

        var (email, password) = await factory.CreateReadOnlyUserAsync();
        var reader = await factory.CreateAuthenticatedApiClientAsync(email, password);
        Assert.Equal(HttpStatusCode.OK, (await reader.GetAsync("/api/v1/shipping-carriers")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await reader.PostAsJsonAsync("/api/v1/shipping-carriers", new ShippingCarrierInput { Name = "x" }, Json)).StatusCode);
    }
}
