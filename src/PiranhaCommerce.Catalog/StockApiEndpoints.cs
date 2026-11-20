using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using PiranhaCommerce.Api;

namespace PiranhaCommerce.Catalog;

internal sealed class StockApiEndpoints : IApiEndpointContributor
{
    public void MapEndpoints(IEndpointRouteBuilder api)
    {
        var group = api.MapGroup("/products/{productId:guid}/stock").WithTags("Stock");

        group.MapGet("/", async (Guid productId, IStockService svc, CancellationToken ct) =>
            await svc.GetLevelAsync(productId, ct) is { } l ? Results.Ok(l) : Results.NotFound());

        group.MapGet("/movements", async (Guid productId, int? page, int? pageSize, IStockService svc, CancellationToken ct) =>
            await svc.ListMovementsAsync(productId, page, pageSize, ct) is { } r ? Results.Ok(r) : Results.NotFound());

        group.MapPost("/adjustments", async (Guid productId, StockAdjustmentInput input, HttpContext http, IStockService svc, CancellationToken ct) =>
                await svc.AdjustAsync(productId, input, http.User.FindFirst("email")?.Value ?? http.User.Identity?.Name, ct) is { } r ? Results.Ok(r) : Results.NotFound())
            .RequireAuthorization(ApiPolicies.Write);

        // Products below zero at any location: the "recount needed" report.
        api.MapGet("/stock/negative", async (IStockService svc, CancellationToken ct) => Results.Ok(await svc.ListNegativeAsync(ct)))
            .WithTags("Stock");

        // Locations are deactivated rather than deleted: the ledger refers to them.
        var locations = api.MapGroup("/stock-locations").WithTags("Stock locations");

        locations.MapGet("/", async (IStockLocationService svc, CancellationToken ct) => Results.Ok(await svc.ListAsync(ct)));

        locations.MapGet("/{id:guid}", async (Guid id, IStockLocationService svc, CancellationToken ct) =>
            await svc.GetAsync(id, ct) is { } l ? Results.Ok(l) : Results.NotFound());

        locations.MapPost("/", async (StockLocationInput input, IStockLocationService svc, CancellationToken ct) =>
            {
                var created = await svc.CreateAsync(input, ct);
                return Results.Created($"/api/v1/stock-locations/{created.Id}", created);
            })
            .RequireAuthorization(ApiPolicies.Write);

        locations.MapPut("/{id:guid}", async (Guid id, StockLocationInput input, IStockLocationService svc, CancellationToken ct) =>
                await svc.UpdateAsync(id, input, ct) is { } l ? Results.Ok(l) : Results.NotFound())
            .RequireAuthorization(ApiPolicies.Write);
    }
}
