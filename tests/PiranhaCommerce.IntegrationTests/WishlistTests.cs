using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using PiranhaCommerce.Catalog;
using PiranhaCommerce.Customers;
using PiranhaCommerce.Ecommerce;

namespace PiranhaCommerce.IntegrationTests;

/// <summary>Wishlists: a signed-in shopper saves products to come back to, sees whether they are still on sale and in stock, and staff see what is wished for most.</summary>
[Collection(OrderSettings.Name)]
public class WishlistTests(PiranhaFactory factory) : IClassFixture<PiranhaFactory>
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private static string Tag() => Guid.NewGuid().ToString("N")[..8];
    private static async Task<T> Read<T>(HttpResponseMessage r) { Assert.True(r.IsSuccessStatusCode, r.StatusCode + " " + await r.Content.ReadAsStringAsync()); return (await r.Content.ReadFromJsonAsync<T>(Json))!; }

    private async Task<(HttpClient Client, Guid Id, string Email)> ShopperAsync()
    {
        var email = $"wish-{Tag()}@example.com";
        var client = factory.CreateClient();
        var registered = await Read<RegisteredDto>(await client.PostAsJsonAsync("/api/v1/storefront/auth/register", new RegisterInput { Email = email, Password = "a-long-enough-password" }));
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", registered.Token.AccessToken);
        return (client, registered.Profile.Id, email);
    }

    private static async Task<ProductDto> Product(HttpClient staff, decimal price = 10m, bool published = true, int stock = 5)
    {
        var p = await Read<ProductDto>(await staff.PostAsJsonAsync("/api/v1/products", new ProductInput { Name = "Wish " + Tag(), Sku = "WL-" + Tag(), Price = price }));
        await staff.PutAsJsonAsync($"/api/v1/products/{p.Id}/listing", new ProductListingInput { Published = published });
        if (stock > 0) await staff.PostAsJsonAsync($"/api/v1/products/{p.Id}/stock/adjustments", new StockAdjustmentInput { Delta = stock, Reason = "Delivery" });
        return p;
    }

    private static Task<HttpResponseMessage> Add(HttpClient shopper, Guid productId) =>
        shopper.PostAsJsonAsync("/api/v1/storefront/wishlist", new WishlistAddInput { ProductId = productId });

    private static async Task<List<WishlistItemDto>> List(HttpClient shopper) => await Read<List<WishlistItemDto>>(await shopper.GetAsync("/api/v1/storefront/wishlist"));

    [Fact]
    public async Task A_shopper_saves_products_and_removes_them_and_saving_twice_keeps_one()
    {
        var staff = await factory.CreateAuthenticatedApiClientAsync();
        var a = await Product(staff, 12.50m);
        var b = await Product(staff, 3m);
        var shopper = await ShopperAsync();

        Assert.Empty(await List(shopper.Client));
        var added = await Read<WishlistItemDto>(await Add(shopper.Client, a.Id));
        Assert.Equal((a.Name, 12.50m, 12.50m, true, true), (added.Name, added.Price, added.PriceWhenAdded, added.Published, added.Available));
        Assert.Equal(HttpStatusCode.OK, (await Add(shopper.Client, a.Id)).StatusCode); // already there: quiet success
        await Add(shopper.Client, b.Id);

        Assert.Equal([b.Id, a.Id], (await List(shopper.Client)).Select(i => i.ProductId)); // newest first

        Assert.Equal(HttpStatusCode.NoContent, (await shopper.Client.DeleteAsync($"/api/v1/storefront/wishlist/{a.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await shopper.Client.DeleteAsync($"/api/v1/storefront/wishlist/{a.Id}")).StatusCode);
        Assert.Equal([b.Id], (await List(shopper.Client)).Select(i => i.ProductId));
        Assert.Equal(HttpStatusCode.NoContent, (await shopper.Client.DeleteAsync("/api/v1/storefront/wishlist")).StatusCode);
        Assert.Empty(await List(shopper.Client));
    }

    [Fact]
    public async Task Each_shopper_has_their_own_list_and_it_needs_a_signed_in_shopper()
    {
        var staff = await factory.CreateAuthenticatedApiClientAsync();
        var p = await Product(staff);
        var ada = await ShopperAsync();
        var bob = await ShopperAsync();
        await Add(ada.Client, p.Id);

        Assert.Empty(await List(bob.Client));
        Assert.Equal(HttpStatusCode.NotFound, (await bob.Client.DeleteAsync($"/api/v1/storefront/wishlist/{p.Id}")).StatusCode);
        Assert.Single(await List(ada.Client));

        var anonymous = factory.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("/api/v1/storefront/wishlist")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await Add(anonymous, p.Id)).StatusCode);
    }

    [Fact]
    public async Task Only_products_on_sale_can_be_saved_and_a_saved_one_shows_when_it_goes_off_sale_or_sells_out()
    {
        var staff = await factory.CreateAuthenticatedApiClientAsync();
        var hidden = await Product(staff, published: false);
        var onSale = await Product(staff);
        var shopper = await ShopperAsync();

        Assert.Equal(HttpStatusCode.BadRequest, (await Add(shopper.Client, hidden.Id)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await Add(shopper.Client, Guid.NewGuid())).StatusCode);

        await Add(shopper.Client, onSale.Id);
        Assert.True((await List(shopper.Client)).Single().Available);

        // Sold out: still saved, no longer available.
        Assert.True((await staff.PostAsJsonAsync($"/api/v1/products/{onSale.Id}/stock/adjustments", new StockAdjustmentInput { Delta = -5, Reason = "Count" })).IsSuccessStatusCode);
        var soldOut = (await List(shopper.Client)).Single();
        Assert.Equal((true, false), (soldOut.Published, soldOut.Available));

        // Taken off sale: still saved, flagged.
        await staff.PutAsJsonAsync($"/api/v1/products/{onSale.Id}/listing", new ProductListingInput { Published = false });
        var off = (await List(shopper.Client)).Single();
        Assert.Equal((false, false), (off.Published, off.Available));
    }

    [Fact]
    public async Task The_price_when_saved_is_kept_so_a_change_shows()
    {
        var staff = await factory.CreateAuthenticatedApiClientAsync();
        var p = await Product(staff, 20m);
        var shopper = await ShopperAsync();
        await Add(shopper.Client, p.Id);

        Assert.True((await staff.PutAsJsonAsync($"/api/v1/products/{p.Id}", new ProductInput { Name = p.Name, Sku = p.Sku, Price = 15m })).IsSuccessStatusCode);
        var item = (await List(shopper.Client)).Single();
        Assert.Equal((15m, 20m), (item.Price, item.PriceWhenAdded));
    }

    [Fact]
    public async Task A_wishlist_holds_a_limited_number_of_products()
    {
        var staff = await factory.CreateAuthenticatedApiClientAsync();
        var shopper = await ShopperAsync();
        var extra = await Product(staff);

        // Fill the list straight through the database: making 200 products over the API would be slow for no extra coverage.
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<PiranhaCommerce.Data.PiranhaDbContext>();
            for (var i = 0; i < 200; i++)
            {
                var product = new Product { Name = "Fill " + Tag() };
                db.Add(product);
                db.Add(new WishlistItem { CustomerId = shopper.Id, ProductId = product.Id, AddedUtc = DateTimeOffset.UtcNow });
            }
            await db.SaveChangesAsync();
        }

        var full = await Add(shopper.Client, extra.Id);
        Assert.Equal(HttpStatusCode.Conflict, full.StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await shopper.Client.DeleteAsync("/api/v1/storefront/wishlist")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await Add(shopper.Client, extra.Id)).StatusCode);
    }

    [Fact]
    public async Task Staff_see_what_is_wished_for_most_and_reading_is_open_to_read_only_staff()
    {
        var staff = await factory.CreateAuthenticatedApiClientAsync();
        var popular = await Product(staff);
        var quiet = await Product(staff);
        var ada = await ShopperAsync();
        var bob = await ShopperAsync();
        var cy = await ShopperAsync();
        foreach (var s in new[] { ada, bob, cy }) await Add(s.Client, popular.Id);
        await Add(ada.Client, quiet.Id);

        var demand = await Read<List<WishlistDemandDto>>(await staff.GetAsync("/api/v1/wishlists/demand"));
        var mine = demand.Where(d => d.ProductId == popular.Id || d.ProductId == quiet.Id).ToList();
        Assert.Equal([(popular.Id, 3), (quiet.Id, 1)], mine.Select(d => (d.ProductId, d.Wishlists)));

        var (email, password) = await factory.CreateReadOnlyUserAsync();
        var reader = await factory.CreateAuthenticatedApiClientAsync(email, password);
        Assert.Equal(HttpStatusCode.OK, (await reader.GetAsync("/api/v1/wishlists/demand")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await factory.CreateClient().GetAsync("/api/v1/wishlists/demand")).StatusCode);
    }

    [Fact]
    public async Task The_wishlist_is_exported_with_the_customer_and_erased_with_them()
    {
        var staff = await factory.CreateAuthenticatedApiClientAsync();
        var p = await Product(staff);
        var shopper = await ShopperAsync();
        await Add(shopper.Client, p.Id);

        var export = await (await staff.GetAsync($"/api/v1/customers/{shopper.Id}/export")).Content.ReadAsStringAsync();
        Assert.Contains("\"wishlist\"", export);
        Assert.Contains(p.Id.ToString(), export);

        Assert.Equal(HttpStatusCode.OK, (await staff.PostAsJsonAsync($"/api/v1/customers/{shopper.Id}/erase", new EraseCustomerInput { ConfirmEmail = shopper.Email })).StatusCode);
        var demand = await Read<List<WishlistDemandDto>>(await staff.GetAsync("/api/v1/wishlists/demand"));
        Assert.DoesNotContain(demand, d => d.ProductId == p.Id);
    }
}
