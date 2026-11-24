using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.DependencyInjection;
using PiranhaCommerce.Ecommerce;
using PiranhaCommerce.Web.SampleData;

namespace PiranhaCommerce.IntegrationTests;

/// <summary>The sample shop front's browse pages, fetched as a browser would.</summary>
public class ShopPagesTests(PiranhaCommerceFactory factory) : IClassFixture<PiranhaCommerceFactory>
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };

    private async Task<HttpClient> SeededShopAsync()
    {
        await factory.Services.GetRequiredService<SampleDataSeeder>().SeedAsync();
        return factory.CreateClient();
    }

    /// <summary>The page as text. Blazor writes characters such as the pound sign as entities.</summary>
    private static async Task<string> Page(HttpClient shop, string url) => WebUtility.HtmlDecode(await shop.GetStringAsync(url));

    [Fact]
    public async Task Home_category_search_and_paging_pages_show_the_catalogue()
    {
        var shop = await SeededShopAsync();

        var home = await Page(shop, "/");
        Assert.Contains("Colour you can count on", home);
        Assert.Contains("On offer now", home);            // the eye sale
        Assert.Contains("Lash Lift Mascara", home);

        var lips = await Page(shop, "/shop/lips");
        Assert.Contains("Velvet Matte Lipstick", lips);
        Assert.Contains("Hydra Shine Lip Gloss", lips);   // sub-categories are included
        Assert.DoesNotContain("Lash Lift Mascara", lips);
        Assert.Contains("/shop/lip-gloss", lips);         // and linked as pills

        var found = await Page(shop, "/search?q=serum");
        Assert.Contains("Vitamin C Serum 30ml", found);
        Assert.DoesNotContain("Velvet Matte Lipstick", found);
        Assert.Contains("Nothing found", await Page(shop, "/search?q=zzzz"));

        var firstPage = await Page(shop, "/shop?sort=PriceHighToLow");
        Assert.Contains("Page 1 of 2", firstPage);
        Assert.Contains("page=2", firstPage);
        Assert.Contains("Page 2 of 2", await Page(shop, "/shop?sort=PriceHighToLow&page=2"));

        Assert.Equal(HttpStatusCode.NotFound, (await shop.GetAsync("/shop/not-a-category")).StatusCode);
    }

    [Fact]
    public async Task The_shop_filters_by_brand_with_counts_and_keeps_the_filter_while_paging_and_the_brand_shows_on_cards_and_product_pages()
    {
        var shop = await SeededShopAsync();

        var all = await Page(shop, "/shop");
        Assert.Contains("name=\"brand\" value=\"petalwick\"", all);          // the filter form offers each brand
        Assert.Contains("Marlowe &amp; Finch", await shop.GetStringAsync("/shop")); // encoded in the markup
        Assert.Contains("Fern & Ash", all);

        var eyes = await Page(shop, "/shop?brand=marlowe-and-finch");
        Assert.Contains("Lash Lift Mascara", eyes);
        Assert.DoesNotContain("Velvet Matte Lipstick", eyes);
        Assert.Contains("checked", eyes);
        Assert.Contains("Eye makeup made for long days", eyes);               // one brand: its own heading and description

        var two = await Page(shop, "/shop?brand=marlowe-and-finch&brand=fern-and-ash");
        Assert.Contains("Lash Lift Mascara", two);
        Assert.Contains("Vitamin C Serum 30ml", two);
        Assert.DoesNotContain("Velvet Matte Lipstick", two);

        // The filter survives the page links.
        var paged = await Page(shop, "/shop?brand=petalwick&brand=juniper-row&brand=fern-and-ash&brand=marlowe-and-finch&sort=PriceHighToLow");
        Assert.Contains("Page 1 of 2", paged);
        Assert.Contains("brand=petalwick&brand=juniper-row&brand=fern-and-ash&brand=marlowe-and-finch&page=2", paged);

        // On the product page the brand links back to its listing.
        var product = await Page(shop, "/product/velvet-matte-lipstick");
        Assert.Contains("/shop?brand=petalwick", product);
        Assert.Contains("Petalwick", product);

        Assert.Contains("Nothing found", await Page(shop, "/shop?brand=no-such-brand"));
    }

    [Fact]
    public async Task The_shop_filters_by_specification_and_shows_the_specification_table()
    {
        var shop = await SeededShopAsync();

        var all = await Page(shop, "/shop");
        Assert.Contains("name=\"spec\" value=\"finish:matte\"", all);          // one checkbox group per filterable attribute
        Assert.Contains("name=\"spec\" value=\"skin-type:dry\"", all);
        Assert.DoesNotContain("key-ingredient:", all);                           // shown on products but not offered as a filter

        var dewy = await Page(shop, "/shop?spec=finish:dewy");
        Assert.Contains("Skin Glow Foundation", dewy);
        Assert.Contains("Luminous Highlighter", dewy);
        Assert.DoesNotContain("Velvet Matte Lipstick", dewy);

        var both = await Page(shop, "/shop?spec=finish:dewy&spec=skin-type:dry");   // all of: only the foundation is both
        Assert.Contains("Skin Glow Foundation", both);
        Assert.DoesNotContain("Luminous Highlighter", both);


        var product = await Page(shop, "/product/daily-moisture-cream");
        Assert.Contains("Specifications", product);
        Assert.Contains("Dry, Sensitive", product);
        Assert.Contains("Hyaluronic acid", product);
    }

    [Fact]
    public async Task The_shop_browses_by_tag_with_a_row_of_tags_a_heading_and_tag_links_on_each_product()
    {
        var shop = await SeededShopAsync();

        var all = await Page(shop, "/shop");
        Assert.Contains("Browse by tag", all);
        Assert.Contains("href=\"/shop?tag=bestseller\"", all);

        var bestsellers = await Page(shop, "/shop?tag=bestseller");
        Assert.Contains("Tagged: Bestseller", bestsellers);
        Assert.Contains("Velvet Matte Lipstick", bestsellers);
        Assert.Contains("Daily Moisture Cream", bestsellers);
        Assert.DoesNotContain("Vitamin C Serum", bestsellers);                    // that one is "New in"
        Assert.Contains("name=\"tag\" value=\"bestseller\"", bestsellers);       // the filter form keeps the tag when you change the sort

        var either = await Page(shop, "/shop?tag=bestseller&tag=new-in");
        Assert.Contains("Vitamin C Serum", either);
        Assert.Contains("Velvet Matte Lipstick", either);
        Assert.DoesNotContain("Gentle Cream Cleanser", either);

        // The product page links to each of its tags, and searching finds a tag's name.
        var product = await Page(shop, "/product/velvet-matte-lipstick");
        Assert.Contains("href=\"/shop?tag=long-wearing\"", product);
        Assert.Contains("Gift idea", product);
        var found = await Page(shop, "/search?q=long-wearing");
        Assert.Contains("Velvet Matte Lipstick", found);
        Assert.Contains("Skin Glow Foundation", found);
        Assert.DoesNotContain("Vitamin C Serum", found);

        Assert.Contains("Nothing found", await Page(shop, "/shop?tag=no-such-tag"));
    }

    [Fact]
    public async Task A_product_page_shows_shades_the_chosen_shade_its_price_with_vat_and_what_is_sold_out()
    {
        var shop = await SeededShopAsync();

        var page = await Page(shop, "/product/velvet-matte-lipstick");
        Assert.Contains("Velvet Matte Lipstick | Petalwick", page);   // page title text from the product
        Assert.Contains("£16.80", page);                              // 14.00 + 20% VAT
        Assert.Contains("includes VAT", page);
        Assert.Contains("Shade: <span class=\"chosen\">Ruby Red</span>", page);   // the first shade in stock is chosen
        Assert.Contains("(3 reviews)", page);
        Assert.Contains("You may also like", page);
        Assert.Contains("Satin Cream Lipstick", page);
        Assert.DoesNotContain("Sold out</span>", page.Split("You may also like")[1]); // a suggestion with shades in stock is not "sold out"

        // A link to a shade opens that shade: the one almost gone, and the one that is sold out.
        var product = await shop.GetFromJsonAsync<ShopProductDto>("/api/v1/storefront/products/by-slug/velvet-matte-lipstick", Json);
        var low = product!.Variants.Single(v => v.Left is > 0);
        Assert.Contains($"Only {low.Left} left", await Page(shop, $"/product/velvet-matte-lipstick?variant={low.ProductId}"));
        var gone = product.Variants.Single(v => !v.Available);
        var soldOut = await Page(shop, $"/product/velvet-matte-lipstick?variant={gone.ProductId}");
        Assert.Contains("<p class=\"stock out\">", soldOut);

        // A sale shows the old price, and quantity prices are listed.
        var mascara = await Page(shop, "/product/lash-lift-mascara");
        Assert.Contains("£12.00", mascara);
        Assert.Contains("£15.00", mascara);
        var liner = await Page(shop, "/product/precision-lip-liner");
        Assert.Contains("Buy 3 or more", liner);

        // A product with no shades; and one that does not exist.
        Assert.Contains("In stock", await Page(shop, "/product/gentle-cream-cleanser"));
        Assert.Equal(HttpStatusCode.NotFound, (await shop.GetAsync("/product/nothing-here")).StatusCode);
    }
}
