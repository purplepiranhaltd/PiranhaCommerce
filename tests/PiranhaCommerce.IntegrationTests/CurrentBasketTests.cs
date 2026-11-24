using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using PiranhaCommerce.Catalog;
using PiranhaCommerce.Customers;
using PiranhaCommerce.Ecommerce;

namespace PiranhaCommerce.IntegrationTests;

/// <summary>A shopper finds their open basket again without remembering its id.</summary>
[Collection(OrderSettings.Name)]
public class CurrentBasketTests(PiranhaCommerceFactory factory) : IClassFixture<PiranhaCommerceFactory>
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() } };

    private async Task<HttpClient> ShopperAsync()
    {
        var client = factory.CreateClient();
        var response = await client.PostAsJsonAsync("/api/v1/storefront/auth/register", new RegisterInput { Email = $"cur-{Guid.NewGuid():N}@example.com", Password = "a-long-enough-password" });
        var registered = (await response.Content.ReadFromJsonAsync<RegisteredDto>(Json))!;
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", registered.Token.AccessToken);
        return client;
    }

    [Fact]
    public async Task The_current_basket_is_the_open_one_used_last_and_belongs_to_the_caller_only()
    {
        var staff = await factory.CreateAuthenticatedApiClientAsync();
        var product = (await (await staff.PostAsJsonAsync("/api/v1/products", new ProductInput { Name = "Current " + Guid.NewGuid().ToString("N")[..6], Sku = "CB-" + Guid.NewGuid().ToString("N")[..6], Price = 5m })).Content.ReadFromJsonAsync<ProductDto>(Json))!;
        await staff.PutAsJsonAsync($"/api/v1/products/{product.Id}/listing", new ProductListingInput { Published = true });

        var me = await ShopperAsync();
        Assert.Equal(HttpStatusCode.NotFound, (await me.GetAsync("/api/v1/storefront/baskets/current")).StatusCode);

        var first = (await (await me.PostAsync("/api/v1/storefront/baskets", null)).Content.ReadFromJsonAsync<BasketDto>(Json))!;
        var second = (await (await me.PostAsync("/api/v1/storefront/baskets", null)).Content.ReadFromJsonAsync<BasketDto>(Json))!;
        Assert.Equal(second.Id, (await me.GetFromJsonAsync<BasketDto>("/api/v1/storefront/baskets/current", Json))!.Id);

        // Touching the older one makes it the current one.
        await me.PutAsJsonAsync($"/api/v1/storefront/baskets/{first.Id}/lines/{product.Id}", new BasketLineInput { Quantity = 1 });
        var current = (await me.GetFromJsonAsync<BasketDto>("/api/v1/storefront/baskets/current", Json))!;
        Assert.Equal(first.Id, current.Id);
        Assert.Single(current.Lines);

        // Someone else never sees it.
        var other = await ShopperAsync();
        Assert.Equal(HttpStatusCode.NotFound, (await other.GetAsync("/api/v1/storefront/baskets/current")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await factory.CreateClient().GetAsync("/api/v1/storefront/baskets/current")).StatusCode);
    }
}
