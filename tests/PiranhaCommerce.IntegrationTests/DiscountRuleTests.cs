using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using PiranhaCommerce.Catalog;
using PiranhaCommerce.Customers;
using PiranhaCommerce.Ecommerce;

namespace PiranhaCommerce.IntegrationTests;

/// <summary>
/// The discount engine. Discounts here always name their own products, or need a code, so they cannot touch the baskets of tests running at the same time.
/// Shipping settings are store-wide, so these run one at a time.
/// </summary>
[Collection(OrderSettings.Name)]
public class DiscountRuleTests(PiranhaFactory factory) : IClassFixture<PiranhaFactory>, IAsyncLifetime
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };
    private static string Tag() => Guid.NewGuid().ToString("N")[..8];
    private static async Task<T> Read<T>(HttpResponseMessage r) { Assert.True(r.IsSuccessStatusCode, r.StatusCode + " " + await r.Content.ReadAsStringAsync()); return (await r.Content.ReadFromJsonAsync<T>(Json))!; }

    private HttpClient _c = default!;
    private readonly List<Guid> _rules = [], _methods = [];

    public async Task InitializeAsync() => _c = await factory.CreateAuthenticatedApiClientAsync();

    public async Task DisposeAsync()
    {
        foreach (var id in _rules) await _c.DeleteAsync($"/api/v1/discounts/{id}");
        foreach (var id in _methods) await _c.DeleteAsync($"/api/v1/shipping-methods/{id}");
    }

    private async Task<DiscountRuleDto> Rule(Action<DiscountRuleInput> set)
    {
        var input = new DiscountRuleInput { Name = "D " + Tag() };
        set(input);
        var r = await Read<DiscountRuleDto>(await _c.PostAsJsonAsync("/api/v1/discounts", input, Json));
        _rules.Add(r.Id);
        return r;
    }

    private async Task<ProductDto> Product(decimal price)
    {
        var p = await Read<ProductDto>(await _c.PostAsJsonAsync("/api/v1/products", new ProductInput { Name = "Disc " + Tag(), Sku = "DR-" + Tag(), Price = price }));
        await _c.PutAsJsonAsync($"/api/v1/products/{p.Id}/listing", new ProductListingInput { Published = true });
        await _c.PostAsJsonAsync($"/api/v1/products/{p.Id}/stock/adjustments", new StockAdjustmentInput { Delta = 500, Reason = "Delivery" });
        return p;
    }

    private async Task<BasketDto> Basket(Guid? customerId, params (ProductDto Product, int Quantity)[] lines)
    {
        var basket = await Read<BasketDto>(await _c.PostAsJsonAsync("/api/v1/baskets", new BasketCreateInput { CustomerId = customerId }));
        foreach (var (p, q) in lines)
            Assert.True((await _c.PutAsJsonAsync($"/api/v1/baskets/{basket.Id}/lines/{p.Id}", new BasketLineInput { Quantity = q })).IsSuccessStatusCode);
        return basket;
    }

    private async Task<QuoteDto> Quote(Guid basketId, Guid? method = null) =>
        await Read<QuoteDto>(await _c.PostAsJsonAsync($"/api/v1/baskets/{basketId}/quote", new QuoteInput { CountryCode = "GB", ShippingMethodId = method }));

    private Task<HttpResponseMessage> UseCode(Guid basketId, string code) => _c.PostAsJsonAsync($"/api/v1/baskets/{basketId}/promo", new CodeInput { Code = code });

    private Task<HttpResponseMessage> Place(Guid basketId, Guid? customerId = null, Guid? method = null) => _c.PostAsJsonAsync($"/api/v1/baskets/{basketId}/checkout", new CheckoutInput
    {
        Email = $"dr-{Tag()}@example.com", CustomerId = customerId, ShippingMethodId = method,
        ShippingAddress = new AddressInput { Line1 = "1 High St", City = "Leeds", CountryCode = "GB" },
    });

    private async Task<Guid> Shipping(decimal price)
    {
        var m = await Read<ShippingMethodDto>(await _c.PostAsJsonAsync("/api/v1/shipping-methods", new ShippingMethodInput { Name = "Post " + Tag(), Price = price }));
        _methods.Add(m.Id);
        return m.Id;
    }

    [Fact]
    public async Task An_automatic_discount_on_chosen_products_applies_without_a_code_and_the_order_keeps_a_copy()
    {
        var a = await Product(10m);
        var b = await Product(10m);
        var rule = await Rule(r => { r.Kind = DiscountKind.PercentOff; r.Value = 20m; r.ProductIds = [a.Id]; });
        Assert.Equal([a.Id], rule.Products.Select(p => p.Id));

        var basket = await Basket(null, (a, 2), (b, 2));
        var quote = await Quote(basket.Id);
        Assert.Equal(4m, quote.DiscountTotal);
        Assert.Equal(4m, quote.Lines!.Single(l => l.ProductId == a.Id).DiscountAmount);
        Assert.Equal(0m, quote.Lines!.Single(l => l.ProductId == b.Id).DiscountAmount);
        Assert.Equal(36m, quote.Total);
        Assert.Equal((rule.Name, 4m), (quote.Discounts!.Single().Name, quote.Discounts!.Single().Amount));

        var order = await Read<OrderDto>(await Place(basket.Id));
        Assert.Equal((4m, 36m), (order.DiscountTotal, order.Total));
        Assert.Equal(rule.Name, Assert.Single(order.Discounts!).Name);

        // Deleting the rule later leaves the order's copy.
        await _c.DeleteAsync($"/api/v1/discounts/{rule.Id}");
        var again = await Read<OrderDto>(await _c.GetAsync($"/api/v1/orders/{order.Id}"));
        Assert.Equal(rule.Name, Assert.Single(again.Discounts!).Name);
    }

    [Fact]
    public async Task A_category_discount_covers_its_sub_categories_and_a_code_is_needed_when_one_is_set()
    {
        var parent = await Read<CategoryDto>(await _c.PostAsJsonAsync("/api/v1/categories", new CategoryInput { Name = "Top " + Tag() }));
        var child = await Read<CategoryDto>(await _c.PostAsJsonAsync("/api/v1/categories", new CategoryInput { Name = "Sub " + Tag(), ParentId = parent.Id }));
        var inChild = await Product(20m);
        var outside = await Product(20m);
        Assert.True((await _c.PutAsJsonAsync($"/api/v1/products/{inChild.Id}/categories", new ProductCategoriesInput { CategoryIds = [child.Id] })).IsSuccessStatusCode);

        var code = "CAT" + Tag();
        await Rule(r => { r.Code = code; r.Kind = DiscountKind.AmountOff; r.Value = 5m; r.CategoryIds = [parent.Id]; });

        var basket = await Basket(null, (inChild, 1), (outside, 1));
        Assert.Equal(0m, (await Quote(basket.Id)).DiscountTotal); // no code yet
        Assert.True((await UseCode(basket.Id, code.ToLowerInvariant())).IsSuccessStatusCode);
        var quote = await Quote(basket.Id);
        Assert.Equal(5m, quote.DiscountTotal);
        Assert.Equal(5m, quote.Lines!.Single(l => l.ProductId == inChild.Id).DiscountAmount);

        var elsewhere = await Basket(null, (outside, 1));
        Assert.True((await UseCode(elsewhere.Id, code)).IsSuccessStatusCode);
        var none = await Quote(elsewhere.Id);
        Assert.Equal(0m, none.DiscountTotal);
        Assert.Contains("does not apply", none.PromotionProblem);
        Assert.Equal(HttpStatusCode.Conflict, (await Place(elsewhere.Id)).StatusCode); // a code that gives nothing stops the order rather than being ignored
    }

    [Fact]
    public async Task Buy_some_get_some_discounts_the_cheapest_units_in_each_group()
    {
        var p = await Product(10m);
        await Rule(r => { r.Kind = DiscountKind.BuyXGetY; r.BuyQuantity = 2; r.GetQuantity = 1; r.Value = 100m; r.ProductIds = [p.Id]; });
        async Task<decimal> Off(int q) => (await Quote((await Basket(null, (p, q))).Id)).DiscountTotal;
        Assert.Equal(0m, await Off(2));
        Assert.Equal(10m, await Off(3));
        Assert.Equal(10m, await Off(5));
        Assert.Equal(20m, await Off(6));

        var half = await Product(10m);
        await Rule(r => { r.Kind = DiscountKind.BuyXGetY; r.BuyQuantity = 1; r.GetQuantity = 1; r.Value = 50m; r.ProductIds = [half.Id]; });
        Assert.Equal(10m, (await Quote((await Basket(null, (half, 4))).Id)).DiscountTotal); // two pairs, half off one of each

        // Across products the cheapest units are the ones that go.
        var cheap = await Product(4m);
        var dear = await Product(30m);
        await Rule(r => { r.Kind = DiscountKind.BuyXGetY; r.BuyQuantity = 2; r.GetQuantity = 1; r.Value = 100m; r.ProductIds = [cheap.Id, dear.Id]; });
        Assert.Equal(4m, (await Quote((await Basket(null, (cheap, 1), (dear, 2))).Id)).DiscountTotal);
    }

    [Fact]
    public async Task Combinable_discounts_stack_in_priority_order_and_the_best_exclusive_one_beats_them_only_when_worth_more()
    {
        var p = await Product(100m);
        await Rule(r => { r.Kind = DiscountKind.PercentOff; r.Value = 10m; r.Priority = 1; r.ProductIds = [p.Id]; });
        await Rule(r => { r.Kind = DiscountKind.AmountOff; r.Value = 5m; r.Priority = 2; r.ProductIds = [p.Id]; });
        var stacked = await Quote((await Basket(null, (p, 1))).Id);
        Assert.Equal(15m, stacked.DiscountTotal); // 10 off 100, then 5 off the 90
        Assert.Equal(2, stacked.Discounts!.Count);

        var small = await Rule(r => { r.Kind = DiscountKind.PercentOff; r.Value = 5m; r.Stacking = DiscountStacking.Exclusive; r.ProductIds = [p.Id]; });
        var quoteSmall = await Quote((await Basket(null, (p, 1))).Id);
        Assert.Equal(15m, quoteSmall.DiscountTotal); // 5 is worth less than the 15 together: the exclusive one is not used
        Assert.DoesNotContain(quoteSmall.Discounts!, d => d.Name == small.Name);

        var big = await Rule(r => { r.Kind = DiscountKind.PercentOff; r.Value = 30m; r.Stacking = DiscountStacking.Exclusive; r.ProductIds = [p.Id]; });
        var quoteBig = await Quote((await Basket(null, (p, 1))).Id);
        Assert.Equal(30m, quoteBig.DiscountTotal); // worth more than 15: it applies alone
        Assert.Equal(big.Name, Assert.Single(quoteBig.Discounts!).Name);
    }

    [Fact]
    public async Task A_free_shipping_code_waives_delivery_and_is_recorded_on_the_order()
    {
        var p = await Product(10m);
        var method = await Shipping(5m);
        var code = "SHIP" + Tag();
        await Rule(r => { r.Code = code; r.Kind = DiscountKind.FreeShipping; r.MinSubtotal = 20m; });

        var basket = await Basket(null, (p, 3));
        var before = await Quote(basket.Id, method);
        Assert.Equal((5m, 35m), (before.ShippingTotal, before.Total));

        Assert.True((await UseCode(basket.Id, code)).IsSuccessStatusCode);
        var after = await Quote(basket.Id, method);
        Assert.Equal((0m, 30m, 0m), (after.ShippingTotal, after.Total, after.DiscountTotal));
        Assert.True(Assert.Single(after.Discounts!).FreeShipping);

        var order = await Read<OrderDto>(await Place(basket.Id, method: method));
        Assert.Equal((0m, 30m), (order.ShippingTotal, order.Total));
        Assert.True(Assert.Single(order.Discounts!).FreeShipping);

        // Under the minimum it gives nothing and says why.
        var small = await Basket(null, (p, 1));
        await UseCode(small.Id, code);
        var none = await Quote(small.Id, method);
        Assert.Equal(5m, none.ShippingTotal);
        Assert.Contains("Spend at least 20", none.PromotionProblem);
    }

    [Fact]
    public async Task Limits_are_per_customer_and_overall_and_a_cancelled_order_gives_its_use_back()
    {
        var p = await Product(10m);
        var code = "ONCE" + Tag();
        var rule = await Rule(r => { r.Code = code; r.Kind = DiscountKind.AmountOff; r.Value = 2m; r.ProductIds = [p.Id]; r.MaxPerCustomer = 1; r.MaxRedemptions = 2; });
        var alice = await Read<CustomerDto>(await _c.PostAsJsonAsync("/api/v1/customers", new CustomerInput { Email = $"a-{Tag()}@example.com" }));
        var bob = await Read<CustomerDto>(await _c.PostAsJsonAsync("/api/v1/customers", new CustomerInput { Email = $"b-{Tag()}@example.com" }));

        // A guest has nobody to count against, so a per-customer discount is not for guests.
        var guest = await Basket(null, (p, 1));
        await UseCode(guest.Id, code);
        Assert.Equal(0m, (await Quote(guest.Id)).DiscountTotal);

        var first = await Basket(alice.Id, (p, 1));
        await UseCode(first.Id, code);
        Assert.Equal(2m, (await Quote(first.Id)).DiscountTotal);
        var order = await Read<OrderDto>(await Place(first.Id, alice.Id));
        Assert.Equal(1, (await Read<DiscountRuleDto>(await _c.GetAsync($"/api/v1/discounts/{rule.Id}"))).Redeemed);

        var second = await Basket(alice.Id, (p, 1));
        await UseCode(second.Id, code);
        Assert.Equal(0m, (await Quote(second.Id)).DiscountTotal); // Alice has had her one

        var bobs = await Basket(bob.Id, (p, 1));
        await UseCode(bobs.Id, code);
        Assert.Equal(2m, (await Quote(bobs.Id)).DiscountTotal);
        Assert.True((await Place(bobs.Id, bob.Id)).IsSuccessStatusCode);
        Assert.Equal(2, (await Read<DiscountRuleDto>(await _c.GetAsync($"/api/v1/discounts/{rule.Id}"))).Redeemed);

        var carol = await Read<CustomerDto>(await _c.PostAsJsonAsync("/api/v1/customers", new CustomerInput { Email = $"c-{Tag()}@example.com" }));
        var carols = await Basket(carol.Id, (p, 1));
        await UseCode(carols.Id, code);
        var used = await Quote(carols.Id);
        Assert.Equal(0m, used.DiscountTotal);
        Assert.Contains("used up", used.PromotionProblem);

        // Cancelling Alice's order gives the use back, to the discount and to Alice.
        Assert.True((await _c.PostAsJsonAsync($"/api/v1/orders/{order.Id}/stage", new OrderStageInput { Stage = OrderStage.Cancelled }, Json)).IsSuccessStatusCode);
        Assert.Equal(1, (await Read<DiscountRuleDto>(await _c.GetAsync($"/api/v1/discounts/{rule.Id}"))).Redeemed);
        Assert.Equal(2m, (await Quote(second.Id)).DiscountTotal);
    }

    [Fact]
    public async Task A_group_discount_is_for_its_members_and_dates_are_honoured()
    {
        var p = await Product(10m);
        var group = await Read<CustomerGroupDto>(await _c.PostAsJsonAsync("/api/v1/customer-groups", new CustomerGroupInput { Name = "VIP " + Tag() }));
        var member = await Read<CustomerDto>(await _c.PostAsJsonAsync("/api/v1/customers", new CustomerInput { Email = $"v-{Tag()}@example.com" }));
        await _c.PutAsync($"/api/v1/customer-groups/{group.Id}/members/{member.Id}", null);
        await Rule(r => { r.Kind = DiscountKind.PercentOff; r.Value = 50m; r.ProductIds = [p.Id]; r.CustomerGroupId = group.Id; });
        Assert.Equal(5m, (await Quote((await Basket(member.Id, (p, 1))).Id)).DiscountTotal);
        Assert.Equal(0m, (await Quote((await Basket(null, (p, 1))).Id)).DiscountTotal);

        var q = await Product(10m);
        await Rule(r => { r.Kind = DiscountKind.PercentOff; r.Value = 50m; r.ProductIds = [q.Id]; r.StartsUtc = DateTimeOffset.UtcNow.AddDays(1); });
        await Rule(r => { r.Kind = DiscountKind.PercentOff; r.Value = 50m; r.ProductIds = [q.Id]; r.EndsUtc = DateTimeOffset.UtcNow.AddDays(-1); });
        await Rule(r => { r.Kind = DiscountKind.PercentOff; r.Value = 50m; r.ProductIds = [q.Id]; r.Active = false; });
        Assert.Equal(0m, (await Quote((await Basket(null, (q, 1))).Id)).DiscountTotal);
    }

    [Fact]
    public async Task Bad_rules_are_refused_and_only_administrators_change_them()
    {
        var p = await Product(10m);
        async Task<HttpStatusCode> Try(Action<DiscountRuleInput> set)
        {
            var i = new DiscountRuleInput { Name = "Bad " + Tag(), Kind = DiscountKind.PercentOff, Value = 10m, ProductIds = [p.Id] };
            set(i);
            var r = await _c.PostAsJsonAsync("/api/v1/discounts", i, Json);
            if (r.IsSuccessStatusCode) _rules.Add((await Read<DiscountRuleDto>(r)).Id);
            return r.StatusCode;
        }
        Assert.Equal(HttpStatusCode.BadRequest, await Try(i => i.Value = 101m));
        Assert.Equal(HttpStatusCode.BadRequest, await Try(i => { i.Kind = DiscountKind.AmountOff; i.Value = 0m; }));
        Assert.Equal(HttpStatusCode.BadRequest, await Try(i => i.Kind = DiscountKind.BuyXGetY));                             // no quantities
        Assert.Equal(HttpStatusCode.BadRequest, await Try(i => { i.BuyQuantity = 1; i.GetQuantity = 1; }));                  // quantities on a percentage
        Assert.Equal(HttpStatusCode.BadRequest, await Try(i => i.ProductIds = []));                                          // applies to nothing
        Assert.Equal(HttpStatusCode.BadRequest, await Try(i => i.AllProducts = true));                                       // everything and named products
        Assert.Equal(HttpStatusCode.BadRequest, await Try(i => i.ProductIds = [Guid.NewGuid()]));
        Assert.Equal(HttpStatusCode.BadRequest, await Try(i => i.CategoryIds = [Guid.NewGuid()]));
        Assert.Equal(HttpStatusCode.BadRequest, await Try(i => i.CustomerGroupId = Guid.NewGuid()));
        Assert.Equal(HttpStatusCode.BadRequest, await Try(i => { i.StartsUtc = DateTimeOffset.UtcNow; i.EndsUtc = DateTimeOffset.UtcNow.AddDays(-1); }));
        Assert.Equal(HttpStatusCode.BadRequest, await Try(i => i.Code = "x!"));

        var code = "DUP" + Tag();
        Assert.Equal(HttpStatusCode.Created, await Try(i => i.Code = code));
        Assert.Equal(HttpStatusCode.BadRequest, await Try(i => i.Code = code.ToLowerInvariant()));                          // another discount's code
        var promo = await Read<PromotionDto>(await _c.PostAsJsonAsync("/api/v1/promotions", new PromotionInput { Code = "PR" + Tag(), Kind = PromotionKind.Percent, Value = 5m }, Json));
        try { Assert.Equal(HttpStatusCode.BadRequest, await Try(i => i.Code = promo.Code)); }                                // a promo code
        finally { await _c.DeleteAsync($"/api/v1/promotions/{promo.Id}"); }

        // Replace: targets change, the uses stay.
        var rule = await Rule(r => { r.Kind = DiscountKind.PercentOff; r.Value = 10m; r.ProductIds = [p.Id]; });
        var other = await Product(10m);
        var put = await _c.PutAsJsonAsync($"/api/v1/discounts/{rule.Id}", new DiscountRuleInput { Name = "Renamed", Kind = DiscountKind.AmountOff, Value = 2m, ProductIds = [other.Id] }, Json);
        var updated = await Read<DiscountRuleDto>(put);
        Assert.Equal(("Renamed", DiscountKind.AmountOff, other.Id), (updated.Name, updated.Kind, Assert.Single(updated.Products).Id));
        Assert.Equal(HttpStatusCode.NotFound, (await _c.PutAsJsonAsync($"/api/v1/discounts/{Guid.NewGuid()}", new DiscountRuleInput { Name = "x", Value = 1m, AllProducts = true }, Json)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await _c.GetAsync($"/api/v1/discounts/{Guid.NewGuid()}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await _c.DeleteAsync($"/api/v1/discounts/{Guid.NewGuid()}")).StatusCode);

        var (email, password) = await factory.CreateReadOnlyUserAsync();
        var reader = await factory.CreateAuthenticatedApiClientAsync(email, password);
        Assert.Equal(HttpStatusCode.OK, (await reader.GetAsync("/api/v1/discounts")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await reader.PostAsJsonAsync("/api/v1/discounts", new DiscountRuleInput { Name = "x", Value = 1m, AllProducts = true }, Json)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await reader.DeleteAsync($"/api/v1/discounts/{rule.Id}")).StatusCode);
    }
}
