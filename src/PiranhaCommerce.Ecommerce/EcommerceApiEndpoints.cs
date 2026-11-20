using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using PiranhaCommerce.Api;

namespace PiranhaCommerce.Ecommerce;

internal sealed class EcommerceApiEndpoints : IApiEndpointContributor
{
    public void MapEndpoints(IEndpointRouteBuilder api)
    {
        var listing = api.MapGroup("/products/{productId:guid}/listing").WithTags("Product listings");

        listing.MapGet("/", async (Guid productId, IProductListingService svc, CancellationToken ct) =>
            await svc.GetAsync(productId, ct) is { } l ? Results.Ok(l) : Results.NotFound());

        listing.MapPut("/", async (Guid productId, ProductListingInput input, IProductListingService svc, CancellationToken ct) =>
                await svc.SetAsync(productId, input, ct) is { } l ? Results.Ok(l) : Results.NotFound())
            .RequireAuthorization(ApiPolicies.Write);
    }
}
