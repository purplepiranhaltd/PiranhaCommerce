using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using PiranhaCommerce.Catalog;
using PiranhaCommerce.Customers;
using PiranhaCommerce.Ecommerce;

namespace PiranhaCommerce.IntegrationTests;

/// <summary>Tax rates per product class. Tax settings are store-wide, so these run one at a time and put everything back.</summary>
[Collection(OrderSettings.Name)]
public class TaxClassTests(PiranhaFactory factory) : IClassFixture<PiranhaFactory>
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };

    private static string Tag() => Guid.NewGuid().ToString("N")[..8];
    private static async Task<T> Read<T>(HttpResponseMessage r) => (await r.Content.ReadFromJsonAsync<T>(Json))!;

    private sealed class Scene(HttpClient staff)
    {
        public HttpClient Staff { get; } = staff;
        public List<Guid> Products { get; } = [];
        public List<Guid> Classes { get; } = [];
        public List<Guid> Methods { get; } = [];

        public async Task<TaxClassDto> NewClass(string? name = null)
        {
            var c = await Read<TaxClassDto>(await Staff.PostAsJsonAsync("/api/v1/tax-classes", new TaxClassInput { Name = name ?? "Class " + Tag() }));
            Classes.Add(c.Id);
            return c;
        }

        public async Task<TaxRuleDto> NewRule(Guid? classId, string country, decimal rate, string? name = null)
        {
            var r = await Staff.PostAsJsonAsync("/api/v1/tax-rules", new TaxRuleInput { TaxClassId = classId, CountryCode = country, Name = name ?? $"VAT {rate}% " + Tag(), Rate = rate });
            Assert.Equal(HttpStatusCode.Created, r.StatusCode);
            return await Read<TaxRuleDto>(r);
        }

        public async Task<ProductDto> NewProduct(decimal price, Guid? classId)
        {
            var p = await Read<ProductDto>(await Staff.PostAsJsonAsync("/api/v1/products", new ProductInput { Name = "Taxed " + Tag(), Sku = "TX-" + Tag(), Price = price }));
            Products.Add(p.Id);
            Assert.Equal(HttpStatusCode.OK, (await Staff.PutAsJsonAsync($"/api/v1/products/{p.Id}/listing", new ProductListingInput { Published = true, TaxClassId = classId })).StatusCode);
            Assert.Equal(HttpStatusCode.OK, (await Staff.PostAsJsonAsync($"/api/v1/products/{p.Id}/stock/adjustments", new StockAdjustmentInput { Delta = 20, Reason = "Delivery" })).StatusCode);
            return p;
        }

        public async Task<Guid> NewShipping(decimal price)
        {
            var m = await Read<ShippingMethodDto>(await Staff.PostAsJsonAsync("/api/v1/shipping-methods", new ShippingMethodInput { Name = "Post " + Tag(), Price = price }));
            Methods.Add(m.Id);
            return m.Id;
        }

        public async Task<BasketDto> Basket(params (ProductDto Product, int Quantity)[] lines)
        {
            var basket = await Read<BasketDto>(await Staff.PostAsJsonAsync("/api/v1/baskets", new BasketCreateInput()));
            foreach (var (p, q) in lines)
                Assert.Equal(HttpStatusCode.OK, (await Staff.PutAsJsonAsync($"/api/v1/baskets/{basket.Id}/lines/{p.Id}", new BasketLineInput { Quantity = q })).StatusCode);
            return basket;
        }

        /// <summary>Leaves tax and shipping settings as it found them: no rules, no methods, and only the Standard class.</summary>
        public async Task CleanAsync()
        {
            foreach (var id in Products)
                await Staff.PutAsJsonAsync($"/api/v1/products/{id}/listing", new ProductListingInput { Published = false });
            foreach (var r in await Staff.GetFromJsonAsync<List<TaxRuleDto>>("/api/v1/tax-rules") ?? [])
                await Staff.DeleteAsync($"/api/v1/tax-rules/{r.Id}");
            foreach (var id in Methods) await Staff.DeleteAsync($"/api/v1/shipping-methods/{id}");
            foreach (var id in Classes) await Staff.DeleteAsync($"/api/v1/tax-classes/{id}");
        }
    }

    private static CheckoutInput Checkout(Guid? method, string country = "GB") => new()
    {
        Email = $"buyer-{Tag()}@example.com",
        ShippingMethodId = method,
        ShippingAddress = new AddressInput { Recipient = "Ada", Line1 = "1 High St", City = "Leeds", PostalCode = "LS1", CountryCode = country },
    };

    [Fact]
    public async Task One_basket_can_carry_the_standard_reduced_and_zero_rates_and_shipping_is_taxed_at_the_standard_rate()
    {
        var scene = new Scene(await factory.CreateAuthenticatedApiClientAsync());
        try
        {
            var reduced = await scene.NewClass("Reduced " + Tag());
            var zero = await scene.NewClass("Zero " + Tag());
            await scene.NewRule(null, "GB", 20m, "VAT 20%");           // no class given: the default class
            await scene.NewRule(reduced.Id, "GB", 5m, "VAT 5%");
            await scene.NewRule(zero.Id, "GB", 0m, "Zero rated");

            var a = await scene.NewProduct(10m, null);                  // no class: default (standard)
            var b = await scene.NewProduct(10m, reduced.Id);
            var c = await scene.NewProduct(10m, zero.Id);
            var method = await scene.NewShipping(5m);
            var basket = await scene.Basket((a, 1), (b, 1), (c, 1));

            // 2.00 + 0.50 + 0.00 on the goods, 1.00 on the 5.00 postage at the standard rate
            var quote = await Read<QuoteDto>(await scene.Staff.PostAsJsonAsync($"/api/v1/baskets/{basket.Id}/quote", new QuoteInput { CountryCode = "GB", ShippingMethodId = method }));
            Assert.Equal((30m, 5m, 3.5m, 38.5m), (quote.Subtotal, quote.ShippingTotal, quote.TaxTotal, quote.Total));
            Assert.Equal(["VAT 20%:20:3.00", "VAT 5%:5:0.50", "Zero rated:0:0.00"],
                quote.Taxes.Select(t => $"{t.Name}:{t.Rate:0.##}:{t.Amount:0.00}"));
            Assert.Equal("Tax", quote.TaxName); // mixed rates

            var order = await Read<OrderDto>(await scene.Staff.PostAsJsonAsync($"/api/v1/baskets/{basket.Id}/checkout", Checkout(method)));
            Assert.Equal((quote.Subtotal, quote.ShippingTotal, quote.TaxTotal, quote.Total), (order.Subtotal, order.ShippingTotal, order.TaxTotal, order.Total));
            Assert.Equal(1m, order.ShippingTax);
            Assert.Equal(quote.Taxes.Select(t => (t.Name, t.Rate, t.Amount)), order.Taxes!.Select(t => (t.Name, t.Rate, t.Amount)));
            Assert.Equal([2m, 0.5m, 0m], order.Lines.OrderBy(l => l.UnitPrice).ThenBy(l => l.TaxRate == 20 ? 2 : l.TaxRate == 5 ? 1 : 0).Select(l => l.TaxAmount).OrderDescending());

            // The order keeps its own copy: changing the rates afterwards changes nothing already sold.
            foreach (var r in await scene.Staff.GetFromJsonAsync<List<TaxRuleDto>>("/api/v1/tax-rules") ?? [])
                await scene.Staff.PutAsJsonAsync($"/api/v1/tax-rules/{r.Id}", new TaxRuleInput { TaxClassId = r.TaxClassId, CountryCode = r.CountryCode, Name = "Changed", Rate = 99m });
            var again = await Read<OrderDto>(await scene.Staff.GetAsync($"/api/v1/orders/{order.Id}"));
            Assert.Equal(order.TaxTotal, again.TaxTotal);
            Assert.DoesNotContain(again.Taxes!, t => t.Name == "Changed");
        }
        finally { await scene.CleanAsync(); }
    }

    [Fact]
    public async Task A_class_with_no_rule_for_the_country_is_untaxed_there_and_its_own_fallback_applies()
    {
        var scene = new Scene(await factory.CreateAuthenticatedApiClientAsync());
        try
        {
            var reduced = await scene.NewClass();
            await scene.NewRule(null, "DE", 19m);
            await scene.NewRule(null, "*", 10m);                 // standard goods elsewhere
            await scene.NewRule(reduced.Id, "GB", 5m);            // reduced goods only have a GB rule
            var std = await scene.NewProduct(100m, null);
            var red = await scene.NewProduct(100m, reduced.Id);
            var method = await scene.NewShipping(0m);

            async Task<QuoteDto> Quote(string country, ProductDto p) =>
                await Read<QuoteDto>(await scene.Staff.PostAsJsonAsync($"/api/v1/baskets/{(await scene.Basket((p, 1))).Id}/quote", new QuoteInput { CountryCode = country, ShippingMethodId = method }));

            Assert.Equal(19m, (await Quote("DE", std)).TaxTotal);   // its own country rule
            Assert.Equal(10m, (await Quote("FR", std)).TaxTotal);   // the class's fallback
            Assert.Equal(5m, (await Quote("GB", red)).TaxTotal);
            Assert.Equal(0m, (await Quote("DE", red)).TaxTotal);    // reduced has no DE rule and no fallback: untaxed
        }
        finally { await scene.CleanAsync(); }
    }

    [Fact]
    public async Task Each_line_is_rounded_to_the_penny_on_its_own_like_an_invoice()
    {
        var scene = new Scene(await factory.CreateAuthenticatedApiClientAsync());
        try
        {
            await scene.NewRule(null, "GB", 20m);
            var p = await scene.NewProduct(0.33m, null);
            var q = await scene.NewProduct(0.33m, null);
            var r = await scene.NewProduct(0.33m, null);
            var method = await scene.NewShipping(0m);
            var basket = await scene.Basket((p, 1), (q, 1), (r, 1));

            var quote = await Read<QuoteDto>(await scene.Staff.PostAsJsonAsync($"/api/v1/baskets/{basket.Id}/quote", new QuoteInput { CountryCode = "GB", ShippingMethodId = method }));
            Assert.Equal(0.21m, quote.TaxTotal); // 3 x 0.066 -> 3 x 0.07, not 0.198 -> 0.20
            Assert.Equal(1.2m, quote.Total);
        }
        finally { await scene.CleanAsync(); }
    }

    [Fact]
    public async Task Rules_are_unique_per_class_and_country_and_must_name_a_real_class()
    {
        var scene = new Scene(await factory.CreateAuthenticatedApiClientAsync());
        try
        {
            var other = await scene.NewClass();
            await scene.NewRule(null, "GB", 20m);
            await scene.NewRule(other.Id, "GB", 5m); // same country, different class: fine

            var dup = await scene.Staff.PostAsJsonAsync("/api/v1/tax-rules", new TaxRuleInput { CountryCode = "gb", Name = "Again", Rate = 1m });
            Assert.Equal(HttpStatusCode.BadRequest, dup.StatusCode);
            var dupOther = await scene.Staff.PostAsJsonAsync("/api/v1/tax-rules", new TaxRuleInput { TaxClassId = other.Id, CountryCode = "GB", Name = "Again", Rate = 1m });
            Assert.Equal(HttpStatusCode.BadRequest, dupOther.StatusCode);
            var ghost = await scene.Staff.PostAsJsonAsync("/api/v1/tax-rules", new TaxRuleInput { TaxClassId = Guid.NewGuid(), CountryCode = "FR", Name = "Ghost", Rate = 1m });
            Assert.Equal(HttpStatusCode.BadRequest, ghost.StatusCode);
        }
        finally { await scene.CleanAsync(); }
    }

    [Fact]
    public async Task Classes_have_unique_names_one_default_and_cannot_be_deleted_while_in_use()
    {
        var scene = new Scene(await factory.CreateAuthenticatedApiClientAsync());
        var originalDefault = (await scene.Staff.GetFromJsonAsync<List<TaxClassDto>>("/api/v1/tax-classes"))!.Single(c => c.IsDefault);
        try
        {
            var a = await scene.NewClass("Alpha " + Tag());
            Assert.Equal(HttpStatusCode.BadRequest, (await scene.Staff.PostAsJsonAsync("/api/v1/tax-classes", new TaxClassInput { Name = a.Name })).StatusCode);

            // The default cannot be turned off or deleted; making another class the default moves it.
            Assert.Equal(HttpStatusCode.Conflict, (await scene.Staff.PutAsJsonAsync($"/api/v1/tax-classes/{originalDefault.Id}", new TaxClassInput { Name = originalDefault.Name, IsDefault = false })).StatusCode);
            Assert.Equal(HttpStatusCode.Conflict, (await scene.Staff.DeleteAsync($"/api/v1/tax-classes/{originalDefault.Id}")).StatusCode);
            Assert.Equal(HttpStatusCode.OK, (await scene.Staff.PutAsJsonAsync($"/api/v1/tax-classes/{a.Id}", new TaxClassInput { Name = a.Name, IsDefault = true })).StatusCode);
            var defaults = (await scene.Staff.GetFromJsonAsync<List<TaxClassDto>>("/api/v1/tax-classes"))!.Where(c => c.IsDefault).ToList();
            Assert.Equal(a.Id, Assert.Single(defaults).Id);

            // A rule or a product holds a class in place.
            var b = await scene.NewClass();
            var rule = await scene.NewRule(b.Id, "GB", 5m);
            Assert.Equal(HttpStatusCode.Conflict, (await scene.Staff.DeleteAsync($"/api/v1/tax-classes/{b.Id}")).StatusCode);
            await scene.Staff.DeleteAsync($"/api/v1/tax-rules/{rule.Id}");
            var product = await scene.NewProduct(1m, b.Id);
            Assert.Equal(HttpStatusCode.Conflict, (await scene.Staff.DeleteAsync($"/api/v1/tax-classes/{b.Id}")).StatusCode);
            await scene.Staff.PutAsJsonAsync($"/api/v1/products/{product.Id}/listing", new ProductListingInput { Published = true }); // back to the default
            Assert.Equal(HttpStatusCode.NoContent, (await scene.Staff.DeleteAsync($"/api/v1/tax-classes/{b.Id}")).StatusCode);
        }
        finally
        {
            await scene.Staff.PutAsJsonAsync($"/api/v1/tax-classes/{originalDefault.Id}", new TaxClassInput { Name = originalDefault.Name, IsDefault = true });
            await scene.CleanAsync();
        }
    }

    [Fact]
    public async Task A_products_tax_class_is_read_back_and_a_bad_class_is_refused()
    {
        var scene = new Scene(await factory.CreateAuthenticatedApiClientAsync());
        try
        {
            var reduced = await scene.NewClass();
            var p = await scene.NewProduct(5m, reduced.Id);
            Assert.Equal(reduced.Id, (await Read<ProductListingDto>(await scene.Staff.GetAsync($"/api/v1/products/{p.Id}/listing"))).TaxClassId);

            var bad = await scene.Staff.PutAsJsonAsync($"/api/v1/products/{p.Id}/listing", new ProductListingInput { Published = true, TaxClassId = Guid.NewGuid() });
            Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);
            Assert.Equal(reduced.Id, (await Read<ProductListingDto>(await scene.Staff.GetAsync($"/api/v1/products/{p.Id}/listing"))).TaxClassId); // unchanged
        }
        finally { await scene.CleanAsync(); }
    }

    [Fact]
    public async Task Tax_classes_need_the_write_policy()
    {
        var (email, password) = await factory.CreateReadOnlyUserAsync();
        var reader = await factory.CreateAuthenticatedApiClientAsync(email, password);
        Assert.Equal(HttpStatusCode.OK, (await reader.GetAsync("/api/v1/tax-classes")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await reader.PostAsJsonAsync("/api/v1/tax-classes", new TaxClassInput { Name = "x" })).StatusCode);
    }
}
