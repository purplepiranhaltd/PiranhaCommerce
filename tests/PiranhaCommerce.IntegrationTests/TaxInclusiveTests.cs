using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using PiranhaCommerce.Catalog;
using PiranhaCommerce.Customers;
using PiranhaCommerce.Ecommerce;

namespace PiranhaCommerce.IntegrationTests;

/// <summary>Showing shoppers tax-inclusive prices: lookups by destination, quote lines with their tax, and order lines.</summary>
[Collection(OrderSettings.Name)]
public class TaxInclusiveTests(PiranhaFactory factory) : IClassFixture<PiranhaFactory>
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };
    private static string Tag() => Guid.NewGuid().ToString("N")[..8];
    private static async Task<T> Read<T>(HttpResponseMessage r) => (await r.Content.ReadFromJsonAsync<T>(Json))!;

    /// <summary>A product in its own tax class with a rate for one destination, so these tests cannot disturb anyone else's tax.</summary>
    private static async Task<ProductDto> TaxedProduct(HttpClient staff, decimal price, decimal? rate, string country)
    {
        var cls = await Read<TaxClassDto>(await staff.PostAsJsonAsync("/api/v1/tax-classes", new TaxClassInput { Name = "TI" + Tag() }));
        if (rate is { } r) Assert.Equal(HttpStatusCode.Created, (await staff.PostAsJsonAsync("/api/v1/tax-rules", new TaxRuleInput { TaxClassId = cls.Id, CountryCode = country, Name = $"VAT {r}", Rate = r })).StatusCode);
        var p = await Read<ProductDto>(await staff.PostAsJsonAsync("/api/v1/products", new ProductInput { Name = "TI " + Tag(), Sku = "TI-" + Tag(), Price = price }));
        await staff.PutAsJsonAsync($"/api/v1/products/{p.Id}/listing", new ProductListingInput { Published = true, TaxClassId = cls.Id });
        await staff.PostAsJsonAsync($"/api/v1/products/{p.Id}/stock/adjustments", new StockAdjustmentInput { Delta = 100, Reason = "Delivery" });
        return p;
    }

    private static async Task<ShopperPriceDto> Price(HttpClient c, ProductDto p, string? country, string route = "/api/v1/storefront/guest/prices") =>
        (await Read<List<ShopperPriceDto>>(await c.GetAsync($"{route}?productIds={p.Id}" + (country is null ? "" : "&countryCode=" + country)))).Single();

    [Fact]
    public async Task Prices_can_be_shown_with_tax_for_a_destination_using_the_rate_of_the_products_own_class()
    {
        var staff = await factory.CreateAuthenticatedApiClientAsync();
        var anon = factory.CreateClient();
        var standard = await TaxedProduct(staff, 10m, 20m, "QX");
        var zero = await TaxedProduct(staff, 10m, null, "QX"); // no rule for this class: not taxed there

        var s = await Price(anon, standard, "QX");
        Assert.Equal(("QX", 20m, 12m, 12m), (s.TaxCountry, s.TaxRate, s.PriceIncludingTax, s.RegularPriceIncludingTax));
        var z = await Price(anon, zero, "qx"); // case does not matter
        Assert.Equal((0m, 10m), (z.TaxRate, z.PriceIncludingTax));

        // No country and no default for the shop: no tax figures, as before.
        var none = await Price(anon, standard, null);
        Assert.Equal((10m, null, null, null), (none.Price, none.TaxCountry, none.TaxRate, none.PriceIncludingTax));
        Assert.Equal(HttpStatusCode.BadRequest, (await anon.GetAsync($"/api/v1/storefront/guest/prices?productIds={standard.Id}&countryCode=GBR")).StatusCode);
    }

    [Fact]
    public async Task A_sale_price_is_shown_with_tax_beside_the_regular_price_with_tax_and_signed_in_shoppers_get_the_same_figures()
    {
        var staff = await factory.CreateAuthenticatedApiClientAsync();
        var anon = factory.CreateClient();
        var p = await TaxedProduct(staff, 10m, 20m, "QX");
        var sale = await SaleTests.NewSale(staff, 25m);
        await staff.PutAsJsonAsync($"/api/v1/sales/{sale.Id}/products/{p.Id}", new SaleProductInput());

        var guest = await Price(anon, p, "QX");
        Assert.Equal((7.5m, 9m, 12m), (guest.Price, guest.PriceIncludingTax, guest.RegularPriceIncludingTax));

        var shopperClient = factory.CreateClient();
        var reg = await Read<RegisteredDto>(await shopperClient.PostAsJsonAsync("/api/v1/storefront/auth/register", new RegisterInput { Email = $"ti-{Tag()}@example.com", Password = "a-long-enough-password" }));
        shopperClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", reg.Token.AccessToken);
        Assert.Equal(guest with { }, await Price(shopperClient, p, "QX", "/api/v1/storefront/prices"));
        Assert.Equal(guest, await Price(staff, p, "QX", $"/api/v1/customers/{reg.Profile.Id}/prices"));
        await staff.DeleteAsync($"/api/v1/sales/{sale.Id}");
    }

    private sealed class HomeCountryFactory : PiranhaFactory
    {
        protected override IReadOnlyDictionary<string, string?> ExtraSettings => new Dictionary<string, string?> { ["PiranhaCommerce:Ecommerce:DefaultCountryCode"] = "qx" };
    }

    [Fact]
    public async Task A_shop_with_a_home_country_shows_tax_inclusive_prices_when_the_caller_names_no_destination()
    {
        await using var home = new HomeCountryFactory();
        var staff = await home.CreateAuthenticatedApiClientAsync();
        var p = await TaxedProduct(staff, 50m, 10m, "QX");
        var price = await Price(home.CreateClient(), p, null);
        Assert.Equal(("QX", 10m, 55m), (price.TaxCountry, price.TaxRate, price.PriceIncludingTax));
        Assert.Equal("GB", (await Price(home.CreateClient(), p, "GB")).TaxCountry); // an explicit one wins
    }

    [Fact]
    public async Task A_quote_lists_each_line_with_its_tax_so_the_tax_inclusive_lines_add_up_to_the_total_and_an_order_line_says_the_same()
    {
        var staff = await factory.CreateAuthenticatedApiClientAsync();
        var anon = factory.CreateClient();
        var a = await TaxedProduct(staff, 10.05m, 20m, "QX");
        var b = await TaxedProduct(staff, 3.33m, 5m, "QX");
        var untaxed = await TaxedProduct(staff, 4m, null, "QX");

        var basket = await Read<BasketDto>(await anon.PostAsync("/api/v1/storefront/guest/baskets", null));
        foreach (var (p, q) in new[] { (a, 3), (b, 2), (untaxed, 1) })
            await anon.PutAsJsonAsync($"/api/v1/storefront/guest/baskets/{basket.Id}/lines/{p.Id}", new BasketLineInput { Quantity = q });
        var code = "TI" + Tag().ToUpperInvariant();
        await staff.PostAsJsonAsync("/api/v1/promotions", new PromotionInput { Code = code, Kind = PromotionKind.Percent, Value = 10 }, Json);
        await anon.PostAsJsonAsync($"/api/v1/storefront/guest/baskets/{basket.Id}/promo", new CodeInput { Code = code });

        var quote = await Read<QuoteDto>(await anon.PostAsJsonAsync($"/api/v1/storefront/guest/baskets/{basket.Id}/quote", new QuoteInput { CountryCode = "QX" }));
        var lines = quote.Lines!;
        Assert.Equal(3, lines.Count);
        var la = lines.Single(l => l.ProductId == a.Id);
        Assert.Equal((20m, 30.15m), (la.TaxRate, la.LineTotal));
        Assert.Equal(la.LineTotal - la.DiscountAmount + la.TaxAmount, la.LineTotalIncludingTax);
        Assert.Equal(0m, lines.Single(l => l.ProductId == untaxed.Id).TaxAmount);
        Assert.Equal(quote.Total, lines.Sum(l => l.LineTotalIncludingTax) + quote.ShippingTotal + quote.ShippingTax);
        Assert.Equal(quote.TaxTotal, lines.Sum(l => l.TaxAmount) + quote.ShippingTax);

        var order = await Read<OrderDto>(await anon.PostAsJsonAsync($"/api/v1/storefront/guest/baskets/{basket.Id}/checkout", new CheckoutInput
        {
            Email = $"ti-{Tag()}@example.com",
            ShippingAddress = new AddressInput { Recipient = "Ada", Line1 = "1 High St", City = "Leeds", PostalCode = "LS1", CountryCode = "QX" },
        }));
        Assert.Equal(quote.Total, order.Total);
        Assert.Equal(order.Total, order.Lines.Sum(l => l.LineTotalIncludingTax) + order.ShippingTotal + order.ShippingTax);
        Assert.Equal(la.LineTotalIncludingTax, order.Lines.Single(l => l.ProductId == a.Id).LineTotalIncludingTax);
    }
}
