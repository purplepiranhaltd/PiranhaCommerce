using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using PiranhaCommerce.Api;
using PiranhaCommerce.Core.Validation;
using PiranhaCommerce.Catalog;

namespace PiranhaCommerce.Catalog;

internal sealed class CatalogApiEndpoints : IApiEndpointContributor
{
    public void MapEndpoints(IEndpointRouteBuilder api)
    {
        var products = api.MapGroup("/products").WithTags("Products");

        products.MapGet("/", async (IProductService svc, int? page, int? pageSize, string? search, CancellationToken ct) =>
            Results.Ok(await svc.ListAsync(page, pageSize, search, ct)));

        products.MapGet("/{id:guid}", async (Guid id, IProductService svc, CancellationToken ct) =>
            await svc.GetAsync(id, ct) is { } p ? Results.Ok(p) : Results.NotFound());

        products.MapPost("/", async (ProductInput input, IProductService svc, CancellationToken ct) =>
            {
                try
                {
                    var created = await svc.CreateAsync(input, ct);
                    return Results.Created($"/api/v1/products/{created.Id}", created);
                }
                catch (PiranhaValidationException ex) { return Results.ValidationProblem(ex.Errors.ToDictionary(k => k.Key, k => k.Value)); }
            })
            .RequireAuthorization(ApiPolicies.Write);

        products.MapPut("/{id:guid}", async (Guid id, ProductInput input, IProductService svc, CancellationToken ct) =>
            {
                try { return await svc.UpdateAsync(id, input, ct) is { } p ? Results.Ok(p) : Results.NotFound(); }
                catch (PiranhaValidationException ex) { return Results.ValidationProblem(ex.Errors.ToDictionary(k => k.Key, k => k.Value)); }
            })
            .RequireAuthorization(ApiPolicies.Write);

        products.MapDelete("/{id:guid}", async (Guid id, IProductService svc, CancellationToken ct) =>
                await svc.DeleteAsync(id, ct) ? Results.NoContent() : Results.NotFound())
            .RequireAuthorization(ApiPolicies.Write);
    }
}
