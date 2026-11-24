using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using PiranhaCommerce.Catalog;
using PiranhaCommerce.Customers;
using PiranhaCommerce.Ecommerce;

namespace PiranhaCommerce.IntegrationTests;

/// <summary>Promo codes. Tax and shipping settings are store-wide, so these run one at a time and put everything back.</summary>
[Collection(OrderSettings.Name)]
public class PromotionTests(PiranhaCommerceFactory factory) : IClassFixture<PiranhaCommerceFactory>
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };

    private static string Tag() => Guid.NewGuid().ToString("N")[..8];
    private static async Task<T> Read<T>(HttpResponseMessage r) => (await r.Content.ReadFromJsonAsync<T>(Json))!;

    private sealed class Scene(HttpClient staff)
    {
        public HttpClient Staff { get; } = staff;
        private readonly List<Guid> _promotions = [], _methods = [], _classes = [];

        public async Task<PromotionDto> Promo(PromotionKind kind, decimal value, Action<PromotionInput>? tweak = null)
        {
            var input = new PromotionInput { Code = "T" + Tag(), Kind = kind, Value = value };
            tweak?.Invoke(input);
            var r = await Staff.PostAsJsonAsync("/api/v1/promotions", input, Json);
            Assert.Equal(HttpStatusCode.Created, r.StatusCode);
            var p = await Read<PromotionDto>(r);
            _promotions.Add(p.Id);
            return p;
        }

        public async Task<TaxClassDto> Class()
        {
            var c = await Read<TaxClassDto>(await Staff.PostAsJsonAsync("/api/v1/tax-classes", new TaxClassInput { Name = "C" + Tag() }));
            _classes.Add(c.Id);
            return c;
        }

        public async Task Rule(Guid? classId, string country, decimal rate)
            => Assert.Equal(HttpStatusCode.Created, (await Staff.PostAsJsonAsync("/api/v1/tax-rules",
                new TaxRuleInput { TaxClassId = classId, CountryCode = country, Name = $"VAT {rate}%", Rate = rate })).StatusCode);

        public async Task<ProductDto> Product(decimal price, Guid? classId = null, int stock = 20)
        {
            var p = await Read<ProductDto>(await Staff.PostAsJsonAsync("/api/v1/products", new ProductInput { Name = "Promo " + Tag(), Sku = "PR-" + Tag(), Price = price }));
            Assert.Equal(HttpStatusCode.OK, (await Staff.PutAsJsonAsync($"/api/v1/products/{p.Id}/listing", new ProductListingInput { Published = true, TaxClassId = classId })).StatusCode);
            Assert.Equal(HttpStatusCode.OK, (await Staff.PostAsJsonAsync($"/api/v1/products/{p.Id}/stock/adjustments", new StockAdjustmentInput { Delta = stock, Reason = "Delivery" })).StatusCode);
            return p;
        }

        public async Task<Guid> Shipping(decimal price, decimal? freeOver = null)
        {
            var m = await Read<ShippingMethodDto>(await Staff.PostAsJsonAsync("/api/v1/shipping-methods", new ShippingMethodInput { Name = "Post " + Tag(), Price = price, FreeOver = freeOver }));
            _methods.Add(m.Id);
            return m.Id;
        }

        public async Task<BasketDto> Basket(params (ProductDto Product, int Quantity)[] lines)
        {
            var basket = await Read<BasketDto>(await Staff.PostAsJsonAsync("/api/v1/baskets", new BasketCreateInput()));
            foreach (var (p, q) in lines)
                Assert.Equal(HttpStatusCode.OK, (await Staff.PutAsJsonAsync($"/api/v1/baskets/{basket.Id}/lines/{p.Id}", new BasketLineInput { Quantity = q })).StatusCode);
            return basket;
        }

        public Task<HttpResponseMessage> Apply(Guid basketId, string code) => Staff.PostAsJsonAsync($"/api/v1/baskets/{basketId}/promo", new CodeInput { Code = code });
        public Task<HttpResponseMessage> Place(Guid basketId, Guid? method) => Staff.PostAsJsonAsync($"/api/v1/baskets/{basketId}/checkout", Checkout(method));
        public async Task<QuoteDto> Quote(Guid basketId, Guid? method) =>
            await Read<QuoteDto>(await Staff.PostAsJsonAsync($"/api/v1/baskets/{basketId}/quote", new QuoteInput { CountryCode = "GB", ShippingMethodId = method }));

        public async Task CleanAsync()
        {
            foreach (var r in await Staff.GetFromJsonAsync<List<TaxRuleDto>>("/api/v1/tax-rules") ?? []) await Staff.DeleteAsync($"/api/v1/tax-rules/{r.Id}");
            foreach (var id in _promotions) await Staff.DeleteAsync($"/api/v1/promotions/{id}");
            foreach (var id in _methods) await Staff.DeleteAsync($"/api/v1/shipping-methods/{id}");
            // products keep their class until unpublished/reset by the next test; delete classes best-effort
            foreach (var id in _classes) await Staff.DeleteAsync($"/api/v1/tax-classes/{id}");
        }
    }

    private static CheckoutInput Checkout(Guid? method) => new()
    {
        Email = $"buyer-{Tag()}@example.com",
        ShippingMethodId = method,
        ShippingAddress = new AddressInput { Recipient = "Ada", Line1 = "1 High St", City = "Leeds", PostalCode = "LS1", CountryCode = "GB" },
    };

    private static void AssertAddsUp(OrderDto o)
    {
        Assert.Equal(o.Subtotal - o.DiscountTotal + o.ShippingTotal + o.TaxTotal, o.Total);
        Assert.Equal(o.DiscountTotal, o.Lines.Sum(l => l.DiscountAmount));
        Assert.All(o.Lines, l => Assert.InRange(l.DiscountAmount, 0m, l.LineTotal));
    }

    [Fact]
    public async Task A_percentage_comes_off_the_goods_first_then_tax_is_charged_on_what_remains_and_quote_matches_checkout()
    {
        var scene = new Scene(await factory.CreateAuthenticatedApiClientAsync());
        try
        {
            var reduced = await scene.Class();
            await scene.Rule(null, "GB", 20m);
            await scene.Rule(reduced.Id, "GB", 5m);
            var standardGoods = await scene.Product(10m);
            var reducedGoods = await scene.Product(20m, reduced.Id);
            var method = await scene.Shipping(5m);
            var promo = await scene.Promo(PromotionKind.Percent, 10m);
            var basket = await scene.Basket((standardGoods, 1), (reducedGoods, 1));
            Assert.Equal(HttpStatusCode.OK, (await scene.Apply(basket.Id, promo.Code.ToLowerInvariant())).StatusCode);

            // 10% of 30.00 = 3.00 (1.00 and 2.00 on the lines). Tax: 9.00*20% + 18.00*5% + postage 5.00*20% = 1.80 + 0.90 + 1.00
            var quote = await scene.Quote(basket.Id, method);
            Assert.Equal((30m, 3m, 5m, 3.7m, 35.7m), (quote.Subtotal, quote.DiscountTotal, quote.ShippingTotal, quote.TaxTotal, quote.Total));
            Assert.Equal(promo.Code, quote.PromotionCode);
            Assert.Null(quote.PromotionProblem);

            var order = await Read<OrderDto>(await scene.Place(basket.Id, method));
            Assert.Equal((quote.Subtotal, quote.DiscountTotal, quote.ShippingTotal, quote.TaxTotal, quote.Total),
                (order.Subtotal, order.DiscountTotal, order.ShippingTotal, order.TaxTotal, order.Total));
            Assert.Equal(promo.Code, order.PromotionCode);
            AssertAddsUp(order);
            Assert.Equal([1m, 2m], order.Lines.Select(l => l.DiscountAmount).Order());
            Assert.Equal(1, (await Read<PromotionDto>(await scene.Staff.GetAsync($"/api/v1/promotions/{promo.Id}"))).Redeemed);
        }
        finally { await scene.CleanAsync(); }
    }

    [Fact]
    public async Task A_fixed_amount_never_takes_more_than_the_goods_and_never_touches_postage()
    {
        var scene = new Scene(await factory.CreateAuthenticatedApiClientAsync());
        try
        {
            var product = await scene.Product(12m);
            var method = await scene.Shipping(4m);
            var promo = await scene.Promo(PromotionKind.Amount, 50m);
            var basket = await scene.Basket((product, 1));
            await scene.Apply(basket.Id, promo.Code);

            var order = await Read<OrderDto>(await scene.Place(basket.Id, method));
            Assert.Equal(12m, order.DiscountTotal);
            Assert.Equal(4m, order.ShippingTotal);
            Assert.Equal(4m, order.Total);
            AssertAddsUp(order);
        }
        finally { await scene.CleanAsync(); }
    }

    [Fact]
    public async Task Free_postage_over_a_spend_is_judged_on_the_goods_after_the_discount()
    {
        var scene = new Scene(await factory.CreateAuthenticatedApiClientAsync());
        try
        {
            var product = await scene.Product(60m);
            var method = await scene.Shipping(4.99m, freeOver: 50m);
            var promo = await scene.Promo(PromotionKind.Amount, 20m);
            var basket = await scene.Basket((product, 1));

            Assert.Equal(0m, (await scene.Quote(basket.Id, method)).ShippingTotal);       // 60 >= 50: free
            await scene.Apply(basket.Id, promo.Code);
            var discounted = await scene.Quote(basket.Id, method);                         // 40 < 50: postage charged
            Assert.Equal((20m, 4.99m, 44.99m), (discounted.DiscountTotal, discounted.ShippingTotal, discounted.Total));
        }
        finally { await scene.CleanAsync(); }
    }

    [Fact]
    public async Task The_shares_of_an_awkward_discount_add_up_exactly_and_no_line_goes_negative()
    {
        var scene = new Scene(await factory.CreateAuthenticatedApiClientAsync());
        try
        {
            var a = await scene.Product(0.33m);
            var b = await scene.Product(0.33m);
            var c = await scene.Product(0.33m);
            var promo = await scene.Promo(PromotionKind.Amount, 0.10m);
            var basket = await scene.Basket((a, 1), (b, 1), (c, 1));
            await scene.Apply(basket.Id, promo.Code);

            var order = await Read<OrderDto>(await scene.Place(basket.Id, null));
            Assert.Equal(0.10m, order.DiscountTotal);
            AssertAddsUp(order);
        }
        finally { await scene.CleanAsync(); }
    }

    [Fact]
    public async Task A_code_that_does_not_work_is_refused_with_one_answer_and_one_that_stops_working_later_blocks_checkout_not_silently_charges_full_price()
    {
        var scene = new Scene(await factory.CreateAuthenticatedApiClientAsync());
        try
        {
            var product = await scene.Product(10m);
            var basket = await scene.Basket((product, 1));

            var off = await scene.Promo(PromotionKind.Percent, 10m, p => p.Active = false);
            var future = await scene.Promo(PromotionKind.Percent, 10m, p => p.StartsUtc = DateTimeOffset.UtcNow.AddDays(1));
            var ended = await scene.Promo(PromotionKind.Percent, 10m, p => { p.StartsUtc = DateTimeOffset.UtcNow.AddDays(-2); p.EndsUtc = DateTimeOffset.UtcNow.AddDays(-1); });
            var answers = new List<string>();
            foreach (var code in new[] { "NOSUCHCODE", off.Code, future.Code, ended.Code })
            {
                var r = await scene.Apply(basket.Id, code);
                Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);
                answers.Add(await r.Content.ReadAsStringAsync());
            }
            Assert.Single(answers.Distinct());

            // A minimum spend is judged at quote and checkout, because the basket may still grow.
            var big = await scene.Promo(PromotionKind.Percent, 10m, p => p.MinSubtotal = 100m);
            Assert.Equal(HttpStatusCode.OK, (await scene.Apply(basket.Id, big.Code)).StatusCode);
            var quote = await scene.Quote(basket.Id, null);
            Assert.NotNull(quote.PromotionProblem);
            Assert.Equal(0m, quote.DiscountTotal);
            Assert.Equal(10m, quote.Total);
            Assert.Equal(HttpStatusCode.Conflict, (await scene.Place(basket.Id, null)).StatusCode);

            // Removing the code lets the order through.
            Assert.Equal(HttpStatusCode.OK, (await scene.Staff.DeleteAsync($"/api/v1/baskets/{basket.Id}/promo")).StatusCode);
            Assert.Equal(HttpStatusCode.OK, (await scene.Place(basket.Id, null)).StatusCode);

            // A checked-out basket is read-only for codes too.
            Assert.Equal(HttpStatusCode.Conflict, (await scene.Apply(basket.Id, big.Code)).StatusCode);
        }
        finally { await scene.CleanAsync(); }
    }

    [Fact]
    public async Task A_code_limited_to_one_use_gives_exactly_one_discounted_order_under_simultaneous_checkouts()
    {
        var scene = new Scene(await factory.CreateAuthenticatedApiClientAsync());
        try
        {
            var product = await scene.Product(10m);
            var promo = await scene.Promo(PromotionKind.Percent, 50m, p => p.MaxRedemptions = 1);
            var baskets = new List<BasketDto>();
            for (var i = 0; i < 4; i++)
            {
                var basket = await scene.Basket((product, 1));
                Assert.Equal(HttpStatusCode.OK, (await scene.Apply(basket.Id, promo.Code)).StatusCode); // every shopper can still enter it
                baskets.Add(basket);
            }

            var results = await Task.WhenAll(baskets.Select(b => scene.Place(b.Id, null)));
            Assert.Equal(1, results.Count(r => r.StatusCode == HttpStatusCode.OK));
            Assert.Equal(3, results.Count(r => r.StatusCode == HttpStatusCode.Conflict));
            var won = results.Single(r => r.StatusCode == HttpStatusCode.OK);
            Assert.Equal(5m, (await Read<OrderDto>(won)).DiscountTotal);
            Assert.Equal(1, (await Read<PromotionDto>(await scene.Staff.GetAsync($"/api/v1/promotions/{promo.Id}"))).Redeemed);
        }
        finally { await scene.CleanAsync(); }
    }

    [Fact]
    public async Task Cancelling_an_order_gives_its_use_of_the_code_back_once()
    {
        var scene = new Scene(await factory.CreateAuthenticatedApiClientAsync());
        try
        {
            var product = await scene.Product(10m);
            var promo = await scene.Promo(PromotionKind.Percent, 10m, p => p.MaxRedemptions = 1);
            var first = await scene.Basket((product, 1));
            await scene.Apply(first.Id, promo.Code);
            var order = await Read<OrderDto>(await scene.Place(first.Id, null));

            var second = await scene.Basket((product, 1));
            Assert.Equal(HttpStatusCode.BadRequest, (await scene.Apply(second.Id, promo.Code)).StatusCode); // used up

            var cancel = new OrderStageInput { Stage = OrderStage.Cancelled };
            Assert.Equal(HttpStatusCode.OK, (await scene.Staff.PostAsJsonAsync($"/api/v1/orders/{order.Id}/stage", cancel, Json)).StatusCode);
            Assert.Equal(HttpStatusCode.Conflict, (await scene.Staff.PostAsJsonAsync($"/api/v1/orders/{order.Id}/stage", cancel, Json)).StatusCode); // cancelling again does not give another back
            Assert.Equal(0, (await Read<PromotionDto>(await scene.Staff.GetAsync($"/api/v1/promotions/{promo.Id}"))).Redeemed);

            Assert.Equal(HttpStatusCode.OK, (await scene.Apply(second.Id, promo.Code)).StatusCode);
            Assert.Equal(HttpStatusCode.OK, (await scene.Place(second.Id, null)).StatusCode);
        }
        finally { await scene.CleanAsync(); }
    }

    [Fact]
    public async Task Promotions_are_validated_unique_and_need_the_write_policy()
    {
        var scene = new Scene(await factory.CreateAuthenticatedApiClientAsync());
        try
        {
            var promo = await scene.Promo(PromotionKind.Percent, 10m);
            var again = await scene.Staff.PostAsJsonAsync("/api/v1/promotions", new PromotionInput { Code = promo.Code.ToLowerInvariant(), Kind = PromotionKind.Amount, Value = 1m }, Json);
            Assert.Equal(HttpStatusCode.BadRequest, again.StatusCode);
            Assert.Equal(HttpStatusCode.BadRequest, (await scene.Staff.PostAsJsonAsync("/api/v1/promotions", new PromotionInput { Code = "OVER" + Tag(), Kind = PromotionKind.Percent, Value = 101m }, Json)).StatusCode);
            Assert.Equal(HttpStatusCode.BadRequest, (await scene.Staff.PostAsJsonAsync("/api/v1/promotions", new PromotionInput { Code = "a b", Kind = PromotionKind.Percent, Value = 5m }, Json)).StatusCode);
            Assert.Equal(HttpStatusCode.BadRequest, (await scene.Staff.PutAsJsonAsync($"/api/v1/promotions/{promo.Id}", new PromotionInput { Code = "RENAMED" + Tag(), Kind = PromotionKind.Percent, Value = 10m }, Json)).StatusCode);

            var (email, password) = await factory.CreateReadOnlyUserAsync();
            var reader = await factory.CreateAuthenticatedApiClientAsync(email, password);
            Assert.Equal(HttpStatusCode.OK, (await reader.GetAsync("/api/v1/promotions")).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await reader.PostAsJsonAsync("/api/v1/promotions", new PromotionInput { Code = "NOPE" + Tag(), Kind = PromotionKind.Percent, Value = 5m }, Json)).StatusCode);
        }
        finally { await scene.CleanAsync(); }
    }
}
