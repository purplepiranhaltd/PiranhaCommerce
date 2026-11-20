using System.Net;
using System.Net.Http.Json;
using PiranhaCommerce.Api.Auth;
using PiranhaCommerce.Core.Paging;
using PiranhaCommerce.Catalog;

namespace PiranhaCommerce.IntegrationTests;

public class ProductApiTests(PiranhaFactory factory) : IClassFixture<PiranhaFactory>
{
    private static ProductInput Input(string name, string? sku = null, decimal price = 10m) =>
        new() { Name = name, Sku = sku, Price = price };

    [Fact]
    public async Task Unauthenticated_request_gets_401_not_a_login_redirect()
    {
        var client = factory.CreateClient(new() { AllowAutoRedirect = false });
        var response = await client.GetAsync("/api/v1/products");
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Token_endpoint_rejects_bad_credentials_and_issues_token_for_good_ones()
    {
        var client = factory.CreateClient();
        var bad = await client.PostAsJsonAsync("/api/v1/auth/token", new TokenRequest(PiranhaFactory.AdminEmail, "wrong-password"));
        Assert.Equal(HttpStatusCode.Unauthorized, bad.StatusCode);

        var good = await client.PostAsJsonAsync("/api/v1/auth/token", new TokenRequest(PiranhaFactory.AdminEmail, PiranhaFactory.AdminPassword));
        Assert.Equal(HttpStatusCode.OK, good.StatusCode);
        var token = await good.Content.ReadFromJsonAsync<TokenResponse>();
        Assert.False(string.IsNullOrEmpty(token?.AccessToken));
    }

    [Fact]
    public async Task Non_admin_can_read_but_not_write()
    {
        var (email, password) = await factory.CreateReadOnlyUserAsync();
        var client = await factory.CreateAuthenticatedApiClientAsync(email, password);

        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/v1/products")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.PostAsJsonAsync("/api/v1/products", Input("Nope"))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.DeleteAsync($"/api/v1/products/{Guid.NewGuid()}")).StatusCode);
    }

    [Fact]
    public async Task Create_get_update_delete_roundtrip()
    {
        var client = await factory.CreateAuthenticatedApiClientAsync();

        var created = await client.PostAsJsonAsync("/api/v1/products", Input("Roundtrip", "RT-1", 5.5m));
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var product = (await created.Content.ReadFromJsonAsync<ProductDto>())!;
        Assert.Equal($"/api/v1/products/{product.Id}", created.Headers.Location?.ToString());

        var fetched = (await client.GetFromJsonAsync<ProductDto>($"/api/v1/products/{product.Id}"))!;
        Assert.Equal("Roundtrip", fetched.Name);
        Assert.Equal(5.5m, fetched.Price);
        Assert.Null(fetched.UpdatedUtc);

        var updated = await client.PutAsJsonAsync($"/api/v1/products/{product.Id}", Input("Roundtrip v2", "RT-1", 7m));
        Assert.Equal(HttpStatusCode.OK, updated.StatusCode);
        var after = (await client.GetFromJsonAsync<ProductDto>($"/api/v1/products/{product.Id}"))!;
        Assert.Equal("Roundtrip v2", after.Name);
        Assert.Equal(product.Id, after.Id);          // update modified the row; it did not insert a new one
        Assert.NotNull(after.UpdatedUtc);

        Assert.Equal(HttpStatusCode.NoContent, (await client.DeleteAsync($"/api/v1/products/{product.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/api/v1/products/{product.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.DeleteAsync($"/api/v1/products/{product.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.PutAsJsonAsync($"/api/v1/products/{product.Id}", Input("x"))).StatusCode);
    }

    [Fact]
    public async Task Invalid_input_returns_400_with_field_errors()
    {
        var client = await factory.CreateAuthenticatedApiClientAsync();
        var response = await client.PostAsJsonAsync("/api/v1/products", Input("", price: -1));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("Name", body);
        Assert.Contains("Price", body);
    }

    [Fact]
    public async Task Duplicate_sku_returns_400()
    {
        var client = await factory.CreateAuthenticatedApiClientAsync();
        var sku = $"DUP-{Guid.NewGuid():N}";
        Assert.Equal(HttpStatusCode.Created, (await client.PostAsJsonAsync("/api/v1/products", Input("First", sku))).StatusCode);

        var second = await client.PostAsJsonAsync("/api/v1/products", Input("Second", sku));
        Assert.Equal(HttpStatusCode.BadRequest, second.StatusCode);
        Assert.Contains("SKU", await second.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task List_is_paged_with_stable_order()
    {
        var client = await factory.CreateAuthenticatedApiClientAsync();
        var tag = Guid.NewGuid().ToString("N")[..8];
        foreach (var n in new[] { "E", "B", "D", "A", "C" })
            Assert.Equal(HttpStatusCode.Created, (await client.PostAsJsonAsync("/api/v1/products", Input($"{tag}-{n}"))).StatusCode);

        var seen = new List<string>();
        for (var page = 1; page <= 3; page++)
        {
            var result = (await client.GetFromJsonAsync<PagedResult<ProductDto>>($"/api/v1/products?search={tag}&page={page}&pageSize=2"))!;
            Assert.Equal(5, result.TotalCount);
            Assert.Equal(3, result.TotalPages);
            Assert.Equal(page == 3 ? 1 : 2, result.Items.Count);
            seen.AddRange(result.Items.Select(i => i.Name));
        }
        Assert.Equal(new[] { "A", "B", "C", "D", "E" }.Select(n => $"{tag}-{n}"), seen);
    }

    [Fact]
    public async Task Oversized_page_size_is_clamped()
    {
        var client = await factory.CreateAuthenticatedApiClientAsync();
        var result = (await client.GetFromJsonAsync<PagedResult<ProductDto>>("/api/v1/products?pageSize=100000&page=-4"))!;
        Assert.Equal(Paging.MaxPageSize, result.PageSize);
        Assert.Equal(1, result.Page);
    }
}
