using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.DependencyInjection;
using PiranhaCommerce.Core.Paging;
using PiranhaCommerce.Ecommerce;
using PiranhaCommerce.Web.SampleData;

namespace PiranhaCommerce.IntegrationTests;

/// <summary>The sample shop's data: it seeds through the real services, the shop API shows it, and running it again changes nothing.</summary>
public class SampleDataTests(PiranhaCommerceFactory factory) : IClassFixture<PiranhaCommerceFactory>
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };

    [Fact]
    public async Task The_sample_catalogue_seeds_once_and_the_shop_can_sell_from_it()
    {
        if (PiranhaCommerceFactory.UsesSharedDatabase) return; // it adds a tax rule and a sale: not for a database other test classes use

        var seeder = factory.Services.GetRequiredService<SampleDataSeeder>();
        await seeder.SeedAsync();
        var shop = factory.CreateClient();

        var categories = await shop.GetFromJsonAsync<List<ShopCategoryDto>>("/api/v1/storefront/categories", Json);
        Assert.Equal(["Lips", "Eyes", "Face", "Skin", "Gift sets"], categories!.Select(c => c.Name));
        Assert.Equal(["Lipsticks", "Lip gloss", "Lip liners"], categories![0].Children.Select(c => c.Name));

        var all = await shop.GetFromJsonAsync<PagedResult<ShopProductSummaryDto>>("/api/v1/storefront/products?pageSize=50", Json);
        Assert.Equal(16, all!.TotalCount);
        Assert.All(all.Items, i => Assert.NotNull(i.Image));                         // every product has a picture
        Assert.Contains(all.Items, i => i.Name == "Velvet Matte Lipstick" && i.HasVariants && i.ReviewCount == 3);

        // Lipstick: six shades, one of them sold out.
        var lipstick = await shop.GetFromJsonAsync<ShopProductDto>("/api/v1/storefront/products/by-slug/velvet-matte-lipstick", Json);
        Assert.Equal(6, lipstick!.Variants.Count);
        Assert.Single(lipstick.Variants, v => !v.Available);
        Assert.All(lipstick.Variants, v => Assert.NotEmpty(v.Images));

        // A product that comes in shades is suggested as available while any shade is in stock.
        var suggested = await shop.GetFromJsonAsync<SuggestionsDto>($"/api/v1/storefront/products/{lipstick.Id}/suggestions", Json);
        Assert.Contains(suggested!.Related, r => r.Name == "Satin Cream Lipstick" && r.Available);

        // The eye make-up sale and VAT show up in prices.
        var mascara = await shop.GetFromJsonAsync<ShopProductDto>("/api/v1/storefront/products/by-slug/lash-lift-mascara?countryCode=GB", Json);
        Assert.Equal((12.50m, 10.00m, true, 20m), (mascara!.Price.RegularPrice, mascara.Price.Price, mascara.Price.Discounted, mascara.Price.TaxRate));
        Assert.Equal(12.00m, mascara.Price.PriceIncludingTax);

        // Delivery: the UK has two methods, other countries one.
        var staff = await factory.CreateAuthenticatedApiClientAsync();
        var methods = await staff.GetFromJsonAsync<List<ShippingMethodDto>>("/api/v1/shipping-methods", Json);
        Assert.Equal(3, methods!.Count);

        // Run again: nothing is added twice.
        await seeder.SeedAsync();
        var again = await shop.GetFromJsonAsync<PagedResult<ShopProductSummaryDto>>("/api/v1/storefront/products?pageSize=50", Json);
        Assert.Equal(all.TotalCount, again!.TotalCount);
        Assert.Equal(3, (await staff.GetFromJsonAsync<List<ShippingMethodDto>>("/api/v1/shipping-methods", Json))!.Count);
    }

    [Fact]
    public async Task A_guest_can_buy_from_the_sample_shop_end_to_end_with_vat_free_delivery_and_a_promo_code()
    {
        if (PiranhaCommerceFactory.UsesSharedDatabase) return;

        await factory.Services.GetRequiredService<SampleDataSeeder>().SeedAsync();
        var shop = factory.CreateClient();
        var product = await shop.GetFromJsonAsync<ShopProductDto>("/api/v1/storefront/products/by-slug/skin-glow-foundation", Json);
        var shade = product!.Variants.First(v => v.Available);

        var basket = await (await shop.PostAsync("/api/v1/storefront/guest/baskets", null)).Content.ReadFromJsonAsync<BasketDto>(Json);
        Assert.True((await shop.PutAsJsonAsync($"/api/v1/storefront/guest/baskets/{basket!.Id}/lines/{shade.ProductId}", new BasketLineInput { Quantity = 2 })).IsSuccessStatusCode);
        Assert.True((await shop.PostAsJsonAsync($"/api/v1/storefront/guest/baskets/{basket.Id}/promo", new CodeInput { Code = "welcome10" })).IsSuccessStatusCode);

        // Two foundations with the code come to 39.60, just under the 40.00 free-delivery limit (judged after the discount): standard delivery costs 3.95.
        var url = $"/api/v1/storefront/guest/baskets/{basket.Id}";
        var options = await shop.GetFromJsonAsync<List<ShippingOptionDto>>($"{url}/shipping-options?countryCode=GB&postalCode=LS1 1AA", Json);
        Assert.Equal(["Standard delivery (3-5 working days)", "Next working day delivery"], options!.Select(o => o.Name));
        Assert.Equal(3.95m, options![0].Price);

        // A third takes it over the limit: free standard delivery. Other countries get the international method only.
        Assert.True((await shop.PutAsJsonAsync($"{url}/lines/{shade.ProductId}", new BasketLineInput { Quantity = 3 })).IsSuccessStatusCode);
        options = await shop.GetFromJsonAsync<List<ShippingOptionDto>>($"{url}/shipping-options?countryCode=GB&postalCode=LS1 1AA", Json);
        Assert.Equal(0m, options!.First(o => o.Name.StartsWith("Standard")).Price);
        var abroad = await shop.GetFromJsonAsync<List<ShippingOptionDto>>($"{url}/shipping-options?countryCode=FR", Json);
        Assert.Equal(["International tracked delivery (5-10 working days)"], abroad!.Select(o => o.Name));

        var order = await (await shop.PostAsJsonAsync($"{url}/checkout", new CheckoutInput
        {
            Email = "guest@example.com", ShippingMethodId = options!.First(o => o.Name.StartsWith("Standard")).Id,
            ShippingAddress = new PiranhaCommerce.Customers.AddressInput { Line1 = "1 High Street", City = "Leeds", PostalCode = "LS1 1AA", CountryCode = "GB" },
        })).Content.ReadFromJsonAsync<OrderDto>(Json);
        // 3 x 22.00 = 66.00, less 10% = 59.40, free delivery, VAT at 20% = 11.88.
        Assert.Equal((66m, 6.60m, 0m, 11.88m, 71.28m), (order!.Subtotal, order.DiscountTotal, order.ShippingTotal, order.TaxTotal, order.Total));
    }
}
