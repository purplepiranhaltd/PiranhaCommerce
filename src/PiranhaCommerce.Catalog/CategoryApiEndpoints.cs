using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using PiranhaCommerce.Api;

namespace PiranhaCommerce.Catalog;

internal sealed class CategoryApiEndpoints : IApiEndpointContributor
{
    public void MapEndpoints(IEndpointRouteBuilder api)
    {
        var categories = api.MapGroup("/categories").WithTags("Categories");

        categories.MapGet("/", async (ICategoryService svc, CancellationToken ct) => Results.Ok(await svc.ListAsync(ct)));

        categories.MapGet("/{id:guid}", async (Guid id, ICategoryService svc, CancellationToken ct) =>
            await svc.GetAsync(id, ct) is { } c ? Results.Ok(c) : Results.NotFound());

        categories.MapGet("/by-slug/{slug}", async (string slug, ICategoryService svc, CancellationToken ct) =>
            await svc.GetBySlugAsync(slug, ct) is { } c ? Results.Ok(c) : Results.NotFound());

        categories.MapPost("/", async (CategoryInput input, ICategoryService svc, CancellationToken ct) =>
            {
                var created = await svc.CreateAsync(input, ct);
                return Results.Created($"/api/v1/categories/{created.Id}", created);
            })
            .RequireAuthorization(ApiPolicies.Write);

        categories.MapPut("/{id:guid}", async (Guid id, CategoryInput input, ICategoryService svc, CancellationToken ct) =>
                await svc.UpdateAsync(id, input, ct) is { } c ? Results.Ok(c) : Results.NotFound())
            .RequireAuthorization(ApiPolicies.Write);

        categories.MapDelete("/{id:guid}", async (Guid id, ICategoryService svc, CancellationToken ct) =>
                await svc.DeleteAsync(id, ct) ? Results.NoContent() : Results.NotFound())
            .RequireAuthorization(ApiPolicies.Write);

        var productCategories = api.MapGroup("/products/{productId:guid}/categories").WithTags("Product categories");

        productCategories.MapGet("/", async (Guid productId, ICategoryService svc, CancellationToken ct) =>
            await svc.GetProductCategoriesAsync(productId, ct) is { } r ? Results.Ok(r) : Results.NotFound());

        productCategories.MapPut("/", async (Guid productId, ProductCategoriesInput input, ICategoryService svc, CancellationToken ct) =>
                await svc.SetProductCategoriesAsync(productId, input.CategoryIds, ct) is { } r ? Results.Ok(r) : Results.NotFound())
            .RequireAuthorization(ApiPolicies.Write);
    }
}
