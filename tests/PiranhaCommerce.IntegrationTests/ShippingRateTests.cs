using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using PiranhaCommerce.Catalog;
using PiranhaCommerce.Customers;
using PiranhaCommerce.Ecommerce;

namespace PiranhaCommerce.IntegrationTests;

/// <summary>Shipping that depends on where the parcel goes and what it weighs. Store-wide settings, so one at a time like the other shipping tests.</summary>
[Collection(OrderSettings.Name)]
public class ShippingRateTests(PiranhaCommerceFactory factory) : IClassFixture<PiranhaCommerceFactory>
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };
    private static string Tag() => Guid.NewGuid().ToString("N")[..8];
    private static async Task<T> Read<T>(HttpResponseMessage r) => (await r.Content.ReadFromJsonAsync<T>(Json))!;

    private static async Task ClearAsync(HttpClient c)
    {
        foreach (var m in await c.GetFromJsonAsync<List<ShippingMethodDto>>("/api/v1/shipping-methods") ?? [])
            await c.DeleteAsync($"/api/v1/shipping-methods/{m.Id}");
    }

    private static async Task<ShippingMethodDto> NewMethod(HttpClient c, decimal price, decimal? freeOver = null)
    {
        var r = await c.PostAsJsonAsync("/api/v1/shipping-methods", new ShippingMethodInput { Name = "Rate " + Tag(), Price = price, FreeOver = freeOver });
        Assert.Equal(HttpStatusCode.Created, r.StatusCode);
        return await Read<ShippingMethodDto>(r);
    }

    private static ShippingRateInput Rate(string country, int from, int? to, decimal price) => new() { CountryCode = country, MinWeightGrams = from, MaxWeightGrams = to, Price = price };

    private static Task<HttpResponseMessage> SetRates(HttpClient c, Guid methodId, params ShippingRateInput[] rates)
        => c.PutAsJsonAsync($"/api/v1/shipping-methods/{methodId}/rates", new ShippingRatesInput { Rates = [.. rates] });

    /// <summary>A basket of one product of the given unit price, weight (null for none set) and quantity.</summary>
    private static async Task<BasketDto> BasketOf(HttpClient c, decimal price, int? grams, int quantity)
    {
        var p = await Read<ProductDto>(await c.PostAsJsonAsync("/api/v1/products", new ProductInput { Name = "W " + Tag(), Price = price }));
        Assert.Equal(HttpStatusCode.OK, (await c.PutAsJsonAsync($"/api/v1/products/{p.Id}/listing", new ProductListingInput { Published = true, WeightGrams = grams })).StatusCode);
        await c.PostAsJsonAsync($"/api/v1/products/{p.Id}/stock/adjustments", new StockAdjustmentInput { Delta = 100, Reason = "Delivery" });
        var basket = await Read<BasketDto>(await c.PostAsJsonAsync("/api/v1/baskets", new BasketCreateInput()));
        await c.PutAsJsonAsync($"/api/v1/baskets/{basket.Id}/lines/{p.Id}", new BasketLineInput { Quantity = quantity });
        return basket;
    }

    private static Task<HttpResponseMessage> Quote(HttpClient c, BasketDto b, Guid methodId, string country)
        => c.PostAsJsonAsync($"/api/v1/baskets/{b.Id}/quote", new QuoteInput { ShippingMethodId = methodId, CountryCode = country });

    private static async Task<decimal> ShippingQuoted(HttpClient c, BasketDto b, Guid methodId, string country)
    {
        var r = await Quote(c, b, methodId, country);
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        return (await Read<QuoteDto>(r)).ShippingTotal;
    }

    private static CheckoutInput Checkout(string country, Guid methodId) => new()
    {
        Email = $"rate-{Tag()}@example.com", ShippingMethodId = methodId,
        ShippingAddress = new AddressInput { Line1 = "1 High St", City = "Leeds", CountryCode = country },
    };

    [Fact]
    public void The_price_lookup_prefers_the_exact_country_then_the_fallback_and_serves_nothing_outside_the_bands()
    {
        var method = new ShippingMethod { Name = "m", Price = 4m };
        ShippingRate R(string cc, int min, int? max, decimal price) => new() { ShippingMethodId = method.Id, CountryCode = cc, MinWeightGrams = min, MaxWeightGrams = max, Price = price };

        Assert.Equal(4m, ShippingRates.PriceFor(method, [], "GB", 99_999)); // no rates: flat price everywhere

        var rates = new[] { R("GB", 0, 1000, 3m), R("GB", 1000, 5000, 6m), R("*", 0, null, 15m) };
        Assert.Equal(3m, ShippingRates.PriceFor(method, rates, "GB", 0));
        Assert.Equal(3m, ShippingRates.PriceFor(method, rates, "gb", 999));
        Assert.Equal(6m, ShippingRates.PriceFor(method, rates, "GB", 1000)); // the upper limit is exclusive
        Assert.Null(ShippingRates.PriceFor(method, rates, "GB", 5000));      // GB has its own bands, so the * row does not rescue it
        Assert.Equal(15m, ShippingRates.PriceFor(method, rates, "FR", 123_456));
        Assert.Null(ShippingRates.PriceFor(method, [R("GB", 0, null, 3m)], "FR", 1)); // no * row: not served
    }

    [Fact]
    public void Overlapping_or_backwards_bands_are_a_problem_but_different_countries_may_share_weights()
    {
        Assert.Null(ShippingRates.Problem([Rate("GB", 0, 1000, 1m), Rate("GB", 1000, null, 2m), Rate("FR", 0, null, 3m), Rate("*", 0, null, 9m)]));
        Assert.NotNull(ShippingRates.Problem([Rate("GB", 0, 1000, 1m), Rate("gb", 500, 2000, 2m)]));  // overlap, any letter case
        Assert.NotNull(ShippingRates.Problem([Rate("GB", 0, null, 1m), Rate("GB", 1000, 2000, 2m)])); // an open band covers everything after it
        Assert.NotNull(ShippingRates.Problem([Rate("GB", 1000, 1000, 1m)]));                           // ends where it starts
        Assert.NotNull(ShippingRates.Problem([Rate("GB", 0, 1000, 1m), Rate("GB", 0, 1000, 2m)]));    // the same band twice
    }

    [Fact]
    public async Task A_quote_and_checkout_charge_by_the_destination_and_the_weight_of_the_basket()
    {
        var c = await factory.CreateAuthenticatedApiClientAsync();
        await ClearAsync(c);
        try
        {
            var method = await NewMethod(c, price: 99m); // the flat price is ignored once there are rates
            Assert.Equal(HttpStatusCode.OK, (await SetRates(c, method.Id, Rate("GB", 0, 1000, 3m), Rate("GB", 1000, 5000, 6m), Rate("*", 0, null, 15m))).StatusCode);

            var heavy = await BasketOf(c, 10m, grams: 500, quantity: 3);  // 1500 g
            var light = await BasketOf(c, 10m, grams: 500, quantity: 1);  // 500 g
            var unweighed = await BasketOf(c, 10m, grams: null, quantity: 2); // no weight set: counts as nothing

            Assert.Equal(6m, await ShippingQuoted(c, heavy, method.Id, "GB"));
            Assert.Equal(3m, await ShippingQuoted(c, light, method.Id, "gb"));
            Assert.Equal(3m, await ShippingQuoted(c, unweighed, method.Id, "GB"));
            Assert.Equal(15m, await ShippingQuoted(c, heavy, method.Id, "FR"));

            // Checkout works it out the same way and keeps it on the order.
            var order = await Read<OrderDto>(await c.PostAsJsonAsync($"/api/v1/baskets/{heavy.Id}/checkout", Checkout("GB", method.Id)));
            Assert.Equal(6m, order.ShippingTotal);
            Assert.Equal(36m, order.Total); // 3 x 10.00 goods and 6.00 postage
        }
        finally { await ClearAsync(c); }
    }

    [Fact]
    public async Task A_method_that_does_not_serve_the_destination_or_weight_is_refused_at_quote_and_checkout_and_nothing_is_taken()
    {
        var c = await factory.CreateAuthenticatedApiClientAsync();
        await ClearAsync(c);
        try
        {
            var gbOnly = await NewMethod(c, 1m);
            await SetRates(c, gbOnly.Id, Rate("GB", 0, 2000, 4m));

            var basket = await BasketOf(c, 10m, 500, 1);
            var quote = await Quote(c, basket, gbOnly.Id, "FR");
            Assert.Equal(HttpStatusCode.BadRequest, quote.StatusCode);
            Assert.Contains("not available for this destination", await quote.Content.ReadAsStringAsync());

            var overweight = await BasketOf(c, 10m, 3000, 1);
            Assert.Equal(HttpStatusCode.BadRequest, (await Quote(c, overweight, gbOnly.Id, "GB")).StatusCode);

            var refused = await c.PostAsJsonAsync($"/api/v1/baskets/{basket.Id}/checkout", Checkout("FR", gbOnly.Id));
            Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
            var after = await Read<BasketDto>(await c.GetAsync($"/api/v1/baskets/{basket.Id}"));
            Assert.Equal(BasketStatus.Open, after.Status); // still a basket, not an order

            // Now it is served: the same basket checks out.
            Assert.Equal(HttpStatusCode.OK, (await c.PostAsJsonAsync($"/api/v1/baskets/{basket.Id}/checkout", Checkout("GB", gbOnly.Id))).StatusCode);
        }
        finally { await ClearAsync(c); }
    }

    [Fact]
    public async Task Free_over_still_applies_to_a_rate_and_the_options_list_shows_only_what_the_basket_can_use()
    {
        var c = await factory.CreateAuthenticatedApiClientAsync();
        await ClearAsync(c);
        try
        {
            var flat = await NewMethod(c, 4.99m);
            var ukOnly = await NewMethod(c, 0m, freeOver: 50m);
            await SetRates(c, ukOnly.Id, Rate("GB", 0, 10_000, 7m));

            var small = await BasketOf(c, 10m, 500, 2);   // 20.00
            var big = await BasketOf(c, 30m, 500, 2);     // 60.00: over the free-over amount

            var smallGb = await Read<List<ShippingOptionDto>>(await c.GetAsync($"/api/v1/baskets/{small.Id}/shipping-options?countryCode=GB"));
            Assert.Equal(new[] { (flat.Name, 4.99m), (ukOnly.Name, 7m) }.OrderBy(x => x.Item1), smallGb.Select(o => (o.Name, o.Price)).OrderBy(x => x.Name));
            var bigGb = await Read<List<ShippingOptionDto>>(await c.GetAsync($"/api/v1/baskets/{big.Id}/shipping-options?countryCode=GB"));
            Assert.Equal(0m, bigGb.Single(o => o.Id == ukOnly.Id).Price);
            var smallFr = await Read<List<ShippingOptionDto>>(await c.GetAsync($"/api/v1/baskets/{small.Id}/shipping-options?countryCode=FR"));
            Assert.Equal(flat.Id, Assert.Single(smallFr).Id);

            Assert.Equal(HttpStatusCode.BadRequest, (await c.GetAsync($"/api/v1/baskets/{small.Id}/shipping-options?countryCode=GBR")).StatusCode);
            Assert.Equal(HttpStatusCode.NotFound, (await c.GetAsync($"/api/v1/baskets/{Guid.NewGuid()}/shipping-options?countryCode=GB")).StatusCode);
        }
        finally { await ClearAsync(c); }
    }

    [Fact]
    public async Task A_shopper_can_see_the_options_for_their_own_basket_only()
    {
        var staff = await factory.CreateAuthenticatedApiClientAsync();
        await ClearAsync(staff);
        try
        {
            var method = await NewMethod(staff, 3m);
            var shopper = factory.CreateClient();
            var email = $"opt-{Tag()}@example.com";
            var reg = await Read<RegisteredDto>(await shopper.PostAsJsonAsync("/api/v1/storefront/auth/register", new RegisterInput { Email = email, Password = "a-long-enough-password" }));
            shopper.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", reg.Token.AccessToken);

            var mine = await Read<BasketDto>(await shopper.PostAsync("/api/v1/storefront/baskets", null));
            var options = await Read<List<ShippingOptionDto>>(await shopper.GetAsync($"/api/v1/storefront/baskets/{mine.Id}/shipping-options?countryCode=GB"));
            Assert.Equal(method.Id, Assert.Single(options).Id);

            var someoneElses = await BasketOf(staff, 10m, 100, 1);
            Assert.Equal(HttpStatusCode.NotFound, (await shopper.GetAsync($"/api/v1/storefront/baskets/{someoneElses.Id}/shipping-options?countryCode=GB")).StatusCode);
        }
        finally { await ClearAsync(staff); }
    }

    [Fact]
    public async Task Rates_are_replaced_as_a_set_validated_and_removed_with_the_method_and_only_writers_may_change_them()
    {
        var c = await factory.CreateAuthenticatedApiClientAsync();
        await ClearAsync(c);
        try
        {
            var method = await NewMethod(c, 5m);
            Assert.Equal(0, (await Read<List<ShippingMethodDto>>(await c.GetAsync("/api/v1/shipping-methods"))).Single(m => m.Id == method.Id).RateCount);

            Assert.Equal(HttpStatusCode.OK, (await SetRates(c, method.Id, Rate("gb", 1000, null, 8m), Rate("GB", 0, 1000, 4m), Rate("*", 0, null, 20m))).StatusCode);
            var rates = await Read<List<ShippingRateDto>>(await c.GetAsync($"/api/v1/shipping-methods/{method.Id}/rates"));
            Assert.Equal(new[] { ("*", 0), ("GB", 0), ("GB", 1000) }, rates.Select(r => (r.CountryCode, r.MinWeightGrams))); // upper-cased, in order
            Assert.Equal(3, (await Read<List<ShippingMethodDto>>(await c.GetAsync("/api/v1/shipping-methods"))).Single(m => m.Id == method.Id).RateCount);

            // A bad set changes nothing.
            Assert.Equal(HttpStatusCode.BadRequest, (await SetRates(c, method.Id, Rate("GB", 0, 1000, 1m), Rate("GB", 500, 2000, 2m))).StatusCode);
            Assert.Equal(HttpStatusCode.BadRequest, (await SetRates(c, method.Id, Rate("GBR", 0, null, 1m))).StatusCode);
            Assert.Equal(HttpStatusCode.BadRequest, (await SetRates(c, method.Id, Rate("GB", 0, null, -1m))).StatusCode);
            Assert.Equal(HttpStatusCode.BadRequest, (await SetRates(c, method.Id, Rate("GB", -5, null, 1m))).StatusCode);
            Assert.Equal(3, (await Read<List<ShippingRateDto>>(await c.GetAsync($"/api/v1/shipping-methods/{method.Id}/rates"))).Count);

            Assert.Equal(HttpStatusCode.NotFound, (await SetRates(c, Guid.NewGuid(), Rate("GB", 0, null, 1m))).StatusCode);
            Assert.Equal(HttpStatusCode.NotFound, (await c.GetAsync($"/api/v1/shipping-methods/{Guid.NewGuid()}/rates")).StatusCode);

            var (email, password) = await factory.CreateReadOnlyUserAsync();
            var reader = await factory.CreateAuthenticatedApiClientAsync(email, password);
            Assert.Equal(HttpStatusCode.OK, (await reader.GetAsync($"/api/v1/shipping-methods/{method.Id}/rates")).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await SetRates(reader, method.Id)).StatusCode);

            // An empty list goes back to the flat price.
            Assert.Equal(HttpStatusCode.OK, (await SetRates(c, method.Id)).StatusCode);
            var basket = await BasketOf(c, 10m, 99_999, 1);
            Assert.Equal(5m, await ShippingQuoted(c, basket, method.Id, "FR"));

            // Deleting the method takes its rates with it.
            await SetRates(c, method.Id, Rate("GB", 0, null, 1m));
            Assert.Equal(HttpStatusCode.NoContent, (await c.DeleteAsync($"/api/v1/shipping-methods/{method.Id}")).StatusCode);
            Assert.Equal(HttpStatusCode.NotFound, (await c.GetAsync($"/api/v1/shipping-methods/{method.Id}/rates")).StatusCode);
        }
        finally { await ClearAsync(c); }
    }

    [Fact]
    public async Task A_products_weight_is_set_on_its_listing_and_a_put_without_it_clears_it()
    {
        var c = await factory.CreateAuthenticatedApiClientAsync();
        var p = await Read<ProductDto>(await c.PostAsJsonAsync("/api/v1/products", new ProductInput { Name = "Wt " + Tag(), Price = 5m }));

        var set = await Read<ProductListingDto>(await c.PutAsJsonAsync($"/api/v1/products/{p.Id}/listing", new ProductListingInput { Published = true, WeightGrams = 250 }));
        Assert.Equal(250, set.WeightGrams);
        Assert.Equal(250, (await Read<ProductListingDto>(await c.GetAsync($"/api/v1/products/{p.Id}/listing"))).WeightGrams);

        Assert.Equal(HttpStatusCode.BadRequest, (await c.PutAsJsonAsync($"/api/v1/products/{p.Id}/listing", new ProductListingInput { Published = true, WeightGrams = -1 })).StatusCode);
        Assert.Equal(250, (await Read<ProductListingDto>(await c.GetAsync($"/api/v1/products/{p.Id}/listing"))).WeightGrams);

        var cleared = await Read<ProductListingDto>(await c.PutAsJsonAsync($"/api/v1/products/{p.Id}/listing", new ProductListingInput { Published = true }));
        Assert.Null(cleared.WeightGrams);
    }
}
