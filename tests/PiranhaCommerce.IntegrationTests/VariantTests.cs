using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using PiranhaCommerce.Catalog;
using PiranhaCommerce.Ecommerce;

namespace PiranhaCommerce.IntegrationTests;

/// <summary>Products that come in options (size, colour): each variant is an ordinary product, so stock, baskets and prices work on it unchanged.</summary>
[Collection(OrderSettings.Name)]
public class VariantTests(PiranhaCommerceFactory factory) : IClassFixture<PiranhaCommerceFactory>
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };
    private static string Tag() => Guid.NewGuid().ToString("N")[..8];
    private static async Task<T> Read<T>(HttpResponseMessage r) => (await r.Content.ReadFromJsonAsync<T>(Json))!;

    private static async Task<ProductDto> Parent(HttpClient staff, decimal price = 20m, bool published = true)
    {
        var p = await Read<ProductDto>(await staff.PostAsJsonAsync("/api/v1/products", new ProductInput { Name = "Tee " + Tag(), Sku = "TEE-" + Tag(), Price = price }));
        await staff.PutAsJsonAsync($"/api/v1/products/{p.Id}/listing", new ProductListingInput { Published = published, WeightGrams = 250 });
        return p;
    }

    private static async Task<ProductVariantsDto> Option(HttpClient staff, ProductDto p, string name, params string[] values)
    {
        var r = await staff.PostAsJsonAsync($"/api/v1/products/{p.Id}/options", new ProductOptionInput { Name = name, Values = [.. values] });
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        return await Read<ProductVariantsDto>(r);
    }

    private static async Task<ProductVariantsDto> Generate(HttpClient staff, ProductDto p, decimal? price = null)
    {
        var r = await staff.PostAsJsonAsync($"/api/v1/products/{p.Id}/variants/generate", new GenerateVariantsInput { Price = price });
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        return await Read<ProductVariantsDto>(r);
    }

    [Fact]
    public async Task Variants_are_made_for_every_combination_and_a_second_run_only_adds_what_is_missing()
    {
        var staff = await factory.CreateAuthenticatedApiClientAsync();
        var p = await Parent(staff, 20m);
        await Option(staff, p, "Size", "S", "M", "L");
        var set = await Option(staff, p, "Colour", "Red", "Blue");

        var made = await Generate(staff, p, 22.5m);
        Assert.Equal(6, made.Variants.Count);
        Assert.All(made.Variants, v => { Assert.Equal(22.5m, v.Price); Assert.Equal(2, v.Values.Count); Assert.StartsWith(p.Name + " - ", v.Name); });
        Assert.Equal(["Size", "Colour"], made.Variants[0].Values.Select(x => x.Option));
        Assert.Contains(made.Variants, v => v.Name == $"{p.Name} - M / Blue");
        Assert.Equal(6, made.Variants.Select(v => string.Join(",", v.Values.Select(x => x.ValueId))).Distinct().Count());

        Assert.Equal(6, (await Generate(staff, p)).Variants.Count); // nothing new to make

        var size = set.Options.Single(o => o.Name == "Size");
        var added = await Read<ProductVariantsDto>(await staff.PostAsJsonAsync($"/api/v1/products/{p.Id}/options/{size.Id}/values", new ProductOptionValueInput { Value = "XL" }));
        Assert.Equal(6, added.Variants.Count);
        Assert.Equal(8, (await Generate(staff, p)).Variants.Count); // XL x Red and XL x Blue

        // A variant says what it is, and its listing started as its parent's (so it can be bought at once).
        var one = made.Variants[0];
        var info = await Read<ProductVariantsDto>(await staff.GetAsync($"/api/v1/products/{one.ProductId}/variants"));
        Assert.Equal(p.Id, info.ParentProductId);
        Assert.Equal(2, info.Values.Count);
        var listing = await Read<ProductListingDto>(await staff.GetAsync($"/api/v1/products/{one.ProductId}/listing"));
        Assert.Equal((true, 250), (listing.Published, listing.WeightGrams));
    }

    [Fact]
    public async Task A_parent_cannot_be_bought_but_its_variants_can_with_their_own_price_and_stock()
    {
        var staff = await factory.CreateAuthenticatedApiClientAsync();
        var anon = factory.CreateClient();
        var p = await Parent(staff);
        await Option(staff, p, "Size", "S", "L");
        var v = (await Generate(staff, p)).Variants;
        var large = v.Single(x => x.Name.EndsWith("L"));
        await staff.PutAsJsonAsync($"/api/v1/products/{large.ProductId}", new ProductInput { Name = large.Name, Sku = "LG-" + Tag(), Price = 25m });
        await staff.PostAsJsonAsync($"/api/v1/products/{large.ProductId}/stock/adjustments", new StockAdjustmentInput { Delta = 5, Reason = "Delivery" });
        await staff.PostAsJsonAsync($"/api/v1/products/{p.Id}/stock/adjustments", new StockAdjustmentInput { Delta = 5, Reason = "Delivery" });

        var basket = await Read<BasketDto>(await anon.PostAsync("/api/v1/storefront/guest/baskets", null));
        var parent = await anon.PutAsJsonAsync($"/api/v1/storefront/guest/baskets/{basket.Id}/lines/{p.Id}", new BasketLineInput { Quantity = 1 });
        Assert.Equal(HttpStatusCode.BadRequest, parent.StatusCode);
        Assert.Contains("variants", await parent.Content.ReadAsStringAsync());

        var ok = await anon.PutAsJsonAsync($"/api/v1/storefront/guest/baskets/{basket.Id}/lines/{large.ProductId}", new BasketLineInput { Quantity = 2 });
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        var line = (await Read<BasketDto>(ok)).Lines.Single();
        Assert.Equal((large.ProductId, 25m), (line.ProductId, line.UnitPrice));
    }

    [Fact]
    public async Task The_rules_keep_variants_consistent()
    {
        var staff = await factory.CreateAuthenticatedApiClientAsync();
        var p = await Parent(staff);
        var set = await Option(staff, p, "Size", "S", "M");
        var colour = (await Option(staff, p, "Colour", "Red")).Options.Single(o => o.Name == "Colour");
        var size = set.Options.Single();

        Assert.Equal(HttpStatusCode.BadRequest, (await staff.PostAsJsonAsync($"/api/v1/products/{p.Id}/options", new ProductOptionInput { Name = "size" })).StatusCode); // same name
        Assert.Equal(HttpStatusCode.BadRequest, (await staff.PostAsJsonAsync($"/api/v1/products/{p.Id}/options/{size.Id}/values", new ProductOptionValueInput { Value = "s" })).StatusCode);

        // One at a time: exactly one value per option.
        var s = size.Values.Single(x => x.Value == "S"); var m = size.Values.Single(x => x.Value == "M");
        var red = (await Read<ProductVariantsDto>(await staff.GetAsync($"/api/v1/products/{p.Id}/variants"))).Options.Single(o => o.Name == "Colour").Values.Single();
        Assert.Equal(HttpStatusCode.BadRequest, (await staff.PostAsJsonAsync($"/api/v1/products/{p.Id}/variants", new ProductVariantInput { ValueIds = [s.Id] })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await staff.PostAsJsonAsync($"/api/v1/products/{p.Id}/variants", new ProductVariantInput { ValueIds = [s.Id, m.Id] })).StatusCode);
        var sku = "VAR-" + Tag();
        var made = await staff.PostAsJsonAsync($"/api/v1/products/{p.Id}/variants", new ProductVariantInput { ValueIds = [s.Id, red.Id], Sku = sku, Price = 19m });
        Assert.Equal(HttpStatusCode.OK, made.StatusCode);
        var variant = (await Read<ProductVariantsDto>(made)).Variants.Single();
        Assert.Equal((sku, 19m), (variant.Sku, variant.Price));
        Assert.Equal(HttpStatusCode.Conflict, (await staff.PostAsJsonAsync($"/api/v1/products/{p.Id}/variants", new ProductVariantInput { ValueIds = [s.Id, red.Id] })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await staff.PostAsJsonAsync($"/api/v1/products/{p.Id}/variants", new ProductVariantInput { ValueIds = [m.Id, red.Id], Sku = sku })).StatusCode);

        // In use: cannot remove a value, an option, or the parent; a variant cannot get options or a new option be added to a parent with variants.
        Assert.Equal(HttpStatusCode.Conflict, (await staff.DeleteAsync($"/api/v1/products/{p.Id}/options/{size.Id}/values/{s.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await staff.DeleteAsync($"/api/v1/products/{p.Id}/options/{size.Id}/values/{m.Id}")).StatusCode); // unused
        Assert.Equal(HttpStatusCode.Conflict, (await staff.DeleteAsync($"/api/v1/products/{p.Id}/options/{colour.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await staff.DeleteAsync($"/api/v1/products/{p.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await staff.PostAsJsonAsync($"/api/v1/products/{p.Id}/options", new ProductOptionInput { Name = "Fit" })).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await staff.PostAsJsonAsync($"/api/v1/products/{variant.ProductId}/options", new ProductOptionInput { Name = "Fit" })).StatusCode);

        // Remove the variant and everything can go.
        Assert.Equal(HttpStatusCode.NoContent, (await staff.DeleteAsync($"/api/v1/products/{variant.ProductId}")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await staff.DeleteAsync($"/api/v1/products/{p.Id}/options/{colour.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await staff.DeleteAsync($"/api/v1/products/{p.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await staff.PostAsJsonAsync($"/api/v1/products/{(await Parent(staff)).Id}/variants/generate", new GenerateVariantsInput())).StatusCode); // no options
    }

    [Fact]
    public async Task A_category_sale_and_group_discounts_reach_variants_through_their_parents_categories()
    {
        var staff = await factory.CreateAuthenticatedApiClientAsync();
        var anon = factory.CreateClient();
        var p = await Parent(staff, 40m);
        await Option(staff, p, "Size", "S");
        var variant = (await Generate(staff, p)).Variants.Single();
        var category = await Read<CategoryDto>(await staff.PostAsJsonAsync("/api/v1/categories", new CategoryInput { Name = "Variant cat " + Tag() }));
        await staff.PutAsJsonAsync($"/api/v1/products/{p.Id}/categories", new ProductCategoriesInput { CategoryIds = [category.Id] });

        var sale = await SaleTests.NewSale(staff, 25m);
        await staff.PutAsync($"/api/v1/sales/{sale.Id}/categories/{category.Id}", null);
        var price = (await Read<List<ShopperPriceDto>>(await anon.GetAsync($"/api/v1/storefront/guest/prices?productIds={variant.ProductId}"))).Single();
        Assert.Equal((40m, 30m, true), (price.RegularPrice, price.Price, price.Discounted));
        await staff.DeleteAsync($"/api/v1/sales/{sale.Id}");
    }
}
