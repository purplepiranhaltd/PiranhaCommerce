using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using PiranhaCommerce.Catalog;
using PiranhaCommerce.Core.Paging;
using PiranhaCommerce.Ecommerce;

namespace PiranhaCommerce.IntegrationTests;

/// <summary>Specification attributes: staff define them and set values on products, shoppers see them on the product and filter listings by them.</summary>
public class SpecificationTests(PiranhaFactory factory) : IClassFixture<PiranhaFactory>
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };
    private static string Tag() => Guid.NewGuid().ToString("N")[..8];
    private static async Task<T> Read<T>(HttpResponseMessage r) { Assert.True(r.IsSuccessStatusCode, r.StatusCode + " " + await r.Content.ReadAsStringAsync()); return (await r.Content.ReadFromJsonAsync<T>(Json))!; }

    private static async Task<SpecAttributeDto> NewAttribute(HttpClient staff, string name, bool filterable = true, string? slug = null) =>
        await Read<SpecAttributeDto>(await staff.PostAsJsonAsync("/api/v1/specifications", new SpecAttributeInput { Name = name, Slug = slug, Filterable = filterable }));

    private static async Task<SpecValueDto> NewValue(HttpClient staff, Guid attributeId, string value, string? slug = null) =>
        await Read<SpecValueDto>(await staff.PostAsJsonAsync($"/api/v1/specifications/{attributeId}/values", new SpecValueInput { Value = value, Slug = slug }));

    private static async Task<ProductDto> NewProduct(HttpClient staff, string name, bool published = true, int stock = 5)
    {
        var p = await Read<ProductDto>(await staff.PostAsJsonAsync("/api/v1/products", new ProductInput { Name = name, Sku = "SP-" + Tag(), Price = 10m }));
        await staff.PutAsJsonAsync($"/api/v1/products/{p.Id}/listing", new ProductListingInput { Published = published });
        if (stock > 0) await staff.PostAsJsonAsync($"/api/v1/products/{p.Id}/stock/adjustments", new StockAdjustmentInput { Delta = stock, Reason = "Delivery" });
        return p;
    }

    private static Task<HttpResponseMessage> SetSpecs(HttpClient staff, Guid productId, params Guid[] valueIds) =>
        staff.PutAsJsonAsync($"/api/v1/products/{productId}/specifications", new ProductSpecsInput { ValueIds = [.. valueIds] });

    [Fact]
    public async Task Staff_manage_attributes_and_values_with_unique_names_and_slugs_made_from_the_name_when_left_out()
    {
        var staff = await factory.CreateAuthenticatedApiClientAsync();
        var tag = Tag();
        var made = await NewAttribute(staff, $"  Skin type {tag} ");
        Assert.Equal($"Skin type {tag}", made.Name);
        Assert.Equal($"skin-type-{tag}", made.Slug);                // made from the name
        Assert.True(made.Filterable);

        Assert.Equal(HttpStatusCode.BadRequest, (await staff.PostAsJsonAsync("/api/v1/specifications", new SpecAttributeInput { Name = $"SKIN TYPE {tag}" })).StatusCode); // name, any case
        Assert.Equal(HttpStatusCode.BadRequest, (await staff.PostAsJsonAsync("/api/v1/specifications", new SpecAttributeInput { Name = "Other " + tag, Slug = made.Slug })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await staff.PostAsJsonAsync("/api/v1/specifications", new SpecAttributeInput { Name = "???" })).StatusCode);          // nothing to make a slug from
        Assert.Equal(HttpStatusCode.BadRequest, (await staff.PostAsJsonAsync("/api/v1/specifications", new SpecAttributeInput { Name = "Bad " + tag, Slug = "Not A Slug!" })).StatusCode);
        Assert.Equal("fine-print-" + tag, (await Read<SpecAttributeDto>(await staff.PostAsJsonAsync("/api/v1/specifications", new SpecAttributeInput { Name = "???" + tag, Slug = "fine-print-" + tag }))).Slug);

        // Values: unique within the attribute (any letter case), and the same value may exist under another attribute.
        var dry = await NewValue(staff, made.Id, "Dry");
        Assert.Equal(("Dry", "dry"), (dry.Value, dry.Slug));
        Assert.Equal(HttpStatusCode.BadRequest, (await staff.PostAsJsonAsync($"/api/v1/specifications/{made.Id}/values", new SpecValueInput { Value = "DRY" })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await staff.PostAsJsonAsync($"/api/v1/specifications/{made.Id}/values", new SpecValueInput { Value = "Parched", Slug = "dry" })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await staff.PostAsJsonAsync($"/api/v1/specifications/{Guid.NewGuid()}/values", new SpecValueInput { Value = "x" })).StatusCode);
        var other = await NewAttribute(staff, $"Hair type {tag}");
        Assert.Equal("dry", (await NewValue(staff, other.Id, "Dry")).Slug);

        // A PUT with the slug left out derives it again (unlike a brand's, a slug is never empty).
        var put = await Read<SpecAttributeDto>(await staff.PutAsJsonAsync($"/api/v1/specifications/{made.Id}", new SpecAttributeInput { Name = $"Skin kind {tag}", Filterable = false }));
        Assert.Equal(($"skin-kind-{tag}", false, 1), (put.Slug, put.Filterable, put.Values.Count));
        var renamed = await Read<SpecValueDto>(await staff.PutAsJsonAsync($"/api/v1/specifications/{made.Id}/values/{dry.Id}", new SpecValueInput { Value = "Very dry", DisplayOrder = 2 }));
        Assert.Equal(("Very dry", "very-dry", 2), (renamed.Value, renamed.Slug, renamed.DisplayOrder));
        Assert.Equal(HttpStatusCode.NotFound, (await staff.PutAsJsonAsync($"/api/v1/specifications/{other.Id}/values/{dry.Id}", new SpecValueInput { Value = "x" })).StatusCode); // not that attribute's value

        Assert.Equal(made.Id, (await Read<SpecAttributeDto>(await staff.GetAsync($"/api/v1/specifications/by-slug/{put.Slug.ToUpperInvariant()}"))).Id);
        Assert.Contains(made.Id, (await Read<List<SpecAttributeDto>>(await staff.GetAsync("/api/v1/specifications"))).Select(a => a.Id));

        var anonymous = factory.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("/api/v1/specifications")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.PostAsJsonAsync("/api/v1/specifications", new SpecAttributeInput { Name = "Nope " + tag })).StatusCode);

        // Deleting an attribute takes its values with it when no product has any.
        Assert.Equal(HttpStatusCode.NoContent, (await staff.DeleteAsync($"/api/v1/specifications/{made.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await staff.DeleteAsync($"/api/v1/specifications/{made.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await staff.GetAsync($"/api/v1/specifications/{made.Id}")).StatusCode);
    }

    [Fact]
    public async Task A_product_has_a_set_of_values_a_variant_shows_its_parents_and_values_in_use_cannot_be_deleted()
    {
        var staff = await factory.CreateAuthenticatedApiClientAsync();
        var tag = Tag();
        var finish = await NewAttribute(staff, "Finish " + tag);
        var matte = await NewValue(staff, finish.Id, "Matte");
        var satin = await NewValue(staff, finish.Id, "Satin");
        var vegan = await NewAttribute(staff, "Vegan " + tag);
        var yes = await NewValue(staff, vegan.Id, "Yes");
        var product = await NewProduct(staff, "Specced " + tag);

        Assert.Empty((await Read<ProductSpecsDto>(await staff.GetAsync($"/api/v1/products/{product.Id}/specifications"))).ValueIds);

        // A product can have several values of one attribute, and a PUT replaces the whole set.
        var set = await Read<ProductSpecsDto>(await SetSpecs(staff, product.Id, matte.Id, satin.Id, yes.Id));
        Assert.Equal(3, set.ValueIds.Count);
        Assert.Equal(["Matte", "Satin"], set.Groups.Single(g => g.Slug == finish.Slug).Values.Select(v => v.Value));
        Assert.Equal(1, (await Read<SpecAttributeDto>(await staff.GetAsync($"/api/v1/specifications/{finish.Id}"))).ProductCount);   // counted once, not twice
        Assert.Equal(1, (await Read<SpecAttributeDto>(await staff.GetAsync($"/api/v1/specifications/{finish.Id}"))).Values.Single(v => v.Id == satin.Id).ProductCount);
        var replaced = await Read<ProductSpecsDto>(await SetSpecs(staff, product.Id, yes.Id));
        Assert.Equal([yes.Id], replaced.ValueIds);

        // Bad requests change nothing.
        Assert.Equal(HttpStatusCode.BadRequest, (await SetSpecs(staff, product.Id, Guid.NewGuid())).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await SetSpecs(staff, product.Id, yes.Id, yes.Id)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await SetSpecs(staff, Guid.NewGuid(), yes.Id)).StatusCode);
        Assert.Equal([yes.Id], (await Read<ProductSpecsDto>(await staff.GetAsync($"/api/v1/products/{product.Id}/specifications"))).ValueIds);

        // A variant shows its parent's values and cannot be given any.
        Assert.True((await staff.PostAsJsonAsync($"/api/v1/products/{product.Id}/options", new ProductOptionInput { Name = "Size", Values = ["S", "M"] })).IsSuccessStatusCode);
        var variant = (await Read<ProductVariantsDto>(await staff.PostAsJsonAsync($"/api/v1/products/{product.Id}/variants/generate", new GenerateVariantsInput { Price = 10m }))).Variants[0].ProductId;
        var shown = await Read<ProductSpecsDto>(await staff.GetAsync($"/api/v1/products/{variant}/specifications"));
        Assert.Equal([yes.Id], shown.ValueIds);
        Assert.True(shown.Inherited);
        Assert.Equal(HttpStatusCode.BadRequest, (await SetSpecs(staff, variant, matte.Id)).StatusCode);

        // In use: a value and its attribute are kept; free the product and they can go.
        Assert.Equal(HttpStatusCode.Conflict, (await staff.DeleteAsync($"/api/v1/specifications/{vegan.Id}/values/{yes.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await staff.DeleteAsync($"/api/v1/specifications/{vegan.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await staff.DeleteAsync($"/api/v1/specifications/{finish.Id}/values/{matte.Id}")).StatusCode);   // nobody has it now
        await SetSpecs(staff, product.Id);
        Assert.Equal(HttpStatusCode.NoContent, (await staff.DeleteAsync($"/api/v1/specifications/{vegan.Id}")).StatusCode);
    }

    private static async Task<PagedResult<ShopProductSummaryDto>> Shop(HttpClient c, string query) =>
        await Read<PagedResult<ShopProductSummaryDto>>(await c.GetAsync("/api/v1/storefront/products?" + query));

    private static async Task<List<ShopFacetDto>> Facets(HttpClient c, string query) =>
        await Read<List<ShopFacetDto>>(await c.GetAsync("/api/v1/storefront/products/facets?" + query));

    private static Dictionary<string, (int Count, bool Selected)> Values(List<ShopFacetDto> facets, string attribute) =>
        facets.Single(f => f.Attribute == attribute).Values.ToDictionary(v => v.Key, v => (v.Count, v.Selected));

    [Fact]
    public async Task Shoppers_filter_by_specification_any_of_within_an_attribute_all_of_across_them_and_each_facet_counts_without_its_own_filter()
    {
        var staff = await factory.CreateAuthenticatedApiClientAsync();
        var shopper = factory.CreateClient();
        var tag = Tag();
        var finish = await NewAttribute(staff, "Finish " + tag, slug: "finish-" + tag);
        var matte = await NewValue(staff, finish.Id, "Matte");
        var satin = await NewValue(staff, finish.Id, "Satin");
        var skin = await NewAttribute(staff, "Skin " + tag, slug: "skin-" + tag);
        var dry = await NewValue(staff, skin.Id, "Dry");
        var oily = await NewValue(staff, skin.Id, "Oily");
        var internalNote = await NewAttribute(staff, "Note " + tag, filterable: false, slug: "note-" + tag);
        var shea = await NewValue(staff, internalNote.Id, "Shea");

        var p1 = await NewProduct(staff, $"Cream one {tag}");                       // matte, dry
        var p2 = await NewProduct(staff, $"Cream two {tag}");                       // matte, oily
        var p3 = await NewProduct(staff, $"Cream three {tag}", stock: 0);           // satin, dry; sold out
        var p4 = await NewProduct(staff, $"Cream four {tag}");                      // satin and matte, no skin type
        var p5 = await NewProduct(staff, $"Cream bare {tag}");                      // nothing
        var p6 = await NewProduct(staff, $"Cream hidden {tag}", published: false);  // matte, dry; not on sale
        await SetSpecs(staff, p1.Id, matte.Id, dry.Id, shea.Id);
        await SetSpecs(staff, p2.Id, matte.Id, oily.Id);
        await SetSpecs(staff, p3.Id, satin.Id, dry.Id);
        await SetSpecs(staff, p4.Id, satin.Id, matte.Id);
        await SetSpecs(staff, p6.Id, matte.Id, dry.Id);

        string F(SpecValueDto v, SpecAttributeDto a) => $"{a.Slug}:{v.Slug}";
        async Task<List<string>> Names(string query) => (await Shop(shopper, $"q={tag}&{query}")).Items.Select(i => i.Name.Replace($" {tag}", "")).Order().ToList();

        Assert.Equal(["Cream bare", "Cream four", "Cream one", "Cream three", "Cream two"], await Names(""));
        Assert.Equal(["Cream four", "Cream one", "Cream two"], await Names($"spec={F(matte, finish)}"));
        Assert.Equal(["Cream four", "Cream one", "Cream three", "Cream two"], await Names($"spec={F(matte, finish)}&spec={F(satin, finish)}"));   // any of
        Assert.Equal(["Cream one"], await Names($"spec={F(matte, finish)}&spec={F(dry, skin)}"));                                              // all of: p4 is matte but has no skin type
        Assert.Equal(["Cream one", "Cream two"], await Names($"spec={F(matte, finish)}&spec={F(dry, skin)}&spec={F(oily, skin)}"));
        Assert.Equal(["Cream one", "Cream three"], await Names($"spec={F(dry, skin)}"));

        // Unknown or unfilterable things: an unknown attribute or a non-filterable one is ignored; an unknown value of a known attribute matches nothing.
        Assert.Equal(5, (await Shop(shopper, $"q={tag}&spec=no-such:thing")).TotalCount);
        Assert.Equal(5, (await Shop(shopper, $"q={tag}&spec={internalNote.Slug}:{shea.Slug}")).TotalCount);
        Assert.Equal(0, (await Shop(shopper, $"q={tag}&spec={finish.Slug}:no-such")).TotalCount);
        Assert.Equal(["Cream four", "Cream one", "Cream two"], await Names($"spec={F(matte, finish)}&spec=garbage"));                         // a key with no colon is ignored
        Assert.Equal(["Cream four", "Cream one", "Cream two"], await Names($"spec={finish.Slug}:no-such&spec={F(matte, finish)}"));       // an unknown value beside a known one of the same attribute is ignored
        Assert.Equal(["Cream three"], await Names($"spec={F(dry, skin)}&inStock=false&spec={F(satin, finish)}"));

        // Facets: only filterable attributes, counted over products on sale, with every other filter applied but the facet's own.
        var all = await Facets(shopper, $"q={tag}");
        Assert.DoesNotContain(all, f => f.Attribute == internalNote.Slug);
        Assert.All(all.Where(f => f.Attribute is not null), f => Assert.Equal("spec", f.Key));
        var finishAll = Values(all, finish.Slug);
        Assert.Equal((3, false), finishAll[F(matte, finish)]);                      // p1, p2, p4 (p6 is not on sale)
        Assert.Equal((2, false), finishAll[F(satin, finish)]);                      // p3, p4
        Assert.Equal((2, false), Values(all, skin.Slug)[F(dry, skin)]);             // p1, p3
        Assert.Equal((1, false), Values(all, skin.Slug)[F(oily, skin)]);

        var chosen = await Facets(shopper, $"q={tag}&spec={F(matte, finish)}");
        var finishWithMatte = Values(chosen, finish.Slug);
        Assert.Equal((3, true), finishWithMatte[F(matte, finish)]);
        Assert.Equal((2, false), finishWithMatte[F(satin, finish)]);                // its own filter does not hide the alternative
        var skinWithMatte = Values(chosen, skin.Slug);
        Assert.Equal((1, false), skinWithMatte[F(dry, skin)]);                      // narrowed by the finish choice: only p1 is matte and dry
        Assert.Equal((1, false), skinWithMatte[F(oily, skin)]);

        var both = await Facets(shopper, $"q={tag}&spec={F(matte, finish)}&spec={F(dry, skin)}");
        Assert.Equal((1, true), Values(both, finish.Slug)[F(matte, finish)]);
        Assert.Equal((1, false), Values(both, finish.Slug)[F(satin, finish)]);      // satin and dry: p3 (p4 has no skin type)
        Assert.Equal((1, true), Values(both, skin.Slug)[F(dry, skin)]);
        Assert.Equal((1, false), Values(both, skin.Slug)[F(oily, skin)]);           // oily and matte: p2

        // A chosen value that now gives nothing stays so it can be unticked; one nobody could pick is left out.
        var empty = await Facets(shopper, $"q=bare%20{tag}&spec={F(dry, skin)}");
        Assert.Equal((0, true), Values(empty, skin.Slug)[F(dry, skin)]);
        Assert.DoesNotContain(F(oily, skin), Values(empty, skin.Slug).Keys);
        Assert.DoesNotContain(empty, f => f.Attribute == finish.Slug);               // nothing in the results has a finish

        // The brand facet and the specification facets work together.
        var brand = await Read<BrandDto>(await staff.PostAsJsonAsync("/api/v1/brands", new BrandInput { Name = "Spec Brand " + tag, Slug = "spec-brand-" + tag }));
        await staff.PutAsJsonAsync($"/api/v1/products/{p1.Id}/brand", new ProductBrandInput { BrandId = brand.Id });
        Assert.Equal(["Cream one"], await Names($"brand={brand.Slug}&spec={F(matte, finish)}"));
        Assert.Empty(await Names($"brand={brand.Slug}&spec={F(satin, finish)}"));
    }

    [Fact]
    public async Task The_product_page_lists_every_attribute_it_has_values_for_in_order_including_ones_not_offered_as_filters()
    {
        var staff = await factory.CreateAuthenticatedApiClientAsync();
        var shopper = factory.CreateClient();
        var tag = Tag();
        var second = await Read<SpecAttributeDto>(await staff.PostAsJsonAsync("/api/v1/specifications", new SpecAttributeInput { Name = "Zeta " + tag, DisplayOrder = 2, Filterable = false }));
        var first = await Read<SpecAttributeDto>(await staff.PostAsJsonAsync("/api/v1/specifications", new SpecAttributeInput { Name = "Alpha " + tag, DisplayOrder = 1 }));
        var z = await NewValue(staff, second.Id, "Zed");
        var b = await Read<SpecValueDto>(await staff.PostAsJsonAsync($"/api/v1/specifications/{first.Id}/values", new SpecValueInput { Value = "Bee", DisplayOrder = 2 }));
        var a = await Read<SpecValueDto>(await staff.PostAsJsonAsync($"/api/v1/specifications/{first.Id}/values", new SpecValueInput { Value = "Ay", DisplayOrder = 1 }));
        var product = await NewProduct(staff, "Page " + tag);
        var hidden = await NewProduct(staff, "Page hidden " + tag, published: false);
        await SetSpecs(staff, product.Id, z.Id, b.Id, a.Id);
        await SetSpecs(staff, hidden.Id, a.Id);

        var page = await Read<ShopProductDto>(await shopper.GetAsync($"/api/v1/storefront/products/{product.Id}"));
        Assert.Equal(["Alpha " + tag, "Zeta " + tag], page.Specs!.Select(s => s.Name));
        Assert.Equal(["Ay", "Bee"], page.Specs![0].Values);
        Assert.Equal(["Zed"], page.Specs[1].Values);
        Assert.Equal(HttpStatusCode.NotFound, (await shopper.GetAsync($"/api/v1/storefront/products/{hidden.Id}")).StatusCode);
        Assert.Empty((await Read<ShopProductDto>(await shopper.GetAsync($"/api/v1/storefront/products/{(await NewProduct(staff, "Plain " + tag)).Id}"))).Specs!);
    }
}
