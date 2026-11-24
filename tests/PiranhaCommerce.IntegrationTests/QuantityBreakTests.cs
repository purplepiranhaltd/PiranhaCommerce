using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using PiranhaCommerce.Catalog;
using PiranhaCommerce.Customers;
using PiranhaCommerce.Ecommerce;

namespace PiranhaCommerce.IntegrationTests;

[Collection(OrderSettings.Name)]
public class QuantityBreakTests(PiranhaCommerceFactory factory) : IClassFixture<PiranhaCommerceFactory>
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };
    private static string Tag() => Guid.NewGuid().ToString("N")[..8];
    private static async Task<T> Read<T>(HttpResponseMessage r) { Assert.True(r.IsSuccessStatusCode, r.StatusCode.ToString()); return (await r.Content.ReadFromJsonAsync<T>(Json))!; }

    private static async Task<ProductDto> NewProduct(HttpClient c, decimal price = 10m)
    {
        var p = await Read<ProductDto>(await c.PostAsJsonAsync("/api/v1/products", new ProductInput { Name = "QB " + Tag(), Sku = "QB-" + Tag(), Price = price }));
        await c.PutAsJsonAsync($"/api/v1/products/{p.Id}/listing", new ProductListingInput { Published = true });
        await c.PostAsJsonAsync($"/api/v1/products/{p.Id}/stock/adjustments", new StockAdjustmentInput { Delta = 500, Reason = "Delivery" });
        return p;
    }

    private static Task<HttpResponseMessage> SetBreaks(HttpClient c, Guid productId, params PriceBreakInput[] breaks) =>
        c.PutAsJsonAsync($"/api/v1/products/{productId}/price-breaks", new PriceBreaksInput { Breaks = [.. breaks] });

    private static async Task<BasketLineDto> LineAsync(HttpClient c, ProductDto p, int quantity, Guid? customerId = null)
    {
        var basket = await Read<BasketDto>(await c.PostAsJsonAsync("/api/v1/baskets", new BasketCreateInput { CustomerId = customerId }));
        var set = await c.PutAsJsonAsync($"/api/v1/baskets/{basket.Id}/lines/{p.Id}", new BasketLineInput { Quantity = quantity });
        return Assert.Single((await Read<BasketDto>(set)).Lines);
    }

    [Fact]
    public async Task The_price_falls_at_each_quantity_and_checkout_charges_the_same()
    {
        var c = await factory.CreateAuthenticatedApiClientAsync();
        var p = await NewProduct(c);
        var saved = await Read<List<PriceBreakDto>>(await SetBreaks(c, p.Id,
            new PriceBreakInput { MinQuantity = 20, Percent = 25m }, new PriceBreakInput { MinQuantity = 5, Price = 9m }));
        Assert.Equal([(5, 9m), (20, 7.5m)], saved.Select(b => (b.MinQuantity, b.UnitPrice)));

        var four = await LineAsync(c, p, 4);
        Assert.Equal((10m, 40m), (four.UnitPrice, four.LineTotal));
        var five = await LineAsync(c, p, 5);
        Assert.Equal((9m, 45m), (five.UnitPrice, five.LineTotal));
        Assert.Equal(9m, (await LineAsync(c, p, 19)).UnitPrice);
        var twenty = await LineAsync(c, p, 20);
        Assert.Equal((7.5m, 150m, 10m), (twenty.UnitPrice, twenty.LineTotal, twenty.RegularPrice));

        var basket = await Read<BasketDto>(await c.PostAsJsonAsync("/api/v1/baskets", new BasketCreateInput()));
        await c.PutAsJsonAsync($"/api/v1/baskets/{basket.Id}/lines/{p.Id}", new BasketLineInput { Quantity = 20 });
        var order = await Read<OrderDto>(await c.PostAsJsonAsync($"/api/v1/baskets/{basket.Id}/checkout", new CheckoutInput
        {
            Email = $"qb-{Tag()}@example.com", ShippingAddress = new AddressInput { Line1 = "1 High St", City = "Leeds", CountryCode = "GB" },
        }));
        var line = Assert.Single(order.Lines);
        Assert.Equal((7.5m, 150m), (line.UnitPrice, line.LineTotal));
    }

    [Fact]
    public async Task A_break_for_one_group_is_only_for_its_members_and_nothing_makes_a_price_dearer()
    {
        var c = await factory.CreateAuthenticatedApiClientAsync();
        var p = await NewProduct(c);
        var group = await Read<CustomerGroupDto>(await c.PostAsJsonAsync("/api/v1/customer-groups", new CustomerGroupInput { Name = "Trade " + Tag() }));
        var member = await Read<CustomerDto>(await c.PostAsJsonAsync("/api/v1/customers", new CustomerInput { Email = $"m-{Tag()}@example.com" }));
        var other = await Read<CustomerDto>(await c.PostAsJsonAsync("/api/v1/customers", new CustomerInput { Email = $"o-{Tag()}@example.com" }));
        Assert.True((await c.PutAsync($"/api/v1/customer-groups/{group.Id}/members/{member.Id}", null)).IsSuccessStatusCode);

        await SetBreaks(c, p.Id, new PriceBreakInput { MinQuantity = 3, Price = 8m, GroupId = group.Id }, new PriceBreakInput { MinQuantity = 3, Price = 9.5m });
        Assert.Equal(8m, (await LineAsync(c, p, 3, member.Id)).UnitPrice);   // the cheaper of the two that apply to them
        Assert.Equal(9.5m, (await LineAsync(c, p, 3, other.Id)).UnitPrice);  // not in the group
        Assert.Equal(9.5m, (await LineAsync(c, p, 3)).UnitPrice);             // nobody signed in

        // A "break" dearer than the normal price changes nothing.
        await SetBreaks(c, p.Id, new PriceBreakInput { MinQuantity = 2, Price = 12m });
        Assert.Equal(10m, (await LineAsync(c, p, 2)).UnitPrice);

        // And it never beats a better price they already had: a group fixed price of 6.
        await c.PutAsJsonAsync($"/api/v1/products/{p.Id}/group-prices/{group.Id}", new GroupPriceInput { Price = 6m });
        await SetBreaks(c, p.Id, new PriceBreakInput { MinQuantity = 2, Price = 7m });
        Assert.Equal(6m, (await LineAsync(c, p, 2, member.Id)).UnitPrice);
    }

    [Fact]
    public async Task Shoppers_are_shown_the_cheaper_prices_that_are_worth_buying_more_for()
    {
        var c = await factory.CreateAuthenticatedApiClientAsync();
        var p = await NewProduct(c);
        await SetBreaks(c, p.Id, new PriceBreakInput { MinQuantity = 5, Price = 9m }, new PriceBreakInput { MinQuantity = 10, Price = 9.5m }, new PriceBreakInput { MinQuantity = 50, Percent = 30m });
        var prices = await Read<List<ShopperPriceDto>>(await factory.CreateClient().GetAsync($"/api/v1/storefront/guest/prices?productIds={p.Id}&countryCode=GB"));
        var breaks = Assert.Single(prices).Breaks!;
        Assert.Equal([(5, 9m), (50, 7m)], breaks.Select(b => (b.MinQuantity, b.UnitPrice))); // 10 for 9.50 is no better than 5 for 9.00
        Assert.All(breaks, b => Assert.NotNull(b.UnitPriceIncludingTax));
    }

    [Fact]
    public async Task Bad_lists_are_refused_and_only_administrators_change_them()
    {
        var c = await factory.CreateAuthenticatedApiClientAsync();
        var p = await NewProduct(c);
        async Task<HttpStatusCode> Try(params PriceBreakInput[] b) => (await SetBreaks(c, p.Id, b)).StatusCode;
        Assert.Equal(HttpStatusCode.BadRequest, await Try(new PriceBreakInput { MinQuantity = 5 }));                                   // neither price nor percent
        Assert.Equal(HttpStatusCode.BadRequest, await Try(new PriceBreakInput { MinQuantity = 5, Price = 1m, Percent = 5m }));        // both
        Assert.Equal(HttpStatusCode.BadRequest, await Try(new PriceBreakInput { MinQuantity = 1, Price = 1m }));                       // one is the normal price
        Assert.Equal(HttpStatusCode.BadRequest, await Try(new PriceBreakInput { MinQuantity = 5, Percent = 101m }));
        Assert.Equal(HttpStatusCode.BadRequest, await Try(new PriceBreakInput { MinQuantity = 5, Price = 1m }, new PriceBreakInput { MinQuantity = 5, Price = 2m })); // same quantity twice
        Assert.Equal(HttpStatusCode.BadRequest, await Try(new PriceBreakInput { MinQuantity = 5, Price = 1m, GroupId = Guid.NewGuid() }));
        Assert.Equal(HttpStatusCode.BadRequest, await Try([.. Enumerable.Range(2, 21).Select(i => new PriceBreakInput { MinQuantity = i, Price = 1m })]));
        Assert.Equal(HttpStatusCode.NotFound, (await SetBreaks(c, Guid.NewGuid(), new PriceBreakInput { MinQuantity = 5, Price = 1m })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await c.GetAsync($"/api/v1/products/{Guid.NewGuid()}/price-breaks")).StatusCode);

        Assert.Equal(HttpStatusCode.OK, await Try(new PriceBreakInput { MinQuantity = 5, Price = 1m }));
        Assert.Equal(HttpStatusCode.OK, await Try()); // an empty list clears them
        Assert.Empty(await Read<List<PriceBreakDto>>(await c.GetAsync($"/api/v1/products/{p.Id}/price-breaks")));

        var (email, password) = await factory.CreateReadOnlyUserAsync();
        var reader = await factory.CreateAuthenticatedApiClientAsync(email, password);
        Assert.Equal(HttpStatusCode.Forbidden, (await SetBreaks(reader, p.Id, new PriceBreakInput { MinQuantity = 5, Price = 1m })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await reader.GetAsync($"/api/v1/products/{p.Id}/price-breaks")).StatusCode);
    }

    [Fact]
    public async Task Minimum_and_maximum_order_quantities_are_enforced_on_the_basket_and_again_at_checkout()
    {
        var c = await factory.CreateAuthenticatedApiClientAsync();
        var p = await NewProduct(c);
        Assert.Equal(HttpStatusCode.BadRequest, (await c.PutAsJsonAsync($"/api/v1/products/{p.Id}/listing", new ProductListingInput { Published = true, MinOrderQuantity = 5, MaxOrderQuantity = 2 })).StatusCode);
        var listing = await Read<ProductListingDto>(await c.PutAsJsonAsync($"/api/v1/products/{p.Id}/listing", new ProductListingInput { Published = true, MinOrderQuantity = 3, MaxOrderQuantity = 10 }));
        Assert.Equal((3, 10), (listing.MinOrderQuantity, listing.MaxOrderQuantity));

        var basket = await Read<BasketDto>(await c.PostAsJsonAsync("/api/v1/baskets", new BasketCreateInput()));
        async Task<HttpStatusCode> Set(int q) => (await c.PutAsJsonAsync($"/api/v1/baskets/{basket.Id}/lines/{p.Id}", new BasketLineInput { Quantity = q })).StatusCode;
        Assert.Equal(HttpStatusCode.BadRequest, await Set(2));
        Assert.Equal(HttpStatusCode.BadRequest, await Set(11));
        Assert.Equal(HttpStatusCode.OK, await Set(3));
        Assert.Equal(HttpStatusCode.OK, await Set(10));
        Assert.Equal(HttpStatusCode.OK, await Set(0)); // taking it out is always allowed
        await Set(4);

        // The limit moves after the line was added: checkout catches it.
        await c.PutAsJsonAsync($"/api/v1/products/{p.Id}/listing", new ProductListingInput { Published = true, MinOrderQuantity = 6 });
        var refused = await c.PostAsJsonAsync($"/api/v1/baskets/{basket.Id}/checkout", new CheckoutInput
        {
            Email = $"mm-{Tag()}@example.com", ShippingAddress = new AddressInput { Line1 = "1 High St", City = "Leeds", CountryCode = "GB" },
        });
        Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
        Assert.Contains("at least 6", await refused.Content.ReadAsStringAsync());

        // The limits are in the catalogue export.
        var csv = await (await c.GetAsync("/api/v1/products/export?search=" + p.Sku)).Content.ReadAsStringAsync();
        Assert.Contains("min_order_quantity,max_order_quantity", csv.Replace("\r", ""));
    }
}
