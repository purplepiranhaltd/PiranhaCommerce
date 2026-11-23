using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using PiranhaCommerce.Catalog;
using PiranhaCommerce.Core.Paging;
using PiranhaCommerce.Ecommerce;

namespace PiranhaCommerce.IntegrationTests;

/// <summary>Brands: staff manage them and set one on a product, the catalogue import and export carry them, and shoppers filter by them.</summary>
public class BrandTests(PiranhaFactory factory) : IClassFixture<PiranhaFactory>
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };
    private static string Tag() => Guid.NewGuid().ToString("N")[..8];
    private static async Task<T> Read<T>(HttpResponseMessage r) { Assert.True(r.IsSuccessStatusCode, r.StatusCode + " " + await r.Content.ReadAsStringAsync()); return (await r.Content.ReadFromJsonAsync<T>(Json))!; }

    private static async Task<BrandDto> NewBrand(HttpClient staff, string? name = null, string? slug = null, bool active = true)
    {
        var tag = Tag();
        return await Read<BrandDto>(await staff.PostAsJsonAsync("/api/v1/brands", new BrandInput { Name = name ?? "Brand " + tag, Slug = slug ?? "brand-" + tag, Active = active }));
    }

    private static async Task<ProductDto> NewProduct(HttpClient staff, string? name = null, bool published = true, int stock = 5)
    {
        var p = await Read<ProductDto>(await staff.PostAsJsonAsync("/api/v1/products", new ProductInput { Name = name ?? "Brandy " + Tag(), Sku = "BR-" + Tag(), Price = 10m }));
        await staff.PutAsJsonAsync($"/api/v1/products/{p.Id}/listing", new ProductListingInput { Published = published });
        if (stock > 0) await staff.PostAsJsonAsync($"/api/v1/products/{p.Id}/stock/adjustments", new StockAdjustmentInput { Delta = stock, Reason = "Delivery" });
        return p;
    }

    private static Task<HttpResponseMessage> SetBrand(HttpClient staff, Guid productId, Guid? brandId) =>
        staff.PutAsJsonAsync($"/api/v1/products/{productId}/brand", new ProductBrandInput { BrandId = brandId });

    [Fact]
    public async Task Staff_manage_brands_with_unique_names_and_slugs_and_only_staff_can_change_them()
    {
        var staff = await factory.CreateAuthenticatedApiClientAsync();
        var tag = Tag();
        var made = await Read<BrandDto>(await staff.PostAsJsonAsync("/api/v1/brands", new BrandInput { Name = "  Rosewater " + tag + " ", Slug = " Rose-Water-" + tag, DisplayOrder = 3, Description = "Floral." }));
        Assert.Equal("Rosewater " + tag, made.Name);                    // trimmed
        Assert.Equal("rose-water-" + tag, made.Slug);                   // lower-cased for you
        Assert.Equal(0, made.ProductCount);

        // A name is unique whatever its letter case, and so is a slug.
        Assert.Equal(HttpStatusCode.BadRequest, (await staff.PostAsJsonAsync("/api/v1/brands", new BrandInput { Name = "ROSEWATER " + tag })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await staff.PostAsJsonAsync("/api/v1/brands", new BrandInput { Name = "Other " + tag, Slug = "rose-water-" + tag })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await staff.PostAsJsonAsync("/api/v1/brands", new BrandInput { Name = "Odd " + tag, Slug = "Not A Slug!" })).StatusCode);

        Assert.Equal(made.Id, (await Read<BrandDto>(await staff.GetAsync($"/api/v1/brands/by-slug/ROSE-WATER-{tag}"))).Id);
        Assert.Contains(made.Id, (await Read<List<BrandDto>>(await staff.GetAsync("/api/v1/brands"))).Select(b => b.Id));

        // A PUT replaces, so a slug left out is cleared; renaming to its own name in another case is fine.
        var put = await Read<BrandDto>(await staff.PutAsJsonAsync($"/api/v1/brands/{made.Id}", new BrandInput { Name = "ROSEWATER " + tag, Active = false }));
        Assert.Equal(("ROSEWATER " + tag, null, false), (put.Name, put.Slug, put.Active));
        Assert.Equal(HttpStatusCode.NotFound, (await staff.PutAsJsonAsync($"/api/v1/brands/{Guid.NewGuid()}", new BrandInput { Name = "x" })).StatusCode);

        // A brand cannot take another brand's name by an edit.
        var other = await NewBrand(staff);
        Assert.Equal(HttpStatusCode.BadRequest, (await staff.PutAsJsonAsync($"/api/v1/brands/{other.Id}", new BrandInput { Name = "rosewater " + tag, Slug = other.Slug })).StatusCode);

        // Anyone signed out is refused; changing needs write access.
        var anonymous = factory.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.PostAsJsonAsync("/api/v1/brands", new BrandInput { Name = "Nope " + tag })).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("/api/v1/brands")).StatusCode);

        Assert.Equal(HttpStatusCode.NoContent, (await staff.DeleteAsync($"/api/v1/brands/{made.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await staff.DeleteAsync($"/api/v1/brands/{made.Id}")).StatusCode);
    }

    [Fact]
    public async Task A_product_has_one_brand_a_variant_shows_its_parents_and_a_brand_in_use_cannot_be_deleted()
    {
        var staff = await factory.CreateAuthenticatedApiClientAsync();
        var brand = await NewBrand(staff);
        var other = await NewBrand(staff);
        var product = await NewProduct(staff);

        Assert.Null((await Read<ProductBrandDto>(await staff.GetAsync($"/api/v1/products/{product.Id}/brand"))).BrandId);
        var set = await Read<ProductBrandDto>(await SetBrand(staff, product.Id, brand.Id));
        Assert.Equal((brand.Id, brand.Name, false), (set.BrandId, set.BrandName, set.Inherited));
        Assert.Equal(1, (await Read<BrandDto>(await staff.GetAsync($"/api/v1/brands/{brand.Id}"))).ProductCount);

        // Editing the product leaves its brand alone.
        await staff.PutAsJsonAsync($"/api/v1/products/{product.Id}", new ProductInput { Name = product.Name + " renamed", Sku = product.Sku, Price = 11m });
        Assert.Equal(brand.Id, (await Read<ProductBrandDto>(await staff.GetAsync($"/api/v1/products/{product.Id}/brand"))).BrandId);

        // Bad requests change nothing.
        Assert.Equal(HttpStatusCode.BadRequest, (await SetBrand(staff, product.Id, Guid.NewGuid())).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await SetBrand(staff, Guid.NewGuid(), brand.Id)).StatusCode);
        Assert.Equal(brand.Id, (await Read<ProductBrandDto>(await staff.GetAsync($"/api/v1/products/{product.Id}/brand"))).BrandId);

        // A variant shows its parent's brand and cannot be given one.
        Assert.True((await staff.PostAsJsonAsync($"/api/v1/products/{product.Id}/options", new ProductOptionInput { Name = "Size", Values = ["S", "M"] })).IsSuccessStatusCode);
        var variant = (await Read<ProductVariantsDto>(await staff.PostAsJsonAsync($"/api/v1/products/{product.Id}/variants/generate", new GenerateVariantsInput { Price = 10m }))).Variants[0].ProductId;
        var shown = await Read<ProductBrandDto>(await staff.GetAsync($"/api/v1/products/{variant}/brand"));
        Assert.Equal((brand.Id, true), (shown.BrandId, shown.Inherited));
        Assert.Equal(HttpStatusCode.BadRequest, (await SetBrand(staff, variant, other.Id)).StatusCode);
        Assert.Equal(1, (await Read<BrandDto>(await staff.GetAsync($"/api/v1/brands/{brand.Id}"))).ProductCount);   // variants are not counted

        // In use: refused, and the brand is still there. Move the product, and it can go.
        Assert.Equal(HttpStatusCode.Conflict, (await staff.DeleteAsync($"/api/v1/brands/{brand.Id}")).StatusCode);
        await SetBrand(staff, product.Id, other.Id);
        Assert.Equal(HttpStatusCode.NoContent, (await staff.DeleteAsync($"/api/v1/brands/{brand.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await staff.DeleteAsync($"/api/v1/brands/{other.Id}")).StatusCode);
        Assert.Null((await Read<ProductBrandDto>(await SetBrand(staff, product.Id, null))).BrandId);
        Assert.Equal(HttpStatusCode.NoContent, (await staff.DeleteAsync($"/api/v1/brands/{other.Id}")).StatusCode);
    }

    private static async Task<ProductImportResultDto> Import(HttpClient staff, string csv)
    {
        using var form = new MultipartFormDataContent();
        var file = new ByteArrayContent(Encoding.UTF8.GetBytes(csv));
        file.Headers.ContentType = new MediaTypeHeaderValue("text/csv");
        form.Add(file, "file", "products.csv");
        return await Read<ProductImportResultDto>(await staff.PostAsync("/api/v1/products/import", form));
    }

    [Fact]
    public async Task The_catalogue_import_and_export_carry_the_brand_matching_names_in_any_case_and_making_missing_ones()
    {
        var staff = await factory.CreateAuthenticatedApiClientAsync();
        var existing = await NewBrand(staff, "Maison " + Tag());
        var skuA = "IM-" + Tag();
        var skuB = "IM-" + Tag();
        var fresh = "Fresh House " + Tag();

        var done = await Import(staff, $"sku,name,price,brand\n{skuA},Import A,5,{existing.Name.ToUpperInvariant()}\n{skuB},Import B,6,{fresh}\n");
        Assert.Equal((2, 0), (done.Created, done.Failed));

        // The existing brand was matched whatever the case; the new one was made.
        var brands = await Read<List<BrandDto>>(await staff.GetAsync("/api/v1/brands"));
        Assert.Single(brands, b => b.Name == existing.Name);
        Assert.Contains(brands, b => b.Name == fresh);
        var a = (await Read<PagedResult<ProductDto>>(await staff.GetAsync($"/api/v1/products?search={skuA}"))).Items.Single();
        Assert.Equal(existing.Id, (await Read<ProductBrandDto>(await staff.GetAsync($"/api/v1/products/{a.Id}/brand"))).BrandId);

        // It comes out again, on the parent's row only: a variant leaves it blank because it shows its parent's.
        var export = await (await staff.GetAsync($"/api/v1/products/export?search={skuA}")).Content.ReadAsStringAsync();
        var lines = export.TrimStart('﻿').Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Contains("brand", lines[0].Split(','));
        Assert.Contains(existing.Name, lines[1]);

        // A blank cell leaves it alone, [clear] removes it, and a variant row cannot name one.
        await Import(staff, $"sku,price,brand\n{skuA},7,\n");
        Assert.Equal(existing.Id, (await Read<ProductBrandDto>(await staff.GetAsync($"/api/v1/products/{a.Id}/brand"))).BrandId);
        await Import(staff, $"sku,brand\n{skuA},[clear]\n");
        Assert.Null((await Read<ProductBrandDto>(await staff.GetAsync($"/api/v1/products/{a.Id}/brand"))).BrandId);

        var variantSku = "IM-V-" + Tag();
        var bad = await Import(staff, $"sku,parent_sku,name,price,option1_name,option1_value,brand\n{variantSku},{skuA},Import A / S,5,Size,S,{existing.Name}\n");
        Assert.Equal(1, bad.Failed);
        Assert.Contains("brand", string.Join(" ", bad.Results.Single().Errors), StringComparison.OrdinalIgnoreCase);
    }

    private static async Task<PagedResult<ShopProductSummaryDto>> Shop(HttpClient c, string query) =>
        await Read<PagedResult<ShopProductSummaryDto>>(await c.GetAsync("/api/v1/storefront/products?" + query));

    private static async Task<List<ShopFacetDto>> Facets(HttpClient c, string query) =>
        await Read<List<ShopFacetDto>>(await c.GetAsync("/api/v1/storefront/products/facets?" + query));

    [Fact]
    public async Task Shoppers_filter_by_brand_and_each_facet_counts_with_every_other_filter_but_its_own()
    {
        var staff = await factory.CreateAuthenticatedApiClientAsync();
        var shopper = factory.CreateClient();
        var tag = Tag();
        var a = await NewBrand(staff, "Aster " + tag, "aster-" + tag);
        var b = await NewBrand(staff, "Birch " + tag, "birch-" + tag);
        var off = await NewBrand(staff, "Dormant " + tag, "dormant-" + tag, active: false);

        var p1 = await NewProduct(staff, $"Gloss one {tag}");
        var p2 = await NewProduct(staff, $"Gloss two {tag}", stock: 0);          // sold out
        var p3 = await NewProduct(staff, $"Gloss three {tag}");
        var p4 = await NewProduct(staff, $"Gloss none {tag}");                    // no brand
        var p5 = await NewProduct(staff, $"Gloss hidden {tag}", published: false); // not on sale
        var p6 = await NewProduct(staff, $"Gloss dormant {tag}");
        foreach (var (p, brand) in new[] { (p1, a), (p2, a), (p3, b), (p5, b), (p6, off) }) await SetBrand(staff, p.Id, brand.Id);

        // The brands a shopper sees: switched on, with something on sale, counted by what is on sale.
        var brands = await Read<List<ShopBrandDto>>(await shopper.GetAsync("/api/v1/storefront/brands"));
        Assert.Equal(2, brands.Count(x => x.Slug == a.Slug || x.Slug == b.Slug));
        Assert.Equal(2, brands.Single(x => x.Slug == a.Slug).ProductCount);
        Assert.Equal(1, brands.Single(x => x.Slug == b.Slug).ProductCount);      // p5 is not on sale
        Assert.DoesNotContain(brands, x => x.Slug == off.Slug);

        // Filtering: one brand, several (any of them), unknown or switched-off ones (nothing).
        Assert.Equal(2, (await Shop(shopper, $"q={tag}&brand={a.Slug}")).TotalCount);
        Assert.Equal(3, (await Shop(shopper, $"q={tag}&brand={a.Slug}&brand={b.Slug}")).TotalCount);
        Assert.Equal(2, (await Shop(shopper, $"q={tag}&brand={a.Slug}&brand=no-such-brand")).TotalCount);
        Assert.Equal(0, (await Shop(shopper, $"q={tag}&brand=no-such-brand")).TotalCount);
        Assert.Equal(0, (await Shop(shopper, $"q={tag}&brand={off.Slug}")).TotalCount);
        Assert.Equal(5, (await Shop(shopper, $"q={tag}")).TotalCount);          // p1, p2, p3, p4 and p6: p5 is not on sale

        // Facets. Each value is counted with the other filters applied; its own facet's filter is left out, so the alternatives show.
        static Dictionary<string, (int Count, bool Selected)> Brands(List<ShopFacetDto> facets) =>
            facets.Single(f => f.Key == "brand").Values.ToDictionary(v => v.Key, v => (v.Count, v.Selected));

        var all = Brands(await Facets(shopper, $"q={tag}"));
        Assert.Equal((2, false), all[a.Slug!]);
        Assert.Equal((1, false), all[b.Slug!]);                                    // p5 is not on sale
        Assert.DoesNotContain(off.Slug!, all.Keys);                                // a switched-off brand is not offered

        var chosen = Brands(await Facets(shopper, $"q={tag}&brand={a.Slug}"));
        Assert.Equal((2, true), chosen[a.Slug!]);
        Assert.Equal((1, false), chosen[b.Slug!]);                                 // its own filter does not hide the alternative

        var inStock = Brands(await Facets(shopper, $"q={tag}&inStock=true"));      // p2 is sold out
        Assert.Equal((1, false), inStock[a.Slug!]);
        Assert.Equal((1, false), inStock[b.Slug!]);

        var narrowed = Brands(await Facets(shopper, $"q=two%20{tag}"));            // only p2
        Assert.Equal((1, false), narrowed[a.Slug!]);
        Assert.DoesNotContain(b.Slug!, narrowed.Keys);                             // it would give nothing

        var keptWhenEmpty = Brands(await Facets(shopper, $"q=two%20{tag}&brand={b.Slug}"));
        Assert.Equal((0, true), keptWhenEmpty[b.Slug!]);                           // chosen, so it stays, so it can be unticked
        Assert.Equal((1, false), keptWhenEmpty[a.Slug!]);

        // No brand among the results: no brand facet at all.
        Assert.DoesNotContain(await Facets(shopper, $"q=none%20{tag}"), f => f.Key == "brand");
    }

    [Fact]
    public async Task The_brand_is_on_each_product_summary_and_detail_unless_it_is_switched_off()
    {
        var staff = await factory.CreateAuthenticatedApiClientAsync();
        var shopper = factory.CreateClient();
        var tag = Tag();
        var live = await NewBrand(staff, "Live " + tag, "live-" + tag);
        var dormant = await NewBrand(staff, "Quiet " + tag, "quiet-" + tag, active: false);
        var withBrand = await NewProduct(staff, $"Branded {tag}");
        var hiddenBrand = await NewProduct(staff, $"Dormant brand {tag}");
        await SetBrand(staff, withBrand.Id, live.Id);
        await SetBrand(staff, hiddenBrand.Id, dormant.Id);

        var list = (await Shop(shopper, $"q={tag}")).Items;
        Assert.Equal(live.Slug, list.Single(x => x.Name.StartsWith("Branded")).Brand?.Slug);
        Assert.Null(list.Single(x => x.Name.StartsWith("Dormant brand")).Brand);

        var detail = await Read<ShopProductDto>(await shopper.GetAsync($"/api/v1/storefront/products/{withBrand.Id}"));
        Assert.Equal((live.Id, live.Name), (detail.Brand!.Id, detail.Brand.Name));
        Assert.Null((await Read<ShopProductDto>(await shopper.GetAsync($"/api/v1/storefront/products/{hiddenBrand.Id}"))).Brand);
    }
}
