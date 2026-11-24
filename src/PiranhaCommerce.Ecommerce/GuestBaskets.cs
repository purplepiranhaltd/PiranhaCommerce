using System.Security.Claims;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using PiranhaCommerce.Api;
using PiranhaCommerce.Core.Validation;
using PiranhaCommerce.Customers;

namespace PiranhaCommerce.Ecommerce;

public sealed class MergeBasketInput
{
    /// <summary>The guest basket (one with no owner) whose lines are moved into this one.</summary>
    public Guid FromBasketId { get; set; }
}

internal sealed partial class BasketService : IGuestBasketService
{
    public async Task<bool> IsGuestBasketAsync(Guid basketId, CancellationToken ct = default) =>
        await db.Set<Basket>().AsNoTracking().AnyAsync(b => b.Id == basketId && b.CustomerId == null, ct);

    public async Task<BasketDto?> MergeAsync(Guid basketId, Guid fromBasketId, Guid customerId, CancellationToken ct = default)
    {
        if (basketId == fromBasketId) return null;
        if (!await db.Set<Basket>().AsNoTracking().AnyAsync(b => b.Id == basketId && b.CustomerId == customerId, ct)) return null;

        var strategy = db.Database.CreateExecutionStrategy();
        bool merged;
        try
        {
            merged = await strategy.ExecuteInTransactionAsync(
                async token =>
                {
                    db.ChangeTracker.Clear();
                    var now = (DateTimeOffset?)DateTimeOffset.UtcNow;

                    // Take the guest basket first: of two merges of the same one, exactly one gets a row here, and the lock is held to commit.
                    var took = await db.Set<Basket>().Where(b => b.Id == fromBasketId && b.CustomerId == null && b.Status == BasketStatus.Open)
                        .ExecuteUpdateAsync(s => s.SetProperty(b => b.UpdatedUtc, now), token);
                    if (took == 0) return false;
                    var held = await db.Set<Basket>().Where(b => b.Id == basketId && b.CustomerId == customerId && b.Status == BasketStatus.Open)
                        .ExecuteUpdateAsync(s => s.SetProperty(b => b.UpdatedUtc, now), token);
                    if (held == 0) return false;

                    var incoming = await db.Set<BasketLine>().AsNoTracking().Where(l => l.BasketId == fromBasketId).Select(l => new { l.ProductId, l.Quantity }).ToListAsync(token);
                    var ids = incoming.Select(l => l.ProductId).ToList();
                    var sellable = (await db.Set<ProductListing>().AsNoTracking().Where(l => l.Published && ids.Contains(l.ProductId)).Select(l => l.ProductId).ToListAsync(token)).ToHashSet();
                    var existing = await db.Set<BasketLine>().AsNoTracking().Where(l => l.BasketId == basketId).ToDictionaryAsync(l => l.ProductId, l => l.Quantity, token);

                    // Quantities add up (never past the limit for one line). A product that can no longer be bought is left behind.
                    foreach (var line in incoming.Where(l => sellable.Contains(l.ProductId)))
                    {
                        if (existing.TryGetValue(line.ProductId, out var have))
                        {
                            var total = (int)Math.Min((long)have + line.Quantity, BasketLineInput.Max);
                            await db.Set<BasketLine>().Where(l => l.BasketId == basketId && l.ProductId == line.ProductId)
                                .ExecuteUpdateAsync(s => s.SetProperty(l => l.Quantity, total), token);
                        }
                        else db.Add(new BasketLine { BasketId = basketId, ProductId = line.ProductId, Quantity = Math.Min(line.Quantity, BasketLineInput.Max) });
                    }
                    await db.SaveChangesAsync(token);

                    // A code or voucher on the guest basket comes along only when this basket has none of its own (and a code must suit this customer).
                    var mine = await db.Set<Basket>().AsNoTracking().Where(b => b.Id == basketId).Select(b => new { b.PromotionCode, b.VoucherId }).FirstAsync(token);
                    var theirs = await db.Set<Basket>().AsNoTracking().Where(b => b.Id == fromBasketId).Select(b => new { b.PromotionCode, b.VoucherId }).FirstAsync(token);
                    if (mine.PromotionCode is null && theirs.PromotionCode is { } code)
                    {
                        var promo = await db.Set<Promotion>().AsNoTracking().FirstOrDefaultAsync(p => p.Code == code, token);
                        if (PromotionRules.Problem(promo, decimal.MaxValue, DateTimeOffset.UtcNow, await PromotionEligibility.Async(db, promo, customerId, token)) is null)
                            await db.Set<Basket>().Where(b => b.Id == basketId).ExecuteUpdateAsync(s => s.SetProperty(b => b.PromotionCode, code), token);
                    }
                    if (mine.VoucherId is null && theirs.VoucherId is { } voucherId)
                        await db.Set<Basket>().Where(b => b.Id == basketId).ExecuteUpdateAsync(s => s.SetProperty(b => b.VoucherId, (Guid?)voucherId), token);

                    await db.Set<Basket>().Where(b => b.Id == fromBasketId).ExecuteDeleteAsync(token); // its lines go with it
                    return true;
                },
                async token => !await db.Set<Basket>().AsNoTracking().AnyAsync(b => b.Id == fromBasketId, token),
                ct);
        }
        catch (DbUpdateException)
        {
            db.ChangeTracker.Clear();
            throw new PiranhaCommerceConflictException("The baskets could not be merged because one of them changed at the same moment. Please try again.");
        }
        return merged ? await GetAsync(basketId, ct) : null;
    }
}

/// <summary>What the guest (not signed in) basket routes need beyond the ordinary basket service.</summary>
public interface IGuestBasketService
{
    /// <summary>True when the basket exists and has no owner: the only baskets the guest routes will touch.</summary>
    Task<bool> IsGuestBasketAsync(Guid basketId, CancellationToken ct = default);

    /// <summary>
    /// Moves the lines of a guest basket into a customer's own open basket, adding quantities, and deletes the guest basket. A code or voucher on
    /// it comes along when the target has none. Null when either basket is missing, the target is not the customer's, or the source is not an
    /// open guest basket (all the same answer).
    /// </summary>
    Task<BasketDto?> MergeAsync(Guid basketId, Guid fromBasketId, Guid customerId, CancellationToken ct = default);
}

/// <summary>
/// Shopping and checking out without an account. A guest basket is an ordinary basket with no owner, and knowing its id is the whole of the
/// proof (as for claiming one), so the routes touch only baskets that have no owner: a signed-in shopper's basket answers 404 here. Creating
/// one is rate limited per client. Placing the order needs an email address, and the order belongs to no customer.
/// </summary>
internal sealed class GuestStorefrontEndpoints : IStorefrontEndpointContributor
{
    public void MapEndpoints(IEndpointRouteBuilder storefront)
    {
        var guest = storefront.MapGroup("/guest/baskets").WithTags("Guest baskets").AllowAnonymous();

        guest.MapPost("/", async (IBasketService svc, CancellationToken ct) =>
        {
            var created = await svc.CreateAsync(new BasketCreateInput(), ct);
            return Results.Created($"/api/v1/storefront/guest/baskets/{created.Id}", created);
        });

        guest.MapGet("/{id:guid}", async (Guid id, IBasketService svc, IGuestBasketService guests, CancellationToken ct) =>
            await guests.IsGuestBasketAsync(id, ct) && await svc.GetAsync(id, ct) is { } b ? Results.Ok(b) : Results.NotFound());

        guest.MapPut("/{id:guid}/lines/{productId:guid}", async (Guid id, Guid productId, BasketLineInput input, IBasketService svc, IGuestBasketService guests, CancellationToken ct) =>
            await guests.IsGuestBasketAsync(id, ct) && await svc.SetLineAsync(id, productId, input, ct) is { } b ? Results.Ok(b) : Results.NotFound());

        guest.MapDelete("/{id:guid}/lines/{productId:guid}", async (Guid id, Guid productId, IBasketService svc, IGuestBasketService guests, CancellationToken ct) =>
            await guests.IsGuestBasketAsync(id, ct) && await svc.SetLineAsync(id, productId, new BasketLineInput { Quantity = 0 }, ct) is { } b ? Results.Ok(b) : Results.NotFound());

        guest.MapPost("/{id:guid}/promo", async (Guid id, CodeInput input, IBasketService svc, IGuestBasketService guests, CancellationToken ct) =>
            await guests.IsGuestBasketAsync(id, ct) && await svc.SetPromotionAsync(id, input.Code, ct) is { } b ? Results.Ok(b) : Results.NotFound());

        guest.MapDelete("/{id:guid}/promo", async (Guid id, IBasketService svc, IGuestBasketService guests, CancellationToken ct) =>
            await guests.IsGuestBasketAsync(id, ct) && await svc.SetPromotionAsync(id, null, ct) is { } b ? Results.Ok(b) : Results.NotFound());

        guest.MapPost("/{id:guid}/voucher", async (Guid id, CodeInput input, IBasketService svc, IGuestBasketService guests, CancellationToken ct) =>
            await guests.IsGuestBasketAsync(id, ct) && await svc.SetVoucherAsync(id, input.Code, ct) is { } b ? Results.Ok(b) : Results.NotFound());

        guest.MapDelete("/{id:guid}/voucher", async (Guid id, IBasketService svc, IGuestBasketService guests, CancellationToken ct) =>
            await guests.IsGuestBasketAsync(id, ct) && await svc.SetVoucherAsync(id, null, ct) is { } b ? Results.Ok(b) : Results.NotFound());

        guest.MapGet("/{id:guid}/shipping-options", async (Guid id, string countryCode, string? postalCode, IBasketService svc, IGuestBasketService guests, CancellationToken ct) =>
            await guests.IsGuestBasketAsync(id, ct) && await svc.ShippingOptionsAsync(id, countryCode, postalCode, ct) is { } o ? Results.Ok(o) : Results.NotFound());

        guest.MapPost("/{id:guid}/quote", async (Guid id, QuoteInput input, IBasketService svc, IGuestBasketService guests, CancellationToken ct) =>
            await guests.IsGuestBasketAsync(id, ct) && await svc.QuoteAsync(id, input, ct) is { } q ? Results.Ok(q) : Results.NotFound());

        guest.MapPost("/{id:guid}/checkout", async (Guid id, CheckoutInput input, IBasketService svc, IGuestBasketService guests, IOrderService orders,
                Microsoft.Extensions.Options.IOptions<EcommerceOptions> options, CancellationToken ct) =>
            {
                if (!await guests.IsGuestBasketAsync(id, ct)) return Results.NotFound();
                if (!options.Value.AllowGuestCheckout)
                    return Results.Problem(title: "Sign in to check out", detail: "This shop does not take orders without an account.", statusCode: StatusCodes.Status403Forbidden);
                input.CustomerId = null; // whatever the caller sent is dropped: a guest order belongs to no customer
                return await orders.CheckoutAsync(id, input, null, ct) is { } o ? Results.Ok(o) : Results.NotFound();
            });
    }
}
