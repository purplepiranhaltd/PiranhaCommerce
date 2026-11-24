using System.Net;
using System.Net.Http.Json;
using PiranhaCommerce.Catalog;

namespace PiranhaCommerce.IntegrationTests;

public class CategoryTests(PiranhaCommerceFactory factory) : IClassFixture<PiranhaCommerceFactory>
{
    private static string Tag() => Guid.NewGuid().ToString("N")[..8];

    private static async Task<CategoryDto> Create(HttpClient c, string name, Guid? parent = null)
    {
        var r = await c.PostAsJsonAsync("/api/v1/categories", new CategoryInput { Name = name, ParentId = parent });
        Assert.Equal(HttpStatusCode.Created, r.StatusCode);
        return (await r.Content.ReadFromJsonAsync<CategoryDto>())!;
    }

    [Fact]
    public async Task Create_nested_list_update_and_delete()
    {
        var c = await factory.CreateAuthenticatedApiClientAsync();
        var root = await Create(c, "Root " + Tag());
        var child = await Create(c, "Child " + Tag(), root.Id);
        Assert.Equal(root.Id, child.ParentId);

        var list = (await c.GetFromJsonAsync<List<CategoryDto>>("/api/v1/categories"))!;
        Assert.Contains(list, x => x.Id == child.Id && x.ParentId == root.Id);

        var upd = await c.PutAsJsonAsync($"/api/v1/categories/{child.Id}", new CategoryInput { Name = "Renamed", ParentId = root.Id });
        Assert.Equal("Renamed", (await upd.Content.ReadFromJsonAsync<CategoryDto>())!.Name);

        Assert.Equal(HttpStatusCode.NoContent, (await c.DeleteAsync($"/api/v1/categories/{child.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await c.GetAsync($"/api/v1/categories/{child.Id}")).StatusCode);
    }

    [Fact]
    public async Task Cycles_and_unknown_parents_are_rejected()
    {
        var c = await factory.CreateAuthenticatedApiClientAsync();
        var a = await Create(c, "A " + Tag());
        var b = await Create(c, "B " + Tag(), a.Id);
        var d = await Create(c, "D " + Tag(), b.Id);

        // a -> under its own grandchild, itself, or a missing parent
        foreach (var parent in new[] { d.Id, a.Id, Guid.NewGuid() })
        {
            var r = await c.PutAsJsonAsync($"/api/v1/categories/{a.Id}", new CategoryInput { Name = a.Name, ParentId = parent });
            Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);
        }
    }

    [Fact]
    public async Task Deleting_a_category_with_children_is_a_conflict()
    {
        var c = await factory.CreateAuthenticatedApiClientAsync();
        var parent = await Create(c, "P " + Tag());
        var child = await Create(c, "C " + Tag(), parent.Id);

        Assert.Equal(HttpStatusCode.Conflict, (await c.DeleteAsync($"/api/v1/categories/{parent.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await c.DeleteAsync($"/api/v1/categories/{child.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await c.DeleteAsync($"/api/v1/categories/{parent.Id}")).StatusCode);
    }

    [Fact]
    public async Task Product_categories_are_replaced_as_a_set_and_validated()
    {
        var c = await factory.CreateAuthenticatedApiClientAsync();
        var one = await Create(c, "One " + Tag());
        var two = await Create(c, "Two " + Tag());
        var product = (await (await c.PostAsJsonAsync("/api/v1/products", new ProductInput { Name = "PC " + Tag(), Price = 1 }))
            .Content.ReadFromJsonAsync<ProductDto>())!;
        var url = $"/api/v1/products/{product.Id}/categories";

        var set = await c.PutAsJsonAsync(url, new ProductCategoriesInput { CategoryIds = [one.Id, two.Id] });
        Assert.Equal(2, (await set.Content.ReadFromJsonAsync<ProductCategoriesDto>())!.CategoryIds.Count);

        await c.PutAsJsonAsync(url, new ProductCategoriesInput { CategoryIds = [two.Id] });
        Assert.Equal([two.Id], (await c.GetFromJsonAsync<ProductCategoriesDto>(url))!.CategoryIds);

        var bad = await c.PutAsJsonAsync(url, new ProductCategoriesInput { CategoryIds = [one.Id, Guid.NewGuid()] });
        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);
        Assert.Equal([two.Id], (await c.GetFromJsonAsync<ProductCategoriesDto>(url))!.CategoryIds); // unchanged

        Assert.Equal(HttpStatusCode.NotFound, (await c.PutAsJsonAsync($"/api/v1/products/{Guid.NewGuid()}/categories", new ProductCategoriesInput())).StatusCode);

        // deleting the category or the product removes the link rows
        Assert.Equal(HttpStatusCode.NoContent, (await c.DeleteAsync($"/api/v1/categories/{two.Id}")).StatusCode);
        Assert.Empty((await c.GetFromJsonAsync<ProductCategoriesDto>(url))!.CategoryIds);
        await c.PutAsJsonAsync(url, new ProductCategoriesInput { CategoryIds = [one.Id] });
        Assert.Equal(HttpStatusCode.NoContent, (await c.DeleteAsync($"/api/v1/products/{product.Id}")).StatusCode);
    }

    [Fact]
    public void Tree_flattening_orders_depth_first()
    {
        CategoryDto N(Guid id, Guid? p, string n, int o = 0) => new(id, n, null, p, o, true, default, null);
        Guid a = Guid.NewGuid(), b = Guid.NewGuid(), a1 = Guid.NewGuid();
        var flat = CategoryTree.Flatten([N(a1, a, "A1"), N(b, null, "B", 1), N(a, null, "A", 0)]);
        Assert.Equal(["A", "A1", "B"], flat.Select(x => x.Category.Name));
        Assert.Equal([0, 1, 0], flat.Select(x => x.Depth));
        Assert.Equal(new HashSet<Guid> { a, a1 }, CategoryTree.SelfAndDescendants([N(a1, a, "A1"), N(a, null, "A"), N(b, null, "B")], a));
    }
}
