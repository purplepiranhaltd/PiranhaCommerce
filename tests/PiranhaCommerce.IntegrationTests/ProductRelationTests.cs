using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using PiranhaCommerce.Catalog;
using PiranhaCommerce.Ecommerce;

namespace PiranhaCommerce.IntegrationTests;

/// <summary>Related and cross-sell products: staff choose them per product, shoppers see only what is on sale, and the basket gets cross-sell suggestions.</summary>
[Collection(OrderSettings.Name)]
public class ProductRelationTests(PiranhaFactory factory) : IClassFixture<PiranhaFactory>
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private static string Tag() => Guid.NewGuid().ToString("N")[..8];
    private static async Task<T> Read<T>(HttpResponseMessage r) { Assert.True(r.IsSuccessStatusCode, r.StatusCode + " " + await r.Content.ReadAsStringAsync()); return (await r.Content.ReadFromJsonAsync<T>(Json))!; }

    private static async Task<ProductDto> Product(HttpClient staff, bool published = true, int stock = 5)
    {
        var p = await Read<ProductDto>(await staff.PostAsJsonAsync("/api/v1/products", new ProductInput { Name = "Rel " + Tag(), Sku = "RL-" + Tag(), Price = 10m }));
        await staff.PutAsJsonAsync($"/api/v1/products/{p.Id}/listing", new ProductListingInput { Published = published });
        if (stock > 0) await staff.PostAsJsonAsync($"/api/v1/products/{p.Id}/stock/adjustments", new StockAdjustmentInput { Delta = stock, Reason = "Delivery" });
        return p;
    }

    private static Task<HttpResponseMessage> Set(HttpClient staff, Guid productId, IEnumerable<Guid> related, IEnumerable<Guid>? crossSell = null) =>
        staff.PutAsJsonAsync($"/api/v1/products/{productId}/relations", new ProductRelationsInput { Related = related.ToList(), CrossSell = (crossSell ?? []).ToList() });

    private static async Task<SuggestionsDto> Suggestions(HttpClient c, Guid id) => await Read<SuggestionsDto>(await c.GetAsync($"/api/v1/storefront/products/{id}/suggestions"));

    [Fact]
    public async Task Staff_set_the_suggestions_in_order_and_a_put_replaces_the_whole_list()
    {
        var staff = await factory.CreateAuthenticatedApiClientAsync();
        var main = await Product(staff);
        var a = await Product(staff);
        var b = await Product(staff);
        var c = await Product(staff);

        var saved = await Read<ProductRelationsDto>(await Set(staff, main.Id, [b.Id, a.Id], [c.Id, a.Id]));
        Assert.Equal([b.Id, a.Id], saved.Related.Select(r => r.ProductId));
        Assert.Equal([c.Id, a.Id], saved.CrossSell.Select(r => r.ProductId)); // a product may be in both lists
        Assert.Equal((b.Name, b.Sku, true), (saved.Related[0].Name, saved.Related[0].Sku, saved.Related[0].Published));

        var replaced = await Read<ProductRelationsDto>(await Set(staff, main.Id, [c.Id]));
        Assert.Equal([c.Id], replaced.Related.Select(r => r.ProductId));
        Assert.Empty(replaced.CrossSell);
        Assert.Equal([c.Id], (await Read<ProductRelationsDto>(await staff.GetAsync($"/api/v1/products/{main.Id}/relations"))).Related.Select(r => r.ProductId));

        // One way only: a is not suggested back.
        Assert.Empty((await Read<ProductRelationsDto>(await staff.GetAsync($"/api/v1/products/{c.Id}/relations"))).Related);
        Assert.Equal(HttpStatusCode.NotFound, (await staff.GetAsync($"/api/v1/products/{Guid.NewGuid()}/relations")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await Set(staff, Guid.NewGuid(), [])).StatusCode);
    }

    [Fact]
    public async Task Bad_suggestions_are_refused_and_nothing_changes()
    {
        var staff = await factory.CreateAuthenticatedApiClientAsync();
        var main = await Product(staff);
        var keep = await Product(staff);
        await Set(staff, main.Id, [keep.Id]);

        Assert.Equal(HttpStatusCode.BadRequest, (await Set(staff, main.Id, [main.Id])).StatusCode);            // itself
        Assert.Equal(HttpStatusCode.BadRequest, (await Set(staff, main.Id, [Guid.NewGuid()])).StatusCode);     // unknown
        Assert.Equal(HttpStatusCode.BadRequest, (await Set(staff, main.Id, [keep.Id, keep.Id])).StatusCode);   // twice in a list
        Assert.Equal(HttpStatusCode.BadRequest, (await Set(staff, main.Id, [], Enumerable.Range(0, 21).Select(_ => Guid.NewGuid()))).StatusCode); // too many

        // A variant can be neither suggested nor given suggestions: they belong to the main product.
        var parent = await Product(staff);
        Assert.True((await staff.PostAsJsonAsync($"/api/v1/products/{parent.Id}/options", new ProductOptionInput { Name = "Size", Values = ["S", "M"] })).IsSuccessStatusCode);
        var set = await Read<ProductVariantsDto>(await staff.PostAsJsonAsync($"/api/v1/products/{parent.Id}/variants/generate", new GenerateVariantsInput { Price = 10m }));
        var variant = set.Variants[0].ProductId;
        Assert.Equal(HttpStatusCode.BadRequest, (await Set(staff, main.Id, [variant])).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await Set(staff, variant, [keep.Id])).StatusCode);

        Assert.Equal([keep.Id], (await Read<ProductRelationsDto>(await staff.GetAsync($"/api/v1/products/{main.Id}/relations"))).Related.Select(r => r.ProductId));
    }

    [Fact]
    public async Task Shoppers_see_only_products_on_sale_in_order_and_whether_each_can_be_bought()
    {
        var staff = await factory.CreateAuthenticatedApiClientAsync();
        var anonymous = factory.CreateClient();
        var main = await Product(staff);
        var inStock = await Product(staff);
        var soldOut = await Product(staff, stock: 0);
        var hidden = await Product(staff, published: false);
        await Set(staff, main.Id, [hidden.Id, soldOut.Id, inStock.Id]);

        var seen = await Suggestions(anonymous, main.Id);
        Assert.Equal([(soldOut.Id, false), (inStock.Id, true)], seen.Related.Select(r => (r.ProductId, r.Available)));
        Assert.DoesNotContain(seen.Related, r => r.ProductId == hidden.Id);
        Assert.Equal((inStock.Name, 10m), (seen.Related[1].Name, seen.Related[1].Price));

        // Staff still see the hidden one, flagged.
        var staffView = await Read<ProductRelationsDto>(await staff.GetAsync($"/api/v1/products/{main.Id}/relations"));
        Assert.False(staffView.Related.Single(r => r.ProductId == hidden.Id).Published);

        // Not on sale itself: nothing to show.
        var offSale = await Product(staff, published: false);
        Assert.Equal(HttpStatusCode.NotFound, (await anonymous.GetAsync($"/api/v1/storefront/products/{offSale.Id}/suggestions")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await anonymous.GetAsync($"/api/v1/storefront/products/{Guid.NewGuid()}/suggestions")).StatusCode);
    }

    [Fact]
    public async Task A_variants_page_shows_its_parents_suggestions()
    {
        var staff = await factory.CreateAuthenticatedApiClientAsync();
        var parent = await Product(staff);
        var other = await Product(staff);
        Assert.True((await staff.PostAsJsonAsync($"/api/v1/products/{parent.Id}/options", new ProductOptionInput { Name = "Size", Values = ["S", "M"] })).IsSuccessStatusCode);
        var set = await Read<ProductVariantsDto>(await staff.PostAsJsonAsync($"/api/v1/products/{parent.Id}/variants/generate", new GenerateVariantsInput { Price = 10m }));
        await Set(staff, parent.Id, [other.Id]);

        var seen = await Suggestions(factory.CreateClient(), set.Variants[0].ProductId);
        Assert.Equal([other.Id], seen.Related.Select(r => r.ProductId));
    }

    [Fact]
    public async Task The_basket_gets_cross_sell_suggestions_without_anything_already_in_it_most_suggested_first()
    {
        var staff = await factory.CreateAuthenticatedApiClientAsync();
        var anonymous = factory.CreateClient();
        var phone = await Product(staff);
        var charger = await Product(staff);
        var caseA = await Product(staff);
        var cable = await Product(staff);
        var hidden = await Product(staff, published: false);
        await Set(staff, phone.Id, [], [caseA.Id, cable.Id, hidden.Id]);
        await Set(staff, charger.Id, [], [cable.Id, phone.Id]);

        string Query(params Guid[] ids) => "/api/v1/storefront/cross-sell?" + string.Join("&", ids.Select(i => "productIds=" + i));

        var both = await Read<List<SuggestedProductDto>>(await anonymous.GetAsync(Query(phone.Id, charger.Id)));
        Assert.Equal([cable.Id, caseA.Id], both.Select(s => s.ProductId)); // cable is suggested by two, the phone is already in the basket, the hidden one is not on sale

        var onlyPhone = await Read<List<SuggestedProductDto>>(await anonymous.GetAsync(Query(phone.Id)));
        Assert.Equal([caseA.Id, cable.Id], onlyPhone.Select(s => s.ProductId)); // the order staff chose

        Assert.Empty(await Read<List<SuggestedProductDto>>(await anonymous.GetAsync(Query(cable.Id))));
        Assert.Empty(await Read<List<SuggestedProductDto>>(await anonymous.GetAsync("/api/v1/storefront/cross-sell")));
    }

    [Fact]
    public async Task Writing_needs_write_access_and_reading_is_open_to_read_only_staff()
    {
        var staff = await factory.CreateAuthenticatedApiClientAsync();
        var main = await Product(staff);
        var (email, password) = await factory.CreateReadOnlyUserAsync();
        var reader = await factory.CreateAuthenticatedApiClientAsync(email, password);

        Assert.Equal(HttpStatusCode.OK, (await reader.GetAsync($"/api/v1/products/{main.Id}/relations")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await Set(reader, main.Id, [])).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await factory.CreateClient().GetAsync($"/api/v1/products/{main.Id}/relations")).StatusCode);
    }
}
