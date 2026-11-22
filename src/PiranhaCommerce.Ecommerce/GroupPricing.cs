using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using PiranhaCommerce.Api;
using PiranhaCommerce.Catalog;
using PiranhaCommerce.Core.Validation;
using PiranhaCommerce.Customers;
using PiranhaCommerce.Data;

namespace PiranhaCommerce.Ecommerce;

/// <summary>A fixed price for one product for the members of one customer group. It beats the group's percentage for that product.</summary>
public class GroupPrice
{
    public Guid GroupId { get; set; }
    public Guid ProductId { get; set; }
    public decimal Price { get; set; }
}

public sealed class GroupPriceInput
{
    [Range(0, 1_000_000_000)] public decimal Price { get; set; }
}

/// <summary>One row of a product's group prices: the group, its percentage off, a fixed price if set, and what its members actually pay.</summary>
public sealed record ProductGroupPriceDto(Guid GroupId, string GroupName, decimal DiscountPercent, decimal? FixedPrice, decimal GroupPrice);

/// <summary>What one shopper pays for a product: <see cref="Price"/> is the regular price unless one of their groups gives them less.</summary>
public sealed record ShopperPriceDto(Guid ProductId, decimal RegularPrice, decimal Price, bool Discounted);

public interface IGroupPricingService
{
    /// <summary>Every customer group with what it pays for this product. Null when the product does not exist.</summary>
    Task<IReadOnlyList<ProductGroupPriceDto>?> ListForProductAsync(Guid productId, CancellationToken ct = default);

    /// <summary>Sets (or replaces) the fixed price for a group. Null when the product or group does not exist.</summary>
    Task<ProductGroupPriceDto?> SetAsync(Guid productId, Guid groupId, GroupPriceInput input, CancellationToken ct = default);

    /// <summary>Removes the fixed price (the group's percentage applies again). False when there was none.</summary>
    Task<bool> RemoveAsync(Guid productId, Guid groupId, CancellationToken ct = default);

    /// <summary>The prices this customer pays (a null customer pays the regular price). Products that do not exist are left out. At most 200 at a time.</summary>
    Task<IReadOnlyList<ShopperPriceDto>> PricesAsync(Guid? customerId, IReadOnlyCollection<Guid> productIds, CancellationToken ct = default);
}

/// <summary>
/// The one place that decides what a customer pays. Each group they belong to gives a price: the fixed price for that product if it has one,
/// otherwise the regular price less the group's percentage (rounded to 2 places). They pay the lowest of those and the regular price, so a
/// group can never make something dearer. Baskets, quotes and checkout all use this, so they always agree.
/// </summary>
internal static class GroupPricing
{
    public static decimal Percentage(decimal price, decimal percent) => decimal.Round(price * (100m - percent) / 100m, 2, MidpointRounding.AwayFromZero);

    public static async Task<Dictionary<Guid, decimal>> EffectiveAsync(PiranhaDbContext db, Guid? customerId, IReadOnlyCollection<(Guid ProductId, decimal Price)> items, CancellationToken ct)
    {
        var result = new Dictionary<Guid, decimal>();
        foreach (var (id, price) in items) result[id] = price;
        if (customerId is not { } customer || result.Count == 0) return result;

        var groups = await (from m in db.Set<CustomerGroupMember>().AsNoTracking()
                            join g in db.Set<CustomerGroup>().AsNoTracking() on m.GroupId equals g.Id
                            where m.CustomerId == customer
                            select new { g.Id, g.DiscountPercent }).ToListAsync(ct);
        if (groups.Count == 0) return result;

        var groupIds = groups.Select(g => g.Id).ToList();
        var productIds = result.Keys.ToList();
        var fixedPrices = (await db.Set<GroupPrice>().AsNoTracking().Where(p => groupIds.Contains(p.GroupId) && productIds.Contains(p.ProductId)).ToListAsync(ct))
            .ToDictionary(p => (p.GroupId, p.ProductId), p => p.Price);

        foreach (var productId in productIds)
        {
            var regular = result[productId];
            var best = regular;
            foreach (var g in groups)
            {
                var candidate = fixedPrices.TryGetValue((g.Id, productId), out var fixedPrice) ? fixedPrice
                    : g.DiscountPercent > 0 ? Percentage(regular, g.DiscountPercent) : regular;
                if (candidate < best) best = candidate;
            }
            result[productId] = best;
        }
        return result;
    }
}

internal sealed class GroupPricingService(PiranhaDbContext db) : IGroupPricingService
{
    private async Task<IReadOnlyList<ProductGroupPriceDto>> RowsAsync(Guid productId, decimal regular, CancellationToken ct)
    {
        var groups = await db.Set<CustomerGroup>().AsNoTracking().OrderBy(g => g.NormalizedName).Select(g => new { g.Id, g.Name, g.DiscountPercent }).ToListAsync(ct);
        var fixedPrices = await db.Set<GroupPrice>().AsNoTracking().Where(p => p.ProductId == productId).ToDictionaryAsync(p => p.GroupId, p => p.Price, ct);
        return groups.Select(g =>
        {
            decimal? fixedPrice = fixedPrices.TryGetValue(g.Id, out var f) ? f : null;
            var price = fixedPrice ?? (g.DiscountPercent > 0 ? GroupPricing.Percentage(regular, g.DiscountPercent) : regular);
            return new ProductGroupPriceDto(g.Id, g.Name, g.DiscountPercent, fixedPrice, price);
        }).ToList();
    }

    public async Task<IReadOnlyList<ProductGroupPriceDto>?> ListForProductAsync(Guid productId, CancellationToken ct = default)
    {
        var regular = await db.Set<Product>().AsNoTracking().Where(p => p.Id == productId).Select(p => (decimal?)p.Price).FirstOrDefaultAsync(ct);
        return regular is null ? null : await RowsAsync(productId, regular.Value, ct);
    }

    public async Task<ProductGroupPriceDto?> SetAsync(Guid productId, Guid groupId, GroupPriceInput input, CancellationToken ct = default)
    {
        ModelValidator.ValidateOrThrow(input);
        if (decimal.Round(input.Price, 4) != input.Price) throw new PiranhaValidationException(nameof(GroupPriceInput.Price), "A price can have at most 4 decimal places.");
        var regular = await db.Set<Product>().AsNoTracking().Where(p => p.Id == productId).Select(p => (decimal?)p.Price).FirstOrDefaultAsync(ct);
        if (regular is null || !await db.Set<CustomerGroup>().AnyAsync(g => g.Id == groupId, ct)) return null;

        // Update first, then insert if there was nothing to update; a concurrent insert of the same pair shows as a key clash and is retried as an update.
        var updated = await db.Set<GroupPrice>().Where(p => p.GroupId == groupId && p.ProductId == productId)
            .ExecuteUpdateAsync(s => s.SetProperty(p => p.Price, input.Price), ct);
        if (updated == 0)
        {
            try
            {
                db.Add(new GroupPrice { GroupId = groupId, ProductId = productId, Price = input.Price });
                await db.SaveChangesAsync(ct);
            }
            catch (DbUpdateException)
            {
                db.ChangeTracker.Clear();
                await db.Set<GroupPrice>().Where(p => p.GroupId == groupId && p.ProductId == productId).ExecuteUpdateAsync(s => s.SetProperty(p => p.Price, input.Price), ct);
            }
        }
        return (await RowsAsync(productId, regular.Value, ct)).First(r => r.GroupId == groupId);
    }

    public async Task<bool> RemoveAsync(Guid productId, Guid groupId, CancellationToken ct = default) =>
        await db.Set<GroupPrice>().Where(p => p.GroupId == groupId && p.ProductId == productId).ExecuteDeleteAsync(ct) > 0;

    public async Task<IReadOnlyList<ShopperPriceDto>> PricesAsync(Guid? customerId, IReadOnlyCollection<Guid> productIds, CancellationToken ct = default)
    {
        var ids = productIds.Distinct().Take(200).ToList();
        if (ids.Count == 0) return [];
        var regular = await db.Set<Product>().AsNoTracking().Where(p => ids.Contains(p.Id)).Select(p => new { p.Id, p.Price }).ToListAsync(ct);
        var effective = await GroupPricing.EffectiveAsync(db, customerId, regular.Select(r => (r.Id, r.Price)).ToList(), ct);
        return regular.Select(r => new ShopperPriceDto(r.Id, r.Price, effective[r.Id], effective[r.Id] < r.Price)).OrderBy(r => r.ProductId).ToList();
    }
}

internal sealed class GroupPricingEndpoints : IApiEndpointContributor
{
    public void MapEndpoints(IEndpointRouteBuilder api)
    {
        var group = api.MapGroup("/products/{productId:guid}/group-prices").WithTags("Group prices");

        group.MapGet("/", async (Guid productId, IGroupPricingService svc, CancellationToken ct) =>
            await svc.ListForProductAsync(productId, ct) is { } rows ? Results.Ok(rows) : Results.NotFound());

        group.MapPut("/{groupId:guid}", async (Guid productId, Guid groupId, GroupPriceInput input, IGroupPricingService svc, CancellationToken ct) =>
                await svc.SetAsync(productId, groupId, input, ct) is { } row ? Results.Ok(row) : Results.NotFound())
            .RequireAuthorization(ApiPolicies.Write);

        group.MapDelete("/{groupId:guid}", async (Guid productId, Guid groupId, IGroupPricingService svc, CancellationToken ct) =>
                await svc.RemoveAsync(productId, groupId, ct) ? Results.NoContent() : Results.NotFound())
            .RequireAuthorization(ApiPolicies.Write);

        // For a shop front that shows prices on a shopper's behalf: what this customer pays for these products.
        api.MapGet("/customers/{customerId:guid}/prices", async (Guid customerId, Guid[]? productIds, IGroupPricingService svc, CancellationToken ct) =>
                Results.Ok(await svc.PricesAsync(customerId, productIds ?? [], ct)))
            .WithTags("Group prices");
    }
}

/// <summary>The signed-in shopper's own prices. The customer is always the one in the token.</summary>
internal sealed class GroupPricingStorefrontEndpoints : IStorefrontEndpointContributor
{
    public void MapEndpoints(IEndpointRouteBuilder storefront)
    {
        storefront.MapGet("/prices", async (Guid[]? productIds, System.Security.Claims.ClaimsPrincipal user, IGroupPricingService svc, CancellationToken ct) =>
                Results.Ok(await svc.PricesAsync(user.CustomerId(), productIds ?? [], ct)))
            .WithTags("Storefront prices").AddEndpointFilter<ActiveCustomerFilter>();
    }
}
