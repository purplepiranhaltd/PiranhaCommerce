using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using PiranhaCommerce.Api;

namespace PiranhaCommerce.Catalog;

internal sealed class BarcodeApiEndpoints : IApiEndpointContributor
{
    public void MapEndpoints(IEndpointRouteBuilder api)
    {
        // Query string, not a route segment: Code 128 values can contain '/' and '%'.
        api.MapGet("/products/lookup", async (string barcode, IBarcodeService svc, CancellationToken ct) =>
                await svc.FindProductAsync(barcode, ct) is { } p ? Results.Ok(p) : Results.NotFound())
            .WithTags("Barcodes");

        var group = api.MapGroup("/products/{productId:guid}/barcodes").WithTags("Barcodes");

        group.MapGet("/", async (Guid productId, IBarcodeService svc, CancellationToken ct) =>
            await svc.GetAsync(productId, ct) is { } r ? Results.Ok(r) : Results.NotFound());

        group.MapPut("/", async (Guid productId, ProductBarcodesInput input, IBarcodeService svc, CancellationToken ct) =>
                await svc.SetAsync(productId, input.Codes, ct) is { } r ? Results.Ok(r) : Results.NotFound())
            .RequireAuthorization(ApiPolicies.Write);
    }
}
