using System.Security.Claims;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using PiranhaCommerce.Api;
using PiranhaCommerce.Customers;

namespace PiranhaCommerce.Ecommerce;

/// <summary>
/// A signed-in shopper's baskets and orders. The customer is always the one in the token: nothing the caller sends can name a
/// different customer, and a basket or order that belongs to someone else answers 404, exactly as one that does not exist.
/// </summary>
internal sealed class EcommerceStorefrontEndpoints : IStorefrontEndpointContributor
{
    public void MapEndpoints(IEndpointRouteBuilder storefront)
    {
        var baskets = storefront.MapGroup("/baskets").WithTags("Storefront baskets").AddEndpointFilter<ActiveCustomerFilter>();

        baskets.MapPost("/", async (ClaimsPrincipal user, IBasketService svc, CancellationToken ct) =>
        {
            var created = await svc.CreateAsync(new BasketCreateInput { CustomerId = user.CustomerId() }, ct);
            return Results.Created($"/api/v1/storefront/baskets/{created.Id}", created);
        });

        baskets.MapGet("/{id:guid}", async (Guid id, ClaimsPrincipal user, IBasketService svc, CancellationToken ct) =>
            await svc.IsOwnedByAsync(id, user.CustomerId()!.Value, ct) && await svc.GetAsync(id, ct) is { } b ? Results.Ok(b) : Results.NotFound());

        baskets.MapPost("/{id:guid}/claim", async (Guid id, ClaimsPrincipal user, IBasketService svc, CancellationToken ct) =>
            await svc.ClaimAsync(id, user.CustomerId()!.Value, ct) && await svc.GetAsync(id, ct) is { } b ? Results.Ok(b) : Results.NotFound());

        baskets.MapPut("/{id:guid}/lines/{productId:guid}", async (Guid id, Guid productId, BasketLineInput input, ClaimsPrincipal user, IBasketService svc, CancellationToken ct) =>
            await svc.IsOwnedByAsync(id, user.CustomerId()!.Value, ct) && await svc.SetLineAsync(id, productId, input, ct) is { } b ? Results.Ok(b) : Results.NotFound());

        baskets.MapDelete("/{id:guid}/lines/{productId:guid}", async (Guid id, Guid productId, ClaimsPrincipal user, IBasketService svc, CancellationToken ct) =>
            await svc.IsOwnedByAsync(id, user.CustomerId()!.Value, ct) && await svc.SetLineAsync(id, productId, new BasketLineInput { Quantity = 0 }, ct) is { } b
                ? Results.Ok(b) : Results.NotFound());

        baskets.MapPost("/{id:guid}/promo", async (Guid id, CodeInput input, ClaimsPrincipal user, IBasketService svc, CancellationToken ct) =>
            await svc.IsOwnedByAsync(id, user.CustomerId()!.Value, ct) && await svc.SetPromotionAsync(id, input.Code, ct) is { } b ? Results.Ok(b) : Results.NotFound());

        baskets.MapDelete("/{id:guid}/promo", async (Guid id, ClaimsPrincipal user, IBasketService svc, CancellationToken ct) =>
            await svc.IsOwnedByAsync(id, user.CustomerId()!.Value, ct) && await svc.SetPromotionAsync(id, null, ct) is { } b ? Results.Ok(b) : Results.NotFound());

        baskets.MapPost("/{id:guid}/voucher", async (Guid id, CodeInput input, ClaimsPrincipal user, IBasketService svc, CancellationToken ct) =>
            await svc.IsOwnedByAsync(id, user.CustomerId()!.Value, ct) && await svc.SetVoucherAsync(id, input.Code, ct) is { } b ? Results.Ok(b) : Results.NotFound());

        baskets.MapDelete("/{id:guid}/voucher", async (Guid id, ClaimsPrincipal user, IBasketService svc, CancellationToken ct) =>
            await svc.IsOwnedByAsync(id, user.CustomerId()!.Value, ct) && await svc.SetVoucherAsync(id, null, ct) is { } b ? Results.Ok(b) : Results.NotFound());

        baskets.MapGet("/{id:guid}/shipping-options", async (Guid id, string countryCode, ClaimsPrincipal user, IBasketService svc, CancellationToken ct) =>
            await svc.IsOwnedByAsync(id, user.CustomerId()!.Value, ct) && await svc.ShippingOptionsAsync(id, countryCode, ct) is { } o ? Results.Ok(o) : Results.NotFound());

        baskets.MapPost("/{id:guid}/quote", async (Guid id, QuoteInput input, ClaimsPrincipal user, IBasketService svc, CancellationToken ct) =>
            await svc.IsOwnedByAsync(id, user.CustomerId()!.Value, ct) && await svc.QuoteAsync(id, input, ct) is { } q ? Results.Ok(q) : Results.NotFound());

        baskets.MapPost("/{id:guid}/checkout", async (Guid id, CheckoutInput input, ClaimsPrincipal user, IBasketService basketSvc, IOrderService orderSvc, CancellationToken ct) =>
        {
            var me = user.CustomerId()!.Value;
            if (!await basketSvc.IsOwnedByAsync(id, me, ct)) return Results.NotFound();

            input.CustomerId = me; // whatever the caller sent is replaced: an order is always the signed-in customer's own
            var accountEmail = user.FindFirstValue("email");
            if (accountEmail is not null) input.Email = accountEmail; // always the account's own: a shopper cannot send the order email elsewhere
            else if (string.IsNullOrWhiteSpace(input.Email)) input.Email = "";
            return await orderSvc.CheckoutAsync(id, input, accountEmail, ct) is { } o ? Results.Ok(o) : Results.NotFound();
        });

        var orders = storefront.MapGroup("/orders").WithTags("Storefront orders").AddEndpointFilter<ActiveCustomerFilter>();

        orders.MapGet("/", async (int? page, int? pageSize, ClaimsPrincipal user, IOrderService svc, CancellationToken ct) =>
            Results.Ok(await svc.ListForCustomerAsync(user.CustomerId()!.Value, page, pageSize, ct)));

        // Starts a card payment for what is still owed on the shopper's own order; another shopper's order is a 404.
        orders.MapPost("/{id:guid}/pay", async (Guid id, StartPaymentInput input, ClaimsPrincipal user, IOrderService orderSvc, IGatewayService svc, CancellationToken ct) =>
            await orderSvc.GetForCustomerAsync(user.CustomerId()!.Value, id, ct) is not null && await svc.StartAsync(id, input.Provider, ct) is { } s
                ? Results.Ok(s) : Results.NotFound());

        orders.MapGet("/{id:guid}", async (Guid id, ClaimsPrincipal user, IOrderService svc, CancellationToken ct) =>
            await svc.GetForCustomerAsync(user.CustomerId()!.Value, id, ct) is { } o ? Results.Ok(o) : Results.NotFound());
    }
}
