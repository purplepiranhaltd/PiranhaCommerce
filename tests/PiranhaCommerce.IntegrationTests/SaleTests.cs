using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using PiranhaCommerce.Catalog;
using PiranhaCommerce.Customers;
using PiranhaCommerce.Ecommerce;

namespace PiranhaCommerce.IntegrationTests;

/// <summary>Sales: dates, percentage or fixed price, products and categories, and how a sale combines with group prices and promo codes.</summary>
[Collection(OrderSettings.Name)]
public class SaleTests(PiranhaFactory factory) : IClassFixture<PiranhaFactory>
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };
    private static string Tag() => Guid.NewGuid().ToString("N")[..8];
    private static async Task<T> Read<T>(HttpResponseMessage r) => (await r.Content.ReadFromJsonAsync<T>(Json))!;

    internal static async Task<ProductDto> NewProduct(HttpClient c, decimal price)
    {
        var p = await Read<ProductDto>(await c.PostAsJsonAsync("/api/v1/products", new ProductInput { Name = "SL " + Tag(), Sku = "SL-" + Tag(), Price = price }));
        await c.PutAsJsonAsync($"/api/v1/products/{p.Id}/listing", new ProductListingInput { Published = true });
        await c.PostAsJsonAsync($"/api/v1/products/{p.Id}/stock/adjustments", new StockAdjustmentInput { Delta = 100, Reason = "Delivery" });
        return p;
    }

    internal static async Task<SaleDto> NewSale(HttpClient staff, decimal percent = 0m, bool allProducts = false, int startsHours = -1, int endsHours = 1, bool enabled = true)
    {
        var r = await staff.PostAsJsonAsync("/api/v1/sales", new SaleInput
        {
            Name = "Sale " + Tag(), StartsUtc = DateTimeOffset.UtcNow.AddHours(startsHours), EndsUtc = DateTimeOffset.UtcNow.AddHours(endsHours),
            Percent = percent, AllProducts = allProducts, Enabled = enabled,
        });
        Assert.Equal(HttpStatusCode.Created, r.StatusCode);
        return await Read<SaleDto>(r);
    }

    private static Task<HttpResponseMessage> AddProduct(HttpClient staff, SaleDto s, ProductDto p, decimal? price = null) =>
        staff.PutAsJsonAsync($"/api/v1/sales/{s.Id}/products/{p.Id}", new SaleProductInput { Price = price });

    private static async Task<ShopperPriceDto> GuestPrice(HttpClient anon, ProductDto p) =>
        (await Read<List<ShopperPriceDto>>(await anon.GetAsync($"/api/v1/storefront/guest/prices?productIds={p.Id}"))).Single();

    private async Task<(HttpClient Client, Guid Id)> NewShopper()
    {
        var client = factory.CreateClient();
        var reg = await Read<RegisteredDto>(await client.PostAsJsonAsync("/api/v1/storefront/auth/register", new RegisterInput { Email = $"sl-{Tag()}@example.com", Password = "a-long-enough-password" }));
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", reg.Token.AccessToken);
        return (client, reg.Profile.Id);
    }

    [Fact]
    public async Task A_sale_only_changes_prices_while_it_is_running_and_enabled_and_shows_its_status()
    {
        var staff = await factory.CreateAuthenticatedApiClientAsync();
        var anon = factory.CreateClient();
        var p = await NewProduct(staff, 100m);

        var running = await NewSale(staff, 25m);
        Assert.Equal(SaleStatus.Running, running.Status);
        await AddProduct(staff, running, p);
        var price = await GuestPrice(anon, p);
        Assert.Equal((100m, 75m, true), (price.RegularPrice, price.Price, price.Discounted));
        Assert.Equal(running.EndsUtc, price.SaleEndsUtc);

        // Pulled early: back to the regular price. Held back until later, or already over: the same.
        var off = Assert.IsType<SaleDto>(await Read<SaleDto>(await staff.PutAsJsonAsync($"/api/v1/sales/{running.Id}", new SaleInput { Name = running.Name, StartsUtc = running.StartsUtc, EndsUtc = running.EndsUtc, Percent = 25m, Enabled = false })));
        Assert.Equal(SaleStatus.Off, off.Status);
        Assert.Equal(100m, (await GuestPrice(anon, p)).Price);
        Assert.Null((await GuestPrice(anon, p)).SaleEndsUtc);

        var q = await NewProduct(staff, 100m);
        var later = await NewSale(staff, 50m, startsHours: 2, endsHours: 3);
        var over = await NewSale(staff, 50m, startsHours: -3, endsHours: -2);
        await AddProduct(staff, later, q); await AddProduct(staff, over, q);
        Assert.Equal((SaleStatus.Scheduled, SaleStatus.Ended), (later.Status, over.Status));
        Assert.Equal(100m, (await GuestPrice(anon, q)).Price);

        var list = await Read<List<SaleDto>>(await staff.GetAsync("/api/v1/sales?status=Scheduled"));
        Assert.Contains(list, s => s.Id == later.Id);
        Assert.DoesNotContain(list, s => s.Id == over.Id);
    }

    [Fact]
    public async Task A_fixed_sale_price_beats_the_percentage_a_product_without_one_uses_the_percentage_and_a_sale_never_raises_a_price()
    {
        var staff = await factory.CreateAuthenticatedApiClientAsync();
        var anon = factory.CreateClient();
        var sale = await NewSale(staff, 10m);
        var fixedOne = await NewProduct(staff, 50m); var pct = await NewProduct(staff, 50m); var dearer = await NewProduct(staff, 50m); var untouched = await NewProduct(staff, 50m);
        await AddProduct(staff, sale, fixedOne, 20m);
        await AddProduct(staff, sale, pct);
        await AddProduct(staff, sale, dearer, 80m); // a "sale" price above the regular price is ignored

        Assert.Equal(20m, (await GuestPrice(anon, fixedOne)).Price);
        Assert.Equal(45m, (await GuestPrice(anon, pct)).Price);
        var d = await GuestPrice(anon, dearer);
        Assert.Equal((50m, false, null), (d.Price, d.Discounted, d.SaleEndsUtc));
        Assert.Equal(50m, (await GuestPrice(anon, untouched)).Price); // not in the sale

        // Taking it out of the sale, and deleting the sale, bring the regular price back.
        await staff.DeleteAsync($"/api/v1/sales/{sale.Id}/products/{fixedOne.Id}");
        Assert.Equal(50m, (await GuestPrice(anon, fixedOne)).Price);
        Assert.Equal(HttpStatusCode.NoContent, (await staff.DeleteAsync($"/api/v1/sales/{sale.Id}")).StatusCode);
        Assert.Equal(50m, (await GuestPrice(anon, pct)).Price);
        Assert.Equal(HttpStatusCode.NotFound, (await staff.GetAsync($"/api/v1/sales/{sale.Id}")).StatusCode);
    }

    [Fact]
    public async Task A_category_in_a_sale_covers_its_sub_categories_and_with_several_sales_the_lowest_price_wins()
    {
        var staff = await factory.CreateAuthenticatedApiClientAsync();
        var anon = factory.CreateClient();
        var parent = await Read<CategoryDto>(await staff.PostAsJsonAsync("/api/v1/categories", new CategoryInput { Name = "Sale parent " + Tag() }));
        var child = await Read<CategoryDto>(await staff.PostAsJsonAsync("/api/v1/categories", new CategoryInput { Name = "Sale child " + Tag(), ParentId = parent.Id }));
        var other = await Read<CategoryDto>(await staff.PostAsJsonAsync("/api/v1/categories", new CategoryInput { Name = "Sale other " + Tag() }));
        var inChild = await NewProduct(staff, 100m); var inOther = await NewProduct(staff, 100m);
        await staff.PutAsJsonAsync($"/api/v1/products/{inChild.Id}/categories", new ProductCategoriesInput { CategoryIds = [child.Id] });
        await staff.PutAsJsonAsync($"/api/v1/products/{inOther.Id}/categories", new ProductCategoriesInput { CategoryIds = [other.Id] });

        var sale = await NewSale(staff, 10m);
        Assert.Equal(HttpStatusCode.OK, (await staff.PutAsync($"/api/v1/sales/{sale.Id}/categories/{parent.Id}", null)).StatusCode);
        Assert.Equal(90m, (await GuestPrice(anon, inChild)).Price);
        Assert.Equal(100m, (await GuestPrice(anon, inOther)).Price);

        var bigger = await NewSale(staff, 30m);
        await AddProduct(staff, bigger, inChild);
        Assert.Equal(70m, (await GuestPrice(anon, inChild)).Price); // 10% and 30%: the lower price

        var withoutCategory = await Read<SaleDto>(await staff.DeleteAsync($"/api/v1/sales/{sale.Id}/categories/{parent.Id}"));
        Assert.Empty(withoutCategory.Categories);
        await staff.DeleteAsync($"/api/v1/sales/{bigger.Id}");
        Assert.Equal(100m, (await GuestPrice(anon, inChild)).Price);
        await staff.DeleteAsync($"/api/v1/sales/{sale.Id}");
    }

    [Fact]
    public async Task The_sale_price_is_what_a_guest_basket_quote_and_order_use_and_the_regular_price_is_shown_alongside()
    {
        var staff = await factory.CreateAuthenticatedApiClientAsync();
        var anon = factory.CreateClient();
        var p = await NewProduct(staff, 40m);
        var sale = await NewSale(staff, 25m);
        await AddProduct(staff, sale, p);

        var basket = await Read<BasketDto>(await anon.PostAsync("/api/v1/storefront/guest/baskets", null));
        var filled = await Read<BasketDto>(await anon.PutAsJsonAsync($"/api/v1/storefront/guest/baskets/{basket.Id}/lines/{p.Id}", new BasketLineInput { Quantity = 2 }));
        var line = filled.Lines.Single();
        Assert.Equal((30m, 40m, 60m), (line.UnitPrice, line.RegularPrice, filled.Subtotal));
        Assert.Equal(60m, (await Read<QuoteDto>(await anon.PostAsJsonAsync($"/api/v1/storefront/guest/baskets/{basket.Id}/quote", new QuoteInput { CountryCode = "GB" }))).Subtotal);

        var order = await Read<OrderDto>(await anon.PostAsJsonAsync($"/api/v1/storefront/guest/baskets/{basket.Id}/checkout", new CheckoutInput
        {
            Email = $"sl-{Tag()}@example.com",
            ShippingAddress = new AddressInput { Recipient = "Ada", Line1 = "1 High St", City = "Leeds", PostalCode = "LS1", CountryCode = "GB" },
        }));
        Assert.Equal(60m, order.Subtotal);
        Assert.Equal(30m, order.Lines.Single().UnitPrice);

        // The order keeps what was charged when the sale ends.
        await staff.DeleteAsync($"/api/v1/sales/{sale.Id}");
        Assert.Equal(30m, (await Read<OrderDto>(await staff.GetAsync($"/api/v1/orders/{order.Id}"))).Lines.Single().UnitPrice);
    }

    [Fact]
    public async Task A_sale_and_a_group_price_do_not_add_together_the_lower_wins_and_a_promo_code_comes_off_the_result()
    {
        var staff = await factory.CreateAuthenticatedApiClientAsync();
        var p = await NewProduct(staff, 100m);
        var sale = await NewSale(staff, 20m);
        await AddProduct(staff, sale, p);
        var group = await Read<CustomerGroupDto>(await staff.PostAsJsonAsync("/api/v1/customer-groups", new CustomerGroupInput { Name = "G " + Tag(), DiscountPercent = 10m }));
        var better = await Read<CustomerGroupDto>(await staff.PostAsJsonAsync("/api/v1/customer-groups", new CustomerGroupInput { Name = "B " + Tag(), DiscountPercent = 30m }));
        var (ordinary, _) = await NewShopper();
        var (member, memberId) = await NewShopper();
        var (vip, vipId) = await NewShopper();
        await staff.PutAsync($"/api/v1/customer-groups/{group.Id}/members/{memberId}", null);
        await staff.PutAsync($"/api/v1/customer-groups/{better.Id}/members/{vipId}", null);

        async Task<decimal> Price(HttpClient c) => (await Read<List<ShopperPriceDto>>(await c.GetAsync($"/api/v1/storefront/prices?productIds={p.Id}"))).Single().Price;
        Assert.Equal(80m, await Price(ordinary)); // the sale
        Assert.Equal(80m, await Price(member));   // 10% group is worse than the sale: the sale
        Assert.Equal(70m, await Price(vip));      // 30% group beats it: not 56

        var code = "SL" + Tag().ToUpperInvariant();
        await staff.PostAsJsonAsync("/api/v1/promotions", new PromotionInput { Code = code, Kind = PromotionKind.Percent, Value = 10 }, Json);
        var b = await Read<BasketDto>(await ordinary.PostAsync("/api/v1/storefront/baskets", null));
        await ordinary.PutAsJsonAsync($"/api/v1/storefront/baskets/{b.Id}/lines/{p.Id}", new BasketLineInput { Quantity = 1 });
        await ordinary.PostAsJsonAsync($"/api/v1/storefront/baskets/{b.Id}/promo", new CodeInput { Code = code });
        var quote = await Read<QuoteDto>(await ordinary.PostAsJsonAsync($"/api/v1/storefront/baskets/{b.Id}/quote", new QuoteInput { CountryCode = "GB" }));
        Assert.Equal((80m, 8m), (quote.Subtotal, quote.DiscountTotal));
        await staff.DeleteAsync($"/api/v1/sales/{sale.Id}");
    }

    [Fact]
    public async Task Sales_are_checked_and_only_staff_with_write_access_can_change_them()
    {
        var staff = await factory.CreateAuthenticatedApiClientAsync();
        var now = DateTimeOffset.UtcNow;
        SaleInput Good() => new() { Name = "S " + Tag(), StartsUtc = now, EndsUtc = now.AddDays(1), Percent = 10m };
        async Task<HttpStatusCode> Create(Action<SaleInput> tweak) { var i = Good(); tweak(i); return (await staff.PostAsJsonAsync("/api/v1/sales", i)).StatusCode; }

        Assert.Equal(HttpStatusCode.BadRequest, await Create(i => i.EndsUtc = i.StartsUtc));
        Assert.Equal(HttpStatusCode.BadRequest, await Create(i => i.EndsUtc = i.StartsUtc.AddDays(-1)));
        Assert.Equal(HttpStatusCode.BadRequest, await Create(i => i.Percent = 100.5m));
        Assert.Equal(HttpStatusCode.BadRequest, await Create(i => i.Percent = 10.555m));
        Assert.Equal(HttpStatusCode.BadRequest, await Create(i => i.Name = "  "));
        var s = await NewSale(staff, 5m);
        var p = await NewProduct(staff, 10m);
        Assert.Equal(HttpStatusCode.BadRequest, (await AddProduct(staff, s, p, -1m)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await AddProduct(staff, s, p, 1.23456m)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await staff.PutAsJsonAsync($"/api/v1/sales/{s.Id}/products/{Guid.NewGuid()}", new SaleProductInput())).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await staff.PutAsync($"/api/v1/sales/{Guid.NewGuid()}/categories/{Guid.NewGuid()}", null)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await staff.PutAsJsonAsync($"/api/v1/sales/{Guid.NewGuid()}", Good())).StatusCode);

        var (email, password) = await factory.CreateReadOnlyUserAsync();
        var reader = await factory.CreateAuthenticatedApiClientAsync(email, password);
        Assert.Equal(HttpStatusCode.OK, (await reader.GetAsync("/api/v1/sales")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await reader.PostAsJsonAsync("/api/v1/sales", Good())).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await reader.DeleteAsync($"/api/v1/sales/{s.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await factory.CreateClient().GetAsync("/api/v1/sales")).StatusCode);
        await staff.DeleteAsync($"/api/v1/sales/{s.Id}");
    }

    private sealed class OwnFactory : PiranhaFactory { }

    [Fact]
    public async Task A_sale_on_all_products_lowers_every_price_and_stops_when_it_ends()
    {
        // Its own factory (and database), because a sale on everything would change prices under every other test.
        await using var own = new OwnFactory();
        var staff = await own.CreateAuthenticatedApiClientAsync();
        var anon = own.CreateClient();
        var a = await NewProduct(staff, 10m); var b = await NewProduct(staff, 200m);
        var sale = await NewSale(staff, 50m, allProducts: true);
        Assert.Equal((5m, 100m), ((await GuestPrice(anon, a)).Price, (await GuestPrice(anon, b)).Price));

        await staff.PutAsJsonAsync($"/api/v1/sales/{sale.Id}", new SaleInput { Name = sale.Name, StartsUtc = DateTimeOffset.UtcNow.AddHours(-2), EndsUtc = DateTimeOffset.UtcNow.AddSeconds(-1), Percent = 50m, AllProducts = true });
        Assert.Equal((10m, 200m), ((await GuestPrice(anon, a)).Price, (await GuestPrice(anon, b)).Price));
    }
}
