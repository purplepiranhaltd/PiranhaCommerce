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

/// <summary>Tags: free-form labels staff put on products (making them as they go), that shoppers browse and search by.</summary>
public class TagTests(PiranhaCommerceFactory factory) : IClassFixture<PiranhaCommerceFactory>
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };
    private static string Tag() => Guid.NewGuid().ToString("N")[..8];
    private static async Task<T> Read<T>(HttpResponseMessage r) { Assert.True(r.IsSuccessStatusCode, r.StatusCode + " " + await r.Content.ReadAsStringAsync()); return (await r.Content.ReadFromJsonAsync<T>(Json))!; }

    private static async Task<ProductDto> NewProduct(HttpClient staff, string name, bool published = true, int stock = 5)
    {
        var p = await Read<ProductDto>(await staff.PostAsJsonAsync("/api/v1/products", new ProductInput { Name = name, Sku = "TG-" + Tag(), Price = 10m }));
        await staff.PutAsJsonAsync($"/api/v1/products/{p.Id}/listing", new ProductListingInput { Published = published });
        if (stock > 0) await staff.PostAsJsonAsync($"/api/v1/products/{p.Id}/stock/adjustments", new StockAdjustmentInput { Delta = stock, Reason = "Delivery" });
        return p;
    }

    private static Task<HttpResponseMessage> SetTags(HttpClient staff, Guid productId, params string[] names) =>
        staff.PutAsJsonAsync($"/api/v1/products/{productId}/tags", new ProductTagsInput { Tags = [.. names] });

    private static async Task<List<string>> TagsOf(HttpClient staff, Guid productId) =>
        (await Read<ProductTagsDto>(await staff.GetAsync($"/api/v1/products/{productId}/tags"))).Tags.Select(t => t.Name).ToList();

    [Fact]
    public async Task Naming_a_tag_on_a_product_makes_it_names_match_in_any_case_and_a_put_replaces_the_set()
    {
        var staff = await factory.CreateAuthenticatedApiClientAsync();
        var tag = Tag();
        var product = await NewProduct(staff, "Tagged " + tag);

        var set = await Read<ProductTagsDto>(await SetTags(staff, product.Id, $"  Summer {tag} ", $"Gift idea {tag}", $"SUMMER {tag}", "", "  "));
        Assert.Equal([$"Gift idea {tag}", $"Summer {tag}"], set.Tags.Select(t => t.Name));          // trimmed, a repeat in another case is the same tag, blanks dropped
        Assert.Equal([$"gift-idea-{tag}", $"summer-{tag}"], set.Tags.Select(t => t.Slug));           // slugs made from the names

        // Naming it again in another case reuses it rather than making a second.
        var other = await NewProduct(staff, "Other " + tag);
        await SetTags(staff, other.Id, $"summer {tag}");
        var all = await Read<List<TagDto>>(await staff.GetAsync("/api/v1/tags"));
        Assert.Single(all, t => t.Name == $"Summer {tag}");
        Assert.Equal(2, all.Single(t => t.Name == $"Summer {tag}").ProductCount);

        // A PUT replaces the whole set; an empty list clears it.
        Assert.Equal([$"Gift idea {tag}"], (await Read<ProductTagsDto>(await SetTags(staff, product.Id, $"Gift idea {tag}"))).Tags.Select(t => t.Name));
        Assert.Empty((await Read<ProductTagsDto>(await SetTags(staff, product.Id))).Tags);
        Assert.Equal(1, (await Read<List<TagDto>>(await staff.GetAsync("/api/v1/tags"))).Single(t => t.Name == $"Summer {tag}").ProductCount);

        // Bad requests change nothing.
        await SetTags(staff, product.Id, $"Keep {tag}");
        Assert.Equal(HttpStatusCode.BadRequest, (await SetTags(staff, product.Id, new string('x', 51))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await SetTags(staff, product.Id, Enumerable.Range(0, 51).Select(i => $"T{i} {tag}").ToArray())).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await SetTags(staff, product.Id, "???")).StatusCode);                                   // nothing to make an address from
        Assert.Equal(HttpStatusCode.NotFound, (await SetTags(staff, Guid.NewGuid(), "x")).StatusCode);
        Assert.Equal([$"Keep {tag}"], await TagsOf(staff, product.Id));

        // Two names that would share an address are refused, and the product is left alone.
        await SetTags(staff, other.Id, $"C++ {tag}");
        var clash = await SetTags(staff, product.Id, $"C-- {tag}");   // "c-tag" both ways
        Assert.Equal(HttpStatusCode.BadRequest, clash.StatusCode);
        Assert.Equal([$"Keep {tag}"], await TagsOf(staff, product.Id));
    }

    [Fact]
    public async Task Staff_rename_tags_everywhere_and_deleting_one_only_takes_the_label_off_and_only_staff_can_change_them()
    {
        var staff = await factory.CreateAuthenticatedApiClientAsync();
        var tag = Tag();
        var made = await Read<TagDto>(await staff.PostAsJsonAsync("/api/v1/tags", new TagInput { Name = $" Vegan {tag} " }));
        Assert.Equal(($"Vegan {tag}", $"vegan-{tag}", 0), (made.Name, made.Slug, made.ProductCount));
        Assert.Equal(HttpStatusCode.BadRequest, (await staff.PostAsJsonAsync("/api/v1/tags", new TagInput { Name = $"VEGAN {tag}" })).StatusCode);                      // name, any case
        Assert.Equal(HttpStatusCode.BadRequest, (await staff.PostAsJsonAsync("/api/v1/tags", new TagInput { Name = "Other " + tag, Slug = made.Slug })).StatusCode);      // slug
        Assert.Equal(HttpStatusCode.BadRequest, (await staff.PostAsJsonAsync("/api/v1/tags", new TagInput { Name = "Bad " + tag, Slug = "Not A Slug!" })).StatusCode);

        var product = await NewProduct(staff, "Renamed " + tag);
        await SetTags(staff, product.Id, made.Name);

        var renamed = await Read<TagDto>(await staff.PutAsJsonAsync($"/api/v1/tags/{made.Id}", new TagInput { Name = $"Plant based {tag}" }));
        Assert.Equal(($"Plant based {tag}", $"plant-based-{tag}", 1), (renamed.Name, renamed.Slug, renamed.ProductCount));    // the slug follows the name when left out
        Assert.Equal([$"Plant based {tag}"], await TagsOf(staff, product.Id));
        Assert.Equal(made.Id, (await Read<TagDto>(await staff.GetAsync($"/api/v1/tags/by-slug/{renamed.Slug.ToUpperInvariant()}"))).Id);
        Assert.Equal(HttpStatusCode.NotFound, (await staff.PutAsJsonAsync($"/api/v1/tags/{Guid.NewGuid()}", new TagInput { Name = "x" })).StatusCode);

        var anonymous = factory.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("/api/v1/tags")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.PostAsJsonAsync("/api/v1/tags", new TagInput { Name = "Nope " + tag })).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.DeleteAsync($"/api/v1/tags/{made.Id}")).StatusCode);

        // A tag is only a label: deleting it takes it off its products rather than refusing.
        Assert.Equal(HttpStatusCode.NoContent, (await staff.DeleteAsync($"/api/v1/tags/{made.Id}")).StatusCode);
        Assert.Empty(await TagsOf(staff, product.Id));
        Assert.Equal(HttpStatusCode.NotFound, (await staff.DeleteAsync($"/api/v1/tags/{made.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await staff.GetAsync($"/api/v1/tags/{made.Id}")).StatusCode);
    }

    [Fact]
    public async Task A_variant_shows_its_parents_tags_and_cannot_be_given_any_and_a_deleted_product_leaves_no_trace()
    {
        var staff = await factory.CreateAuthenticatedApiClientAsync();
        var tag = Tag();
        var parent = await NewProduct(staff, "Parent " + tag);
        await SetTags(staff, parent.Id, $"Shared {tag}");
        Assert.True((await staff.PostAsJsonAsync($"/api/v1/products/{parent.Id}/options", new ProductOptionInput { Name = "Size", Values = ["S", "M"] })).IsSuccessStatusCode);
        var variant = (await Read<ProductVariantsDto>(await staff.PostAsJsonAsync($"/api/v1/products/{parent.Id}/variants/generate", new GenerateVariantsInput { Price = 10m }))).Variants[0].ProductId;

        var shown = await Read<ProductTagsDto>(await staff.GetAsync($"/api/v1/products/{variant}/tags"));
        Assert.True(shown.Inherited);
        Assert.Equal([$"Shared {tag}"], shown.Tags.Select(t => t.Name));
        Assert.Equal(HttpStatusCode.BadRequest, (await SetTags(staff, variant, "Mine")).StatusCode);
        Assert.Equal(1, (await Read<List<TagDto>>(await staff.GetAsync("/api/v1/tags"))).Single(t => t.Name == $"Shared {tag}").ProductCount);   // variants are not counted

        var lone = await NewProduct(staff, "Lone " + tag, stock: 0);                  // a product with stock history cannot be deleted
        await SetTags(staff, lone.Id, $"Shared {tag}");
        Assert.Equal(2, (await Read<List<TagDto>>(await staff.GetAsync("/api/v1/tags"))).Single(t => t.Name == $"Shared {tag}").ProductCount);
        Assert.Equal(HttpStatusCode.NoContent, (await staff.DeleteAsync($"/api/v1/products/{lone.Id}")).StatusCode);
        Assert.Equal(1, (await Read<List<TagDto>>(await staff.GetAsync("/api/v1/tags"))).Single(t => t.Name == $"Shared {tag}").ProductCount);
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
    public async Task The_catalogue_import_and_export_carry_tags_making_missing_ones_like_brands()
    {
        var staff = await factory.CreateAuthenticatedApiClientAsync();
        var tag = Tag();
        var sku = "IT-" + Tag();

        var done = await Import(staff, $"sku,name,price,tags\n{sku},Imported {tag},5,\"Summer {tag} | gift idea {tag} | SUMMER {tag}\"\n");
        Assert.Equal((1, 0), (done.Created, done.Failed));
        var product = (await Read<PagedResult<ProductDto>>(await staff.GetAsync($"/api/v1/products?search={sku}"))).Items.Single();
        Assert.Equal([$"gift idea {tag}", $"Summer {tag}"], await TagsOf(staff, product.Id));       // both made, the repeat folded in

        var export = await (await staff.GetAsync($"/api/v1/products/export?search={sku}")).Content.ReadAsStringAsync();
        Assert.Contains("tags", export.Split('\n')[0].Split(','));
        Assert.Contains($"gift idea {tag} | Summer {tag}", export);                                 // by name

        // A blank cell leaves them alone, a list replaces the set, and [clear] removes them all.
        await Import(staff, $"sku,price,tags\n{sku},6,\n");
        Assert.Equal(2, (await TagsOf(staff, product.Id)).Count);
        await Import(staff, $"sku,tags\n{sku},Only {tag}\n");
        Assert.Equal([$"Only {tag}"], await TagsOf(staff, product.Id));
        await Import(staff, $"sku,tags\n{sku},[clear]\n");
        Assert.Empty(await TagsOf(staff, product.Id));

        // A variant row cannot carry them.
        var variant = await Import(staff, $"sku,parent_sku,name,price,option1_name,option1_value,tags\n{sku}-S,{sku},Imported S,5,Size,S,\"Nope {tag}\"\n");
        Assert.Equal(1, variant.Failed);
        Assert.Contains("tags", string.Join(" ", variant.Results.Single().Errors), StringComparison.OrdinalIgnoreCase);
    }

    private static async Task<PagedResult<ShopProductSummaryDto>> Shop(HttpClient c, string query) =>
        await Read<PagedResult<ShopProductSummaryDto>>(await c.GetAsync("/api/v1/storefront/products?" + query));

    [Fact]
    public async Task Shoppers_browse_by_tag_any_of_several_and_search_finds_tag_names_and_the_product_page_lists_its_tags()
    {
        var staff = await factory.CreateAuthenticatedApiClientAsync();
        var shopper = factory.CreateClient();
        var tag = Tag();
        var summer = $"Summer-{tag}";
        var gift = $"Gift-{tag}";
        var p1 = await NewProduct(staff, $"Alpha {tag}");
        var p2 = await NewProduct(staff, $"Beta {tag}");
        var p3 = await NewProduct(staff, $"Gamma {tag}");
        var hidden = await NewProduct(staff, $"Hidden {tag}", published: false);
        await SetTags(staff, p1.Id, summer);
        await SetTags(staff, p2.Id, summer, gift);
        await SetTags(staff, hidden.Id, gift);
        async Task<List<string>> Names(string query) => (await Shop(shopper, $"{query}")).Items.Select(i => i.Name.Replace($" {tag}", "")).Order().ToList();

        Assert.Equal(["Alpha", "Beta"], await Names($"tag=summer-{tag.ToLowerInvariant()}"));
        Assert.Equal(["Beta"], await Names($"tag=gift-{tag.ToLowerInvariant()}"));                              // the draft is not shown
        Assert.Equal(["Alpha", "Beta"], await Names($"tag=summer-{tag.ToLowerInvariant()}&tag=gift-{tag.ToLowerInvariant()}"));   // any of
        Assert.Equal(["Beta"], await Names($"tag=gift-{tag.ToLowerInvariant()}&tag=nope-{tag.ToLowerInvariant()}"));              // unknown slugs are ignored...
        Assert.Empty(await Names($"tag=nope-{tag.ToLowerInvariant()}"));                                       // ...unless none are known
        Assert.Equal(["Beta"], await Names($"q={tag}&tag=gift-{tag.ToLowerInvariant()}"));                       // and it combines with other filters

        // The words of a tag are searchable, so "gift" finds products tagged Gift even when the name never says so.
        Assert.Equal(["Beta"], await Names($"q=gift-{tag.ToLowerInvariant()}"));
        Assert.Empty(await Names($"q=gift-{tag.ToLowerInvariant()}%20zzz"));

        // The tag list for a shop front: only tags with something on sale, counted by what is on sale.
        var tags = await Read<List<ShopTagDto>>(await shopper.GetAsync("/api/v1/storefront/tags"));
        Assert.Equal(2, tags.Single(t => t.Name == summer).ProductCount);
        Assert.Equal(1, tags.Single(t => t.Name == gift).ProductCount);

        // Each product page lists its tags; a product with none has an empty list.
        var page = await Read<ShopProductDto>(await shopper.GetAsync($"/api/v1/storefront/products/{p2.Id}"));
        Assert.Equal([gift, summer], page.Tags!.Select(t => t.Name));
        Assert.Empty((await Read<ShopProductDto>(await shopper.GetAsync($"/api/v1/storefront/products/{p3.Id}"))).Tags!);
    }
}
