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
    }
}
