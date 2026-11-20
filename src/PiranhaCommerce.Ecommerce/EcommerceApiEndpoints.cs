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

        // Baskets sit behind the same API authorisation as everything else: there is no storefront identity yet, so a storefront
        // backend calls with its own token and keeps the (unguessable) basket id for the shopper. See docs/roadmap.md.
        var baskets = api.MapGroup("/baskets").WithTags("Baskets");

        baskets.MapPost("/", async (BasketCreateInput? input, IBasketService svc, CancellationToken ct) =>
            {
                var created = await svc.CreateAsync(input ?? new BasketCreateInput(), ct);
                return Results.Created($"/api/v1/baskets/{created.Id}", created);
            })
            .RequireAuthorization(ApiPolicies.Write);

        baskets.MapGet("/{id:guid}", async (Guid id, IBasketService svc, CancellationToken ct) =>
            await svc.GetAsync(id, ct) is { } b ? Results.Ok(b) : Results.NotFound());

        baskets.MapPut("/{id:guid}/lines/{productId:guid}", async (Guid id, Guid productId, BasketLineInput input, IBasketService svc, CancellationToken ct) =>
                await svc.SetLineAsync(id, productId, input, ct) is { } b ? Results.Ok(b) : Results.NotFound())
            .RequireAuthorization(ApiPolicies.Write);

        baskets.MapDelete("/{id:guid}/lines/{productId:guid}", async (Guid id, Guid productId, IBasketService svc, CancellationToken ct) =>
                await svc.SetLineAsync(id, productId, new BasketLineInput { Quantity = 0 }, ct) is { } b ? Results.Ok(b) : Results.NotFound())
            .RequireAuthorization(ApiPolicies.Write);

        baskets.MapPost("/{id:guid}/checkout", async (Guid id, CheckoutInput input, IOrderService svc, HttpContext http, CancellationToken ct) =>
                await svc.CheckoutAsync(id, input, http.User.FindFirst("email")?.Value, ct) is { } o ? Results.Ok(o) : Results.NotFound())
            .RequireAuthorization(ApiPolicies.Write);

        var orders = api.MapGroup("/orders").WithTags("Orders");

        orders.MapGet("/", async (int? page, int? pageSize, OrderStage? stage, string? search, IOrderService svc, CancellationToken ct) =>
            Results.Ok(await svc.ListAsync(page, pageSize, stage, search, ct)));

        orders.MapGet("/{id:guid}", async (Guid id, IOrderService svc, CancellationToken ct) =>
            await svc.GetAsync(id, ct) is { } o ? Results.Ok(o) : Results.NotFound());

        orders.MapGet("/by-number/{number:long}", async (long number, IOrderService svc, CancellationToken ct) =>
            await svc.GetByNumberAsync(number, ct) is { } o ? Results.Ok(o) : Results.NotFound());

        orders.MapPost("/{id:guid}/stage", async (Guid id, OrderStageInput input, IOrderService svc, HttpContext http, CancellationToken ct) =>
                await svc.ChangeStageAsync(id, input.Stage, http.User.FindFirst("email")?.Value, ct) is { } o ? Results.Ok(o) : Results.NotFound())
            .RequireAuthorization(ApiPolicies.Write);
    }
}
