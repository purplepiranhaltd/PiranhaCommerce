using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using PiranhaCommerce.Catalog;
using PiranhaCommerce.Core.Paging;
using PiranhaCommerce.Customers;
using PiranhaCommerce.Ecommerce;

namespace PiranhaCommerce.IntegrationTests;

/// <summary>What a shopper sees of the catalogue: published products and active categories only, priced the way baskets price them.</summary>
public class ShopCatalogueTests(PiranhaFactory factory) : IClassFixture<PiranhaFactory>
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };
    private static string Tag() => Guid.NewGuid().ToString("N")[..8];
    private static async Task<T> Read<T>(HttpResponseMessage r) { Assert.True(r.IsSuccessStatusCode, r.StatusCode + " " + await r.Content.ReadAsStringAsync()); return (await r.Content.ReadFromJsonAsync<T>(Json))!; }

    private static async Task<ProductDto> Product(HttpClient staff, string tag, string name = "Item", decimal price = 10m, bool published = true, int stock = 5, string? description = null, string? slug = null)
    {
        var p = await Read<ProductDto>(await staff.PostAsJsonAsync("/api/v1/products", new ProductInput { Name = $"{name} {tag}", Sku = $"SH-{Tag()}", Price = price, Description = description, Slug = slug }));
        await staff.PutAsJsonAsync($"/api/v1/products/{p.Id}/listing", new ProductListingInput { Published = published });
        if (stock > 0) await staff.PostAsJsonAsync($"/api/v1/products/{p.Id}/stock/adjustments", new StockAdjustmentInput { Delta = stock, Reason = "Delivery" });
        return p;
    }

    private static async Task<CategoryDto> Category(HttpClient staff, string name, Guid? parent = null, bool active = true, string? slug = null) =>
        await Read<CategoryDto>(await staff.PostAsJsonAsync("/api/v1/categories", new CategoryInput { Name = name, ParentId = parent, Active = active, Slug = slug }));

    private static Task<HttpResponseMessage> Put(HttpClient staff, Guid product, params Guid[] categories) =>
        staff.PutAsJsonAsync($"/api/v1/products/{product}/categories", new ProductCategoriesInput { CategoryIds = categories.ToList() });

    private static async Task<PagedResult<ShopProductSummaryDto>> List(HttpClient c, string query) =>
        await Read<PagedResult<ShopProductSummaryDto>>(await c.GetAsync("/api/v1/storefront/products?" + query));

    private static ShopCategoryDto? Find(IEnumerable<ShopCategoryDto> tree, Guid id) =>
        tree.Select(c => c.Id == id ? c : Find(c.Children, id)).FirstOrDefault(c => c is not null);

    [Fact]
    public async Task The_category_tree_has_only_active_categories_and_counts_products_on_sale_including_sub_categories()
    {
        var staff = await factory.CreateAuthenticatedApiClientAsync();
        var anon = factory.CreateClient();
        var tag = Tag();
        var root = await Category(staff, "Lips " + tag);
        var child = await Category(staff, "Lipsticks " + tag, root.Id);
        var off = await Category(staff, "Hidden " + tag, active: false);
        var underOff = await Category(staff, "Under hidden " + tag, off.Id);
        var onSale = await Product(staff, tag);
        var unpublished = await Product(staff, tag, published: false);
        await Put(staff, onSale.Id, child.Id, underOff.Id);
        await Put(staff, unpublished.Id, child.Id);

        var tree = await Read<List<ShopCategoryDto>>(await anon.GetAsync("/api/v1/storefront/categories"));
        var found = Find(tree, root.Id)!;
        Assert.Equal(1, found.ProductCount);                        // the unpublished product is not counted
        var kid = Assert.Single(found.Children);
        Assert.Equal((child.Id, 1), (kid.Id, kid.ProductCount));
        Assert.Null(Find(tree, off.Id));                             // switched off
        Assert.Null(Find(tree, underOff.Id));                        // and so is everything under it
    }

    [Fact]
    public async Task Listings_show_only_published_products_find_by_words_and_filter_by_category_with_sub_categories()
    {
        var staff = await factory.CreateAuthenticatedApiClientAsync();
        var anon = factory.CreateClient();
        var tag = Tag();
        var root = await Category(staff, "Face " + tag, slug: "face-" + tag);
        var child = await Category(staff, "Blush " + tag, root.Id);
        var other = await Category(staff, "Eyes " + tag);
        var rose = await Product(staff, tag, "Rose blush", 12m, description: "A soft powder.");
        var peach = await Product(staff, tag, "Peach blush", 15m);
        var mascara = await Product(staff, tag, "Mascara", 9m);
        var hidden = await Product(staff, tag, "Secret blush", 8m, published: false);
        await Put(staff, rose.Id, child.Id);
        await Put(staff, peach.Id, root.Id);
        await Put(staff, mascara.Id, other.Id);
        await Put(staff, hidden.Id, child.Id);

        var all = await List(anon, $"q={tag}");
        Assert.Equal(3, all.TotalCount);                              // not the unpublished one
        Assert.DoesNotContain(all.Items, i => i.Id == hidden.Id);

        // Words match in any letter case, in the name, the SKU or the description; every word must be there.
        Assert.Equal([peach.Id, rose.Id], (await List(anon, $"q=BLUSH+{tag.ToUpperInvariant()}")).Items.Select(i => i.Id));
        Assert.Equal([rose.Id], (await List(anon, $"q=powder+{tag}")).Items.Select(i => i.Id));
        Assert.Equal([rose.Id], (await List(anon, $"q={rose.Sku!.ToLowerInvariant()}")).Items.Select(i => i.Id));

        // A category includes what is in its sub-categories, by id or by slug.
        Assert.Equal([peach.Id, rose.Id], (await List(anon, $"categoryId={root.Id}")).Items.Select(i => i.Id));
        Assert.Equal([peach.Id, rose.Id], (await List(anon, $"category=face-{tag}")).Items.Select(i => i.Id));
        Assert.Equal([rose.Id], (await List(anon, $"categoryId={child.Id}")).Items.Select(i => i.Id));
        Assert.Equal(0, (await List(anon, $"categoryId={Guid.NewGuid()}")).TotalCount);

        // Sorting, price limits and paging.
        Assert.Equal([mascara.Id, rose.Id, peach.Id], (await List(anon, $"q={tag}&sort=PriceLowToHigh")).Items.Select(i => i.Id));
        Assert.Equal([peach.Id, rose.Id, mascara.Id], (await List(anon, $"q={tag}&sort=PriceHighToLow")).Items.Select(i => i.Id));
        Assert.Equal([rose.Id], (await List(anon, $"q={tag}&minPrice=10&maxPrice=13")).Items.Select(i => i.Id));
        var page2 = await List(anon, $"q={tag}&sort=PriceLowToHigh&page=2&pageSize=2");
        Assert.Equal((3, 2), (page2.TotalCount, page2.Page));
        Assert.Equal([peach.Id], page2.Items.Select(i => i.Id));
    }

    [Fact]
    public async Task A_product_shows_whether_it_can_be_bought_and_how_many_are_left_when_few_are()
    {
        var staff = await factory.CreateAuthenticatedApiClientAsync();
        var anon = factory.CreateClient();
        var tag = Tag();
        var plenty = await Product(staff, tag, "Plenty", stock: 50);
        var few = await Product(staff, tag, "Few", stock: 3);
        var none = await Product(staff, tag, "None", stock: 0);

        var items = (await List(anon, $"q={tag}&sort=Name")).Items.ToDictionary(i => i.Id);
        Assert.Equal((true, (int?)null), (items[plenty.Id].Available, items[plenty.Id].Left));
        Assert.Equal((true, (int?)3), (items[few.Id].Available, items[few.Id].Left));
        Assert.False(items[none.Id].Available);

        var inStock = (await List(anon, $"q={tag}&inStock=true")).Items.Select(i => i.Id).Order().ToList();
        Assert.Equal(new[] { few.Id, plenty.Id }.Order().ToList(), inStock);
    }

    [Fact]
    public async Task A_product_with_options_shows_its_variants_and_the_cheapest_price_and_is_found_by_any_of_them()
    {
        var staff = await factory.CreateAuthenticatedApiClientAsync();
        var anon = factory.CreateClient();
        var tag = Tag();
        var parent = await Product(staff, tag, "Lipstick", 12m, stock: 0, slug: "lipstick-" + tag, description: "Creamy colour.\nLong wearing.");
        Assert.True((await staff.PostAsJsonAsync($"/api/v1/products/{parent.Id}/options", new ProductOptionInput { Name = "Shade", Values = ["Ruby", "Coral", "Nude"] })).IsSuccessStatusCode);
        var set = await Read<ProductVariantsDto>(await staff.PostAsJsonAsync($"/api/v1/products/{parent.Id}/variants/generate", new GenerateVariantsInput { Price = 12m }));
        var ruby = set.Variants.First(v => v.Values[0].Value == "Ruby").ProductId;
        var coral = set.Variants.First(v => v.Values[0].Value == "Coral").ProductId;
        var nude = set.Variants.First(v => v.Values[0].Value == "Nude").ProductId;
        await staff.PutAsJsonAsync($"/api/v1/products/{coral}", new ProductInput { Name = "Lipstick " + tag + " - Coral", Sku = "CORAL-" + tag, Price = 9m });
        await staff.PostAsJsonAsync($"/api/v1/products/{coral}/stock/adjustments", new StockAdjustmentInput { Delta = 2, Reason = "Delivery" });
        await staff.PutAsJsonAsync($"/api/v1/products/{nude}/listing", new ProductListingInput { Published = false });

        // Listed once, through its parent, with the cheapest price and "available" because one shade is.
        var list = await List(anon, $"q={tag}");
        var item = Assert.Single(list.Items);
        Assert.Equal((parent.Id, true, true, 9m), (item.Id, item.HasVariants, item.Available, item.Price.Price));
        Assert.Equal("Creamy colour.", item.Summary);
        Assert.Equal((int?)null, item.Left); // the count is only given for a single product

        var detail = await Read<ShopProductDto>(await anon.GetAsync($"/api/v1/storefront/products/{parent.Id}"));
        var option = Assert.Single(detail.Options);
        Assert.Equal(["Ruby", "Coral", "Nude"], option.Values.Select(v => v.Value));
        Assert.Equal(["Ruby", "Coral"], detail.Variants.Select(v => v.Values[0].Value)); // the unpublished shade is not offered, the order is the option's order
        Assert.Equal([(false, 12m), (true, 9m)], detail.Variants.Select(v => (v.Available, v.Price.Price)));
        Assert.Null(detail.SelectedVariantId);

        // Any of its variants (or its address) finds the same page.
        var viaVariant = await Read<ShopProductDto>(await anon.GetAsync($"/api/v1/storefront/products/{ruby}"));
        Assert.Equal((parent.Id, ruby), (viaVariant.Id, viaVariant.SelectedVariantId));
        var viaSlug = await Read<ShopProductDto>(await anon.GetAsync($"/api/v1/storefront/products/by-slug/LIPSTICK-{tag}"));
        Assert.Equal(parent.Id, viaSlug.Id);
        Assert.Equal(HttpStatusCode.NotFound, (await anon.GetAsync($"/api/v1/storefront/products/by-slug/nothing-{tag}")).StatusCode);
    }

    [Fact]
    public async Task Unpublished_and_unknown_products_are_not_found()
    {
        var staff = await factory.CreateAuthenticatedApiClientAsync();
        var anon = factory.CreateClient();
        var hidden = await Product(staff, Tag(), published: false, slug: "hidden-" + Tag());
        Assert.Equal(HttpStatusCode.NotFound, (await anon.GetAsync($"/api/v1/storefront/products/{hidden.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await anon.GetAsync($"/api/v1/storefront/products/{Guid.NewGuid()}")).StatusCode);
    }

    [Fact]
    public async Task Prices_are_what_the_shopper_pays_a_sale_for_everyone_and_group_prices_for_a_signed_in_member()
    {
        var staff = await factory.CreateAuthenticatedApiClientAsync();
        var anon = factory.CreateClient();
        var tag = Tag();
        var onSale = await Product(staff, tag, "Sale item", 20m);
        var plain = await Product(staff, tag, "Plain item", 20m);

        var starts = DateTimeOffset.UtcNow.AddDays(-1);
        var sale = await Read<SaleDto>(await staff.PostAsJsonAsync("/api/v1/sales", new SaleInput { Name = "Sale " + tag, StartsUtc = starts, EndsUtc = starts.AddDays(5), Percent = 25m }, Json));
        Assert.True((await staff.PutAsJsonAsync($"/api/v1/sales/{sale.Id}/products/{onSale.Id}", new SaleProductInput(), Json)).IsSuccessStatusCode);

        var seen = (await List(anon, $"q={tag}")).Items.ToDictionary(i => i.Id);
        Assert.Equal((15m, 20m, true), (seen[onSale.Id].Price.Price, seen[onSale.Id].Price.RegularPrice, seen[onSale.Id].Price.Discounted));
        Assert.Equal((20m, false), (seen[plain.Id].Price.Price, seen[plain.Id].Price.Discounted));

        // A member of a group with 10% off pays that only when signed in.
        var group = await Read<CustomerGroupDto>(await staff.PostAsJsonAsync("/api/v1/customer-groups", new CustomerGroupInput { Name = "Trade " + tag, DiscountPercent = 10m }));
        var email = $"shop-{tag}@example.com";
        var shopper = factory.CreateClient();
        var registered = await Read<RegisteredDto>(await shopper.PostAsJsonAsync("/api/v1/storefront/auth/register", new RegisterInput { Email = email, Password = "a-long-enough-password" }));
        shopper.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", registered.Token.AccessToken);
        Assert.True((await staff.PutAsync($"/api/v1/customer-groups/{group.Id}/members/{registered.Profile.Id}", null)).IsSuccessStatusCode);

        Assert.Equal(18m, (await Read<ShopProductDto>(await shopper.GetAsync($"/api/v1/storefront/products/{plain.Id}"))).Price.Price);
        Assert.Equal(20m, (await Read<ShopProductDto>(await anon.GetAsync($"/api/v1/storefront/products/{plain.Id}"))).Price.Price);
        Assert.Equal(15m, (await Read<ShopProductDto>(await shopper.GetAsync($"/api/v1/storefront/products/{onSale.Id}"))).Price.Price); // the lowest of the sale and the group price

        // Wishlists and suggestions use the same price, so they agree with the product page.
        Assert.True((await shopper.PostAsJsonAsync("/api/v1/storefront/wishlist", new WishlistAddInput { ProductId = onSale.Id })).IsSuccessStatusCode);
        var wish = (await Read<List<WishlistItemDto>>(await shopper.GetAsync("/api/v1/storefront/wishlist"))).Single();
        Assert.Equal((15m, 20m), (wish.Price, wish.RegularPrice));
        await staff.PutAsJsonAsync($"/api/v1/products/{plain.Id}/relations", new ProductRelationsInput { Related = [onSale.Id] });
        var suggestions = await Read<SuggestionsDto>(await anon.GetAsync($"/api/v1/storefront/products/{plain.Id}/suggestions"));
        Assert.Equal((15m, 20m), (suggestions.Related[0].Price, suggestions.Related[0].RegularPrice));
    }

    [Fact]
    public async Task Pictures_are_listed_in_order_with_the_main_one_on_the_summary()
    {
        var staff = await factory.CreateAuthenticatedApiClientAsync();
        var anon = factory.CreateClient();
        var tag = Tag();
        var p = await Product(staff, tag);
        var first = await Read<ProductImageDto>(await staff.PostAsync($"/api/v1/products/{p.Id}/images", ImageTests.Form(ImageTests.Png(200, 100), "a.png", alt: "Front")));
        var second = await Read<ProductImageDto>(await staff.PostAsync($"/api/v1/products/{p.Id}/images", ImageTests.Form(ImageTests.Png(200, 100), "b.png", alt: "Back")));

        var summary = Assert.Single((await List(anon, $"q={tag}")).Items);
        Assert.Equal((first.Id, "Front"), (summary.Image!.Id, summary.Image.AltText));
        var detail = await Read<ShopProductDto>(await anon.GetAsync($"/api/v1/storefront/products/{p.Id}"));
        Assert.Equal([first.Id, second.Id], detail.Images.Select(i => i.Id));
        Assert.Equal(HttpStatusCode.OK, (await anon.GetAsync(detail.Images[0].Url + "?width=100")).StatusCode);
    }

    [Fact]
    public async Task Review_totals_and_the_categories_a_product_is_in_come_with_it()
    {
        var staff = await factory.CreateAuthenticatedApiClientAsync();
        var anon = factory.CreateClient();
        var tag = Tag();
        var cat = await Category(staff, "Cat " + tag, slug: "cat-" + tag);
        var p = await Product(staff, tag);
        await Put(staff, p.Id, cat.Id);

        var reviewer = factory.CreateClient();
        var registered = await Read<RegisteredDto>(await reviewer.PostAsJsonAsync("/api/v1/storefront/auth/register", new RegisterInput { Email = $"rv-{tag}@example.com", Password = "a-long-enough-password" }));
        reviewer.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", registered.Token.AccessToken);
        var review = await Read<ReviewDto>(await reviewer.PostAsJsonAsync("/api/v1/storefront/reviews", new ReviewInput { ProductId = p.Id, Rating = 4 }, Json));
        Assert.True((await staff.PutAsJsonAsync($"/api/v1/reviews/{review.Id}/status", new ReviewStatusInput { Status = ReviewStatus.Approved }, Json)).IsSuccessStatusCode);

        var summary = Assert.Single((await List(anon, $"q={tag}")).Items);
        Assert.Equal((1, 4m), (summary.ReviewCount, summary.ReviewAverage));
        var detail = await Read<ShopProductDto>(await anon.GetAsync($"/api/v1/storefront/products/{p.Id}"));
        Assert.Equal((1, 4m), (detail.Reviews.Count, detail.Reviews.Average));
        Assert.Equal([cat.Id], detail.Categories.Select(c => c.Id));
    }
}
