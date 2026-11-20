using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using PiranhaCommerce.Catalog;
using PiranhaCommerce.Customers;
using PiranhaCommerce.Ecommerce;

namespace PiranhaCommerce.IntegrationTests;

/// <summary>
/// Shipping methods and tax rules are store-wide settings, so these tests (and every test that places an order) run one at a time
/// and each puts the settings back as it found them.
/// </summary>
[Collection(OrderSettings.Name)]
public class ShippingTaxTests(PiranhaFactory factory) : IClassFixture<PiranhaFactory>
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };

    private static string Tag() => Guid.NewGuid().ToString("N")[..8];

    private static async Task<T> Read<T>(HttpResponseMessage r) => (await r.Content.ReadFromJsonAsync<T>(Json))!;

    private static async Task<ShippingMethodDto> NewMethod(HttpClient c, decimal price, decimal? freeOver = null, bool active = true)
    {
        var r = await c.PostAsJsonAsync("/api/v1/shipping-methods", new ShippingMethodInput { Name = "Ship " + Tag(), Price = price, FreeOver = freeOver, Active = active });
        Assert.Equal(HttpStatusCode.Created, r.StatusCode);
        return await Read<ShippingMethodDto>(r);
    }

    private static async Task<TaxRuleDto> NewRule(HttpClient c, string country, decimal rate)
    {
        var r = await c.PostAsJsonAsync("/api/v1/tax-rules", new TaxRuleInput { CountryCode = country, Name = "VAT " + Tag(), Rate = rate });
        Assert.Equal(HttpStatusCode.Created, r.StatusCode);
        return await Read<TaxRuleDto>(r);
    }

    /// <summary>Removes every shipping method and tax rule, so a test starts from "none configured" and leaves it that way.</summary>
    private static async Task ClearAsync(HttpClient c)
    {
        foreach (var m in await c.GetFromJsonAsync<List<ShippingMethodDto>>("/api/v1/shipping-methods") ?? [])
            await c.DeleteAsync($"/api/v1/shipping-methods/{m.Id}");
        foreach (var t in await c.GetFromJsonAsync<List<TaxRuleDto>>("/api/v1/tax-rules") ?? [])
            await c.DeleteAsync($"/api/v1/tax-rules/{t.Id}");
    }

    private static async Task<BasketDto> BasketOf(HttpClient c, decimal price, int quantity)
    {
        var p = await Read<ProductDto>(await c.PostAsJsonAsync("/api/v1/products", new ProductInput { Name = "ST " + Tag(), Price = price }));
        await c.PutAsJsonAsync($"/api/v1/products/{p.Id}/listing", new ProductListingInput { Published = true });
        await c.PostAsJsonAsync($"/api/v1/products/{p.Id}/stock/adjustments", new StockAdjustmentInput { Delta = 100, Reason = "Delivery" });
        var basket = await Read<BasketDto>(await c.PostAsJsonAsync("/api/v1/baskets", new BasketCreateInput()));
        await c.PutAsJsonAsync($"/api/v1/baskets/{basket.Id}/lines/{p.Id}", new BasketLineInput { Quantity = quantity });
        return basket;
    }

    private static CheckoutInput Checkout(string country = "GB", Guid? methodId = null) => new()
    {
        Email = $"st-{Tag()}@example.com",
        ShippingMethodId = methodId,
        ShippingAddress = new AddressInput { Line1 = "1 High St", City = "Leeds", CountryCode = country },
    };

    [Fact]
    public async Task With_nothing_configured_shipping_and_tax_are_zero()
    {
        var c = await factory.CreateAuthenticatedApiClientAsync();
        await ClearAsync(c);
        var basket = await BasketOf(c, 10m, 2);

        var order = await Read<OrderDto>(await c.PostAsJsonAsync($"/api/v1/baskets/{basket.Id}/checkout", Checkout()));
        Assert.Equal(20m, order.Total);
        Assert.Equal(0m, order.ShippingTotal);
        Assert.Equal(0m, order.TaxTotal);
        Assert.Null(order.TaxName);
    }

    [Fact]
    public async Task Shipping_and_tax_are_added_and_the_quote_matches_what_checkout_charges()
    {
        var c = await factory.CreateAuthenticatedApiClientAsync();
        await ClearAsync(c);
        try
        {
            var method = await NewMethod(c, 4.99m);
            var rule = await NewRule(c, "gb", 20m);
            var basket = await BasketOf(c, 10m, 3); // 30.00 + 4.99 = 34.99; 20% = 6.998 -> 7.00

            var quote = await Read<QuoteDto>(await c.PostAsJsonAsync($"/api/v1/baskets/{basket.Id}/quote", new QuoteInput { ShippingMethodId = method.Id, CountryCode = "GB" }));
            Assert.Equal((30m, 4.99m, 7.00m, 41.99m), (quote.Subtotal, quote.ShippingTotal, quote.TaxTotal, quote.Total));

            var order = await Read<OrderDto>(await c.PostAsJsonAsync($"/api/v1/baskets/{basket.Id}/checkout", Checkout("GB", method.Id)));
            Assert.Equal((quote.Subtotal, quote.ShippingTotal, quote.TaxTotal, quote.Total), (order.Subtotal, order.ShippingTotal, order.TaxTotal, order.Total));
            Assert.Equal(method.Name, order.ShippingMethodName);
            Assert.Equal(rule.Name, order.TaxName);
            Assert.Equal(20m, order.TaxRate);

            // editing or deleting the settings later never changes the order
            await c.PutAsJsonAsync($"/api/v1/shipping-methods/{method.Id}", new ShippingMethodInput { Name = "Renamed", Price = 99m });
            await c.PutAsJsonAsync($"/api/v1/tax-rules/{rule.Id}", new TaxRuleInput { CountryCode = "GB", Name = "Changed", Rate = 5m });
            await c.DeleteAsync($"/api/v1/shipping-methods/{method.Id}");
            var again = await Read<OrderDto>(await c.GetAsync($"/api/v1/orders/{order.Id}"));
            Assert.Equal(41.99m, again.Total);
            Assert.Equal(method.Name, again.ShippingMethodName);
        }
        finally { await ClearAsync(c); }
    }

    [Fact]
    public async Task Tax_uses_the_destination_country_then_the_fallback_and_rounds_to_the_penny()
    {
        var c = await factory.CreateAuthenticatedApiClientAsync();
        await ClearAsync(c);
        try
        {
            await NewRule(c, "GB", 20m);
            await NewRule(c, "*", 7.5m);

            var gb = await Read<QuoteDto>(await c.PostAsJsonAsync($"/api/v1/baskets/{(await BasketOf(c, 10.10m, 1)).Id}/quote", new QuoteInput { CountryCode = "gb" }));
            Assert.Equal(2.02m, gb.TaxTotal);

            var fr = await Read<QuoteDto>(await c.PostAsJsonAsync($"/api/v1/baskets/{(await BasketOf(c, 10.10m, 1)).Id}/quote", new QuoteInput { CountryCode = "FR" }));
            Assert.Equal(0.76m, fr.TaxTotal); // 0.7575 rounds away from zero
            Assert.Equal(7.5m, fr.TaxRate);

            // a country with a rule of its own may be given only once
            var dup = await c.PostAsJsonAsync("/api/v1/tax-rules", new TaxRuleInput { CountryCode = "gb", Name = "Again", Rate = 1m });
            Assert.Equal(HttpStatusCode.BadRequest, dup.StatusCode);
            Assert.Equal(HttpStatusCode.BadRequest, (await c.PostAsJsonAsync("/api/v1/tax-rules", new TaxRuleInput { CountryCode = "GBR", Name = "x", Rate = 1m })).StatusCode);
            Assert.Equal(HttpStatusCode.BadRequest, (await c.PostAsJsonAsync("/api/v1/tax-rules", new TaxRuleInput { CountryCode = "DE", Name = "x", Rate = 101m })).StatusCode);
        }
        finally { await ClearAsync(c); }
    }

    [Fact]
    public async Task Checkout_needs_an_active_shipping_method_once_any_exists_and_free_over_waives_the_price()
    {
        var c = await factory.CreateAuthenticatedApiClientAsync();
        await ClearAsync(c);
        try
        {
            var paid = await NewMethod(c, 5m, freeOver: 50m);
            var off = await NewMethod(c, 1m, active: false);

            var small = await BasketOf(c, 10m, 1);
            Assert.Equal(HttpStatusCode.BadRequest, (await c.PostAsJsonAsync($"/api/v1/baskets/{small.Id}/checkout", Checkout())).StatusCode);                  // none chosen
            Assert.Equal(HttpStatusCode.BadRequest, (await c.PostAsJsonAsync($"/api/v1/baskets/{small.Id}/checkout", Checkout("GB", off.Id))).StatusCode);      // inactive
            Assert.Equal(HttpStatusCode.BadRequest, (await c.PostAsJsonAsync($"/api/v1/baskets/{small.Id}/checkout", Checkout("GB", Guid.NewGuid()))).StatusCode);
            var charged = await Read<OrderDto>(await c.PostAsJsonAsync($"/api/v1/baskets/{small.Id}/checkout", Checkout("GB", paid.Id)));
            Assert.Equal(5m, charged.ShippingTotal);

            var big = await BasketOf(c, 25m, 2); // 50.00 reaches the free-shipping threshold
            var free = await Read<OrderDto>(await c.PostAsJsonAsync($"/api/v1/baskets/{big.Id}/checkout", Checkout("GB", paid.Id)));
            Assert.Equal(0m, free.ShippingTotal);
            Assert.Equal(50m, free.Total);

            var active = await c.GetFromJsonAsync<List<ShippingMethodDto>>("/api/v1/shipping-methods?activeOnly=true");
            Assert.Single(active!);
        }
        finally { await ClearAsync(c); }
    }

    [Fact]
    public async Task Shipping_and_tax_settings_need_the_write_policy()
    {
        var (email, password) = await factory.CreateReadOnlyUserAsync();
        var reader = await factory.CreateAuthenticatedApiClientAsync(email, password);
        Assert.Equal(HttpStatusCode.OK, (await reader.GetAsync("/api/v1/shipping-methods")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await reader.PostAsJsonAsync("/api/v1/shipping-methods", new ShippingMethodInput { Name = "x" })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await reader.PostAsJsonAsync("/api/v1/tax-rules", new TaxRuleInput { CountryCode = "GB", Name = "x", Rate = 1 })).StatusCode);
    }
}

public static class OrderSettings
{
    /// <summary>Tests that place orders or change store-wide order settings must not overlap.</summary>
    public const string Name = "order-settings";
}
