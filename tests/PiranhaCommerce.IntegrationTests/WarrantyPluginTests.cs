using System.Net;
using System.Net.Http.Json;
using PiranhaCommerce.Catalog;
using PiranhaCommerce.Example.Warranty;

namespace PiranhaCommerce.IntegrationTests;

/// <summary>The example plugin lives outside every src package; these tests prove it plugs in through public contracts only.</summary>
public class WarrantyPluginTests(PiranhaCommerceFactory factory) : IClassFixture<PiranhaCommerceFactory>
{
    private static async Task<ProductDto> NewProduct(HttpClient c) =>
        (await (await c.PostAsJsonAsync("/api/v1/products", new ProductInput { Name = "W " + Guid.NewGuid().ToString("N")[..8], Price = 1 }))
            .Content.ReadFromJsonAsync<ProductDto>())!;

    [Fact]
    public async Task Plugin_endpoint_roundtrips_and_defaults_to_no_warranty()
    {
        var c = await factory.CreateAuthenticatedApiClientAsync();
        var p = await NewProduct(c);
        var url = $"/api/v1/products/{p.Id}/warranty";

        Assert.Equal(0, (await c.GetFromJsonAsync<WarrantyDto>(url))!.Months);

        var put = await c.PutAsJsonAsync(url, new WarrantyInput { Months = 24, Terms = "Parts and labour" });
        Assert.Equal(HttpStatusCode.OK, put.StatusCode);
        var got = (await c.GetFromJsonAsync<WarrantyDto>(url))!;
        Assert.Equal(24, got.Months);
        Assert.Equal("Parts and labour", got.Terms);

        await c.PutAsJsonAsync(url, new WarrantyInput { Months = 12 }); // upsert, not a second row
        Assert.Equal(12, (await c.GetFromJsonAsync<WarrantyDto>(url))!.Months);
    }

    [Fact]
    public async Task Plugin_validates_permissions_and_cleans_up_with_the_product()
    {
        var admin = await factory.CreateAuthenticatedApiClientAsync();
        var p = await NewProduct(admin);
        var url = $"/api/v1/products/{p.Id}/warranty";

        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PutAsJsonAsync(url, new WarrantyInput { Months = 601 })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await admin.GetAsync($"/api/v1/products/{Guid.NewGuid()}/warranty")).StatusCode);

        var (email, password) = await factory.CreateReadOnlyUserAsync();
        var reader = await factory.CreateAuthenticatedApiClientAsync(email, password);
        Assert.Equal(HttpStatusCode.OK, (await reader.GetAsync(url)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await reader.PutAsJsonAsync(url, new WarrantyInput { Months = 6 })).StatusCode);

        await admin.PutAsJsonAsync(url, new WarrantyInput { Months = 6 });
        Assert.Equal(HttpStatusCode.NoContent, (await admin.DeleteAsync($"/api/v1/products/{p.Id}")).StatusCode); // FK cascade, no 409/500
    }
}
