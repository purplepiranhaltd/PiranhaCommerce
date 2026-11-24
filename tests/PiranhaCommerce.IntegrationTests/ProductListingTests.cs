using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using PiranhaCommerce.Catalog;
using PiranhaCommerce.Core.Admin;
using PiranhaCommerce.Data;
using PiranhaCommerce.Ecommerce;

namespace PiranhaCommerce.IntegrationTests;

/// <summary>Ecommerce data layered on the shared catalogue product.</summary>
public class ProductListingTests(PiranhaCommerceFactory factory) : IClassFixture<PiranhaCommerceFactory>
{
    private async Task<(HttpClient Client, ProductDto Product)> NewProductAsync()
    {
        var client = await factory.CreateAuthenticatedApiClientAsync();
        var response = await client.PostAsJsonAsync("/api/v1/products", new ProductInput { Name = "Listed", Price = 3m });
        response.EnsureSuccessStatusCode();
        return (client, (await response.Content.ReadFromJsonAsync<ProductDto>())!);
    }

    [Fact]
    public async Task Product_with_no_listing_is_unpublished_then_can_be_published()
    {
        var (client, product) = await NewProductAsync();
        var url = $"/api/v1/products/{product.Id}/listing";

        Assert.False((await client.GetFromJsonAsync<ProductListingDto>(url))!.Published);

        Assert.Equal(HttpStatusCode.OK, (await client.PutAsJsonAsync(url, new ProductListingInput { Published = true })).StatusCode);
        Assert.True((await client.GetFromJsonAsync<ProductListingDto>(url))!.Published);

        Assert.Equal(HttpStatusCode.OK, (await client.PutAsJsonAsync(url, new ProductListingInput { Published = false })).StatusCode);
        Assert.False((await client.GetFromJsonAsync<ProductListingDto>(url))!.Published);
    }

    [Fact]
    public async Task Listing_for_unknown_product_is_404()
    {
        var client = await factory.CreateAuthenticatedApiClientAsync();
        var url = $"/api/v1/products/{Guid.NewGuid()}/listing";
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync(url)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.PutAsJsonAsync(url, new ProductListingInput { Published = true })).StatusCode);
    }

    [Fact]
    public async Task Listing_cannot_be_changed_by_non_admins()
    {
        var (_, product) = await NewProductAsync();
        var (email, password) = await factory.CreateReadOnlyUserAsync();
        var viewer = await factory.CreateAuthenticatedApiClientAsync(email, password);
        var response = await viewer.PutAsJsonAsync($"/api/v1/products/{product.Id}/listing", new ProductListingInput { Published = true });
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Deleting_a_product_removes_its_listing()
    {
        var (client, product) = await NewProductAsync();
        await client.PutAsJsonAsync($"/api/v1/products/{product.Id}/listing", new ProductListingInput { Published = true });

        Assert.Equal(HttpStatusCode.NoContent, (await client.DeleteAsync($"/api/v1/products/{product.Id}")).StatusCode);

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PiranhaCommerceDbContext>();
        Assert.False(await db.Set<ProductListing>().AnyAsync(l => l.ProductId == product.Id));
    }

    [Fact]
    public void Ecommerce_panel_is_registered_in_the_product_edit_slot()
    {
        var items = factory.Services.GetServices<IAdminSlotContributor>().SelectMany(c => c.GetItems());
        Assert.Contains(items, w => w.Slot == AdminSlots.ProductEditPanels && w.ComponentType.Name == "ProductListingPanel");
    }
}
