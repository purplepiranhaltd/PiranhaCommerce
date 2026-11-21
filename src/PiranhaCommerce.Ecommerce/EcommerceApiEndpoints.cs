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

        baskets.MapPost("/{id:guid}/promo", async (Guid id, CodeInput input, IBasketService svc, CancellationToken ct) =>
                await svc.SetPromotionAsync(id, input.Code, ct) is { } b ? Results.Ok(b) : Results.NotFound())
            .RequireAuthorization(ApiPolicies.Write);

        baskets.MapDelete("/{id:guid}/promo", async (Guid id, IBasketService svc, CancellationToken ct) =>
                await svc.SetPromotionAsync(id, null, ct) is { } b ? Results.Ok(b) : Results.NotFound())
            .RequireAuthorization(ApiPolicies.Write);

        baskets.MapPost("/{id:guid}/voucher", async (Guid id, CodeInput input, IBasketService svc, CancellationToken ct) =>
                await svc.SetVoucherAsync(id, input.Code, ct) is { } b ? Results.Ok(b) : Results.NotFound())
            .RequireAuthorization(ApiPolicies.Write);

        baskets.MapDelete("/{id:guid}/voucher", async (Guid id, IBasketService svc, CancellationToken ct) =>
                await svc.SetVoucherAsync(id, null, ct) is { } b ? Results.Ok(b) : Results.NotFound())
            .RequireAuthorization(ApiPolicies.Write);

        baskets.MapPost("/{id:guid}/quote", async (Guid id, QuoteInput input, IBasketService svc, CancellationToken ct) =>
            await svc.QuoteAsync(id, input, ct) is { } q ? Results.Ok(q) : Results.NotFound());

        var shipping = api.MapGroup("/shipping-methods").WithTags("Shipping");

        shipping.MapGet("/", async (bool? activeOnly, IShippingService svc, CancellationToken ct) =>
            Results.Ok(await svc.ListAsync(activeOnly ?? false, ct)));

        shipping.MapPost("/", async (ShippingMethodInput input, IShippingService svc, CancellationToken ct) =>
            {
                var m = await svc.CreateAsync(input, ct);
                return Results.Created($"/api/v1/shipping-methods/{m.Id}", m);
            })
            .RequireAuthorization(ApiPolicies.Write);

        shipping.MapPut("/{id:guid}", async (Guid id, ShippingMethodInput input, IShippingService svc, CancellationToken ct) =>
                await svc.UpdateAsync(id, input, ct) is { } m ? Results.Ok(m) : Results.NotFound())
            .RequireAuthorization(ApiPolicies.Write);

        shipping.MapDelete("/{id:guid}", async (Guid id, IShippingService svc, CancellationToken ct) =>
                await svc.DeleteAsync(id, ct) ? Results.NoContent() : Results.NotFound())
            .RequireAuthorization(ApiPolicies.Write);

        var vouchers = api.MapGroup("/vouchers").WithTags("Gift vouchers");

        vouchers.MapGet("/", async (int? page, int? pageSize, string? search, IVoucherService svc, CancellationToken ct) =>
            Results.Ok(await svc.ListAsync(page, pageSize, search, ct)));

        vouchers.MapGet("/{id:guid}", async (Guid id, IVoucherService svc, CancellationToken ct) =>
            await svc.GetAsync(id, ct) is { } v ? Results.Ok(v) : Results.NotFound());

        vouchers.MapPost("/lookup", async (CodeInput input, IVoucherService svc, CancellationToken ct) =>
            await svc.FindByCodeAsync(input.Code, ct) is { } v ? Results.Ok(v) : Results.NotFound());

        vouchers.MapPost("/", async (VoucherIssueInput input, IVoucherService svc, HttpContext http, CancellationToken ct) =>
            {
                var issued = await svc.IssueAsync(input, http.User.FindFirst("email")?.Value, ct);
                return Results.Created($"/api/v1/vouchers/{issued.Voucher.Id}", issued);
            })
            .RequireAuthorization(ApiPolicies.Write);

        vouchers.MapPut("/{id:guid}", async (Guid id, VoucherUpdateInput input, IVoucherService svc, CancellationToken ct) =>
                await svc.UpdateAsync(id, input, ct) is { } v ? Results.Ok(v) : Results.NotFound())
            .RequireAuthorization(ApiPolicies.Write);

        vouchers.MapPost("/{id:guid}/adjustments", async (Guid id, VoucherAdjustInput input, IVoucherService svc, HttpContext http, CancellationToken ct) =>
                await svc.AdjustAsync(id, input, http.User.FindFirst("email")?.Value, ct) is { } v ? Results.Ok(v) : Results.NotFound())
            .RequireAuthorization(ApiPolicies.Write);

        var promotions = api.MapGroup("/promotions").WithTags("Promotions");

        promotions.MapGet("/", async (IPromotionService svc, CancellationToken ct) => Results.Ok(await svc.ListAsync(ct)));

        promotions.MapGet("/{id:guid}", async (Guid id, IPromotionService svc, CancellationToken ct) =>
            await svc.GetAsync(id, ct) is { } p ? Results.Ok(p) : Results.NotFound());

        promotions.MapPost("/", async (PromotionInput input, IPromotionService svc, CancellationToken ct) =>
            {
                var p = await svc.CreateAsync(input, ct);
                return Results.Created($"/api/v1/promotions/{p.Id}", p);
            })
            .RequireAuthorization(ApiPolicies.Write);

        promotions.MapPut("/{id:guid}", async (Guid id, PromotionInput input, IPromotionService svc, CancellationToken ct) =>
                await svc.UpdateAsync(id, input, ct) is { } p ? Results.Ok(p) : Results.NotFound())
            .RequireAuthorization(ApiPolicies.Write);

        promotions.MapDelete("/{id:guid}", async (Guid id, IPromotionService svc, CancellationToken ct) =>
                await svc.DeleteAsync(id, ct) ? Results.NoContent() : Results.NotFound())
            .RequireAuthorization(ApiPolicies.Write);

        var classes = api.MapGroup("/tax-classes").WithTags("Tax");

        classes.MapGet("/", async (ITaxService svc, CancellationToken ct) => Results.Ok(await svc.ListClassesAsync(ct)));

        classes.MapPost("/", async (TaxClassInput input, ITaxService svc, CancellationToken ct) =>
            {
                var c = await svc.CreateClassAsync(input, ct);
                return Results.Created($"/api/v1/tax-classes/{c.Id}", c);
            })
            .RequireAuthorization(ApiPolicies.Write);

        classes.MapPut("/{id:guid}", async (Guid id, TaxClassInput input, ITaxService svc, CancellationToken ct) =>
                await svc.UpdateClassAsync(id, input, ct) is { } c ? Results.Ok(c) : Results.NotFound())
            .RequireAuthorization(ApiPolicies.Write);

        classes.MapDelete("/{id:guid}", async (Guid id, ITaxService svc, CancellationToken ct) =>
                await svc.DeleteClassAsync(id, ct) ? Results.NoContent() : Results.NotFound())
            .RequireAuthorization(ApiPolicies.Write);

        var tax = api.MapGroup("/tax-rules").WithTags("Tax");

        tax.MapGet("/", async (ITaxService svc, CancellationToken ct) => Results.Ok(await svc.ListAsync(ct)));

        tax.MapPost("/", async (TaxRuleInput input, ITaxService svc, CancellationToken ct) =>
            {
                var r = await svc.CreateAsync(input, ct);
                return Results.Created($"/api/v1/tax-rules/{r.Id}", r);
            })
            .RequireAuthorization(ApiPolicies.Write);

        tax.MapPut("/{id:guid}", async (Guid id, TaxRuleInput input, ITaxService svc, CancellationToken ct) =>
                await svc.UpdateAsync(id, input, ct) is { } r ? Results.Ok(r) : Results.NotFound())
            .RequireAuthorization(ApiPolicies.Write);

        tax.MapDelete("/{id:guid}", async (Guid id, ITaxService svc, CancellationToken ct) =>
                await svc.DeleteAsync(id, ct) ? Results.NoContent() : Results.NotFound())
            .RequireAuthorization(ApiPolicies.Write);

        var orders = api.MapGroup("/orders").WithTags("Orders");

        orders.MapGet("/", async (int? page, int? pageSize, OrderStage? stage, string? search, IOrderService svc, CancellationToken ct) =>
            Results.Ok(await svc.ListAsync(page, pageSize, stage, search, ct)));

        orders.MapGet("/{id:guid}", async (Guid id, IOrderService svc, CancellationToken ct) =>
            await svc.GetAsync(id, ct) is { } o ? Results.Ok(o) : Results.NotFound());

        orders.MapGet("/by-number/{number:long}", async (long number, IOrderService svc, CancellationToken ct) =>
            await svc.GetByNumberAsync(number, ct) is { } o ? Results.Ok(o) : Results.NotFound());

        orders.MapGet("/{id:guid}/payments", async (Guid id, IPaymentService svc, CancellationToken ct) =>
            await svc.ListAsync(id, ct) is { } list ? Results.Ok(list) : Results.NotFound());

        orders.MapPost("/{id:guid}/payments", async (Guid id, PaymentInput input, IPaymentService svc, HttpContext http, CancellationToken ct) =>
                await svc.RecordPaymentAsync(id, input, http.User.FindFirst("email")?.Value, ct) is { } p
                    ? Results.Created($"/api/v1/orders/{id}/payments/{p.Id}", p) : Results.NotFound())
            .RequireAuthorization(ApiPolicies.Write);

        orders.MapPost("/{id:guid}/refunds", async (Guid id, PaymentInput input, IPaymentService svc, HttpContext http, CancellationToken ct) =>
                await svc.RecordRefundAsync(id, input, http.User.FindFirst("email")?.Value, ct) is { } p
                    ? Results.Created($"/api/v1/orders/{id}/payments/{p.Id}", p) : Results.NotFound())
            .RequireAuthorization(ApiPolicies.Write);

        orders.MapPost("/{id:guid}/payments/start", async (Guid id, StartPaymentInput input, IGatewayService svc, CancellationToken ct) =>
                await svc.StartAsync(id, input.Provider, ct) is { } s ? Results.Ok(s) : Results.NotFound())
            .RequireAuthorization(ApiPolicies.Write);

        // Refunds through the provider itself (money goes back to the buyer's card), booked as a normal refund.
        orders.MapPost("/{id:guid}/payments/{paymentId:guid}/refund", async (Guid id, Guid paymentId, RefundStartInput input, IGatewayService svc, HttpContext http, CancellationToken ct) =>
                await svc.RefundAsync(id, paymentId, input, http.User.FindFirst("email")?.Value, ct) is { } r ? Results.Ok(r) : Results.NotFound())
            .RequireAuthorization(ApiPolicies.Write);

        orders.MapGet("/{id:guid}/shipments", async (Guid id, IFulfilmentService svc, CancellationToken ct) =>
            await svc.ListShipmentsAsync(id, ct) is { } list ? Results.Ok(list) : Results.NotFound());

        orders.MapPost("/{id:guid}/shipments", async (Guid id, ShipmentInput input, IFulfilmentService svc, HttpContext http, CancellationToken ct) =>
                await svc.ShipAsync(id, input, http.User.FindFirst("email")?.Value, ct) is { } s ? Results.Created($"/api/v1/orders/{id}/shipments/{s.Id}", s) : Results.NotFound())
            .RequireAuthorization(ApiPolicies.Write);

        orders.MapGet("/{id:guid}/returns", async (Guid id, IFulfilmentService svc, CancellationToken ct) =>
            await svc.ListReturnsAsync(id, ct) is { } list ? Results.Ok(list) : Results.NotFound());

        orders.MapPost("/{id:guid}/returns", async (Guid id, ReturnInput input, IFulfilmentService svc, HttpContext http, CancellationToken ct) =>
                await svc.RecordReturnAsync(id, input, http.User.FindFirst("email")?.Value, ct) is { } r ? Results.Created($"/api/v1/orders/{id}/returns/{r.Id}", r) : Results.NotFound())
            .RequireAuthorization(ApiPolicies.Write);

        // Public on purpose: a gateway cannot sign in. The gateway plugin verifies the webhook's signature, and nothing is trusted without it.
        api.MapPost("/payments/webhooks/{provider}", async (string provider, HttpContext http, IGatewayService svc, CancellationToken ct) =>
            {
                const int MaxBody = 256 * 1024;
                if (http.Request.ContentLength > MaxBody) return Results.StatusCode(StatusCodes.Status413PayloadTooLarge);
                using var buffer = new MemoryStream();
                var chunk = new byte[8192];
                int read;
                while ((read = await http.Request.Body.ReadAsync(chunk, ct)) > 0)
                {
                    if (buffer.Length + read > MaxBody) return Results.StatusCode(StatusCodes.Status413PayloadTooLarge);
                    buffer.Write(chunk, 0, read);
                }
                var headers = http.Request.Headers.ToDictionary(h => h.Key, h => h.Value.ToString(), StringComparer.OrdinalIgnoreCase);
                try
                {
                    return await svc.HandleWebhookAsync(provider, new GatewayWebhookRequest(headers, buffer.ToArray()), ct) is { } r ? Results.Ok(r) : Results.NotFound();
                }
                catch (GatewayWebhookRejectedException)
                {
                    return Results.BadRequest(new { error = "The webhook was rejected." }); // never say why: it would help a forger
                }
            })
            .AllowAnonymous().WithTags("Payments");

        // Money a gateway confirmed that could not be booked against an order: the list a person works through.
        var unreconciled = api.MapGroup("/unreconciled-payments").WithTags("Payments");

        unreconciled.MapGet("/", async (int? page, int? pageSize, bool? resolved, IUnreconciledPaymentService svc, CancellationToken ct) =>
            Results.Ok(await svc.ListAsync(page, pageSize, resolved ?? false, ct)));

        unreconciled.MapGet("/open-count", async (IUnreconciledPaymentService svc, CancellationToken ct) => Results.Ok(new { open = await svc.OpenCountAsync(ct) }));

        unreconciled.MapGet("/{id:guid}", async (Guid id, IUnreconciledPaymentService svc, CancellationToken ct) =>
            await svc.GetAsync(id, ct) is { } u ? Results.Ok(u) : Results.NotFound());

        unreconciled.MapPost("/{id:guid}/resolve", async (Guid id, ResolveUnreconciledInput input, IUnreconciledPaymentService svc, HttpContext http, CancellationToken ct) =>
                await svc.ResolveAsync(id, input, http.User.FindFirst("email")?.Value, ct) is { } u ? Results.Ok(u) : Results.NotFound())
            .RequireAuthorization(ApiPolicies.Write);

        unreconciled.MapPost("/{id:guid}/retry", async (Guid id, IUnreconciledPaymentService svc, HttpContext http, CancellationToken ct) =>
                await svc.RetryAsync(id, http.User.FindFirst("email")?.Value, ct) is { } u ? Results.Ok(u) : Results.NotFound())
            .RequireAuthorization(ApiPolicies.Write);

        api.MapGet("/payment-providers", (IPaymentService svc) => Results.Ok(svc.Providers())).WithTags("Payments");

        orders.MapPost("/{id:guid}/stage", async (Guid id, OrderStageInput input, IOrderService svc, HttpContext http, CancellationToken ct) =>
                await svc.ChangeStageAsync(id, input.Stage, http.User.FindFirst("email")?.Value, ct) is { } o ? Results.Ok(o) : Results.NotFound())
            .RequireAuthorization(ApiPolicies.Write);
    }
}
