using System.Security.Claims;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using PiranhaCommerce.Api;
using PiranhaCommerce.Catalog;
using PiranhaCommerce.Core.Domain;
using PiranhaCommerce.Core.Validation;
using PiranhaCommerce.Customers;
using PiranhaCommerce.Data;

namespace PiranhaCommerce.Ecommerce;

/// <summary>One product (or one variant) on a signed-in customer's wishlist. A customer has one wishlist; a product is on it once.</summary>
public class WishlistItem : Entity
{
    public Guid CustomerId { get; set; }

    /// <summary>The product saved: a simple product or one chosen variant (so a shopper can save "the blue one in medium").</summary>
    public Guid ProductId { get; set; }

    public DateTimeOffset AddedUtc { get; set; }

    /// <summary>The price when it was saved, so the shopper can be shown that it has changed.</summary>
    public decimal PriceWhenAdded { get; set; }
}

public sealed class WishlistAddInput
{
    public Guid ProductId { get; set; }
}

/// <param name="Price">What this shopper pays now (a sale, their group price and so on); <paramref name="PriceWhenAdded"/> is what they would have paid when they saved it.</param>
/// <param name="Available">True while the product is on sale and in stock where online orders take it from.</param>
/// <param name="Published">False when the product has been taken off sale since it was saved.</param>
public sealed record WishlistItemDto(
    Guid ProductId, string Name, string? Sku, string? Slug, decimal Price, decimal RegularPrice, decimal PriceWhenAdded, bool Published, bool Available, DateTimeOffset AddedUtc, ShopImageDto? Image);

/// <summary>For staff: how many wishlists a product is on.</summary>
public sealed record WishlistDemandDto(Guid ProductId, string Name, string? Sku, int Wishlists);

public interface IWishlistService
{
    /// <summary>The customer's wishlist, newest first.</summary>
    Task<IReadOnlyList<WishlistItemDto>> ListAsync(Guid customerId, CancellationToken ct = default);

    /// <summary>
    /// Saves a product. Saving one already saved is a quiet success. Validation error when the product is not on sale;
    /// conflict when the wishlist is full.
    /// </summary>
    Task<WishlistItemDto> AddAsync(Guid customerId, Guid productId, CancellationToken ct = default);

    Task<bool> RemoveAsync(Guid customerId, Guid productId, CancellationToken ct = default);
    Task<int> ClearAsync(Guid customerId, CancellationToken ct = default);

    /// <summary>The products on the most wishlists, most first.</summary>
    Task<IReadOnlyList<WishlistDemandDto>> DemandAsync(CancellationToken ct = default);
}

internal sealed class WishlistService(PiranhaCommerceDbContext db, IOptions<EcommerceOptions> options, IGroupPricingService pricing) : IWishlistService
{
    /// <summary>The most products one wishlist holds.</summary>
    public const int MaxItems = 200;

    public async Task<IReadOnlyList<WishlistItemDto>> ListAsync(Guid customerId, CancellationToken ct = default)
    {
        var items = await db.Set<WishlistItem>().AsNoTracking().Where(i => i.CustomerId == customerId).OrderByDescending(i => i.AddedUtc).ThenBy(i => i.Id).ToListAsync(ct);
        if (items.Count == 0) return [];
        var ids = items.Select(i => i.ProductId).ToList();
        var location = options.Value.StockLocationId ?? StockLocations.DefaultId;
        var products = await db.Set<Product>().AsNoTracking().Where(p => ids.Contains(p.Id)).Select(p => new { p.Id, p.Name, p.Sku, p.Slug }).ToDictionaryAsync(p => p.Id, ct);
        var prices = (await pricing.PricesAsync(customerId, ids, null, ct)).ToDictionary(x => x.ProductId);
        var images = await ShopImages.FirstAsync(db, ids, ct);
        var published = (await db.Set<ProductListing>().AsNoTracking().Where(l => ids.Contains(l.ProductId) && l.Published).Select(l => l.ProductId).ToListAsync(ct)).ToHashSet();
        var inStock = await ShopStock.InStockAsync(db, ids, location, ct);
        return items.Where(i => products.ContainsKey(i.ProductId)).Select(i =>
        {
            var p = products[i.ProductId];
            var isPublished = published.Contains(i.ProductId);
            var price = prices[i.ProductId];
            return new WishlistItemDto(i.ProductId, p.Name, p.Sku, p.Slug, price.Price, price.RegularPrice, i.PriceWhenAdded, isPublished, isPublished && inStock.Contains(i.ProductId), i.AddedUtc, images.GetValueOrDefault(i.ProductId));
        }).ToList();
    }

    public async Task<WishlistItemDto> AddAsync(Guid customerId, Guid productId, CancellationToken ct = default)
    {
        var product = await db.Set<Product>().AsNoTracking().Where(p => p.Id == productId).Select(p => new { p.Id }).FirstOrDefaultAsync(ct);
        if (product is null || !await db.Set<ProductListing>().AsNoTracking().AnyAsync(l => l.ProductId == productId && l.Published, ct))
            throw new PiranhaCommerceValidationException(nameof(WishlistAddInput.ProductId), "That product cannot be added to a wishlist.");

        if (!await db.Set<WishlistItem>().AnyAsync(i => i.CustomerId == customerId && i.ProductId == productId, ct))
        {
            if (await db.Set<WishlistItem>().CountAsync(i => i.CustomerId == customerId, ct) >= MaxItems)
                throw new PiranhaCommerceConflictException($"A wishlist holds up to {MaxItems} products. Remove some first.");
            db.Add(new WishlistItem { CustomerId = customerId, ProductId = productId, AddedUtc = DateTimeOffset.UtcNow, PriceWhenAdded = (await pricing.PricesAsync(customerId, [productId], null, ct)).First().Price });
            try { await db.SaveChangesAsync(ct); }
            catch (DbUpdateException) { db.ChangeTracker.Clear(); } // the same product added twice at once: the unique index keeps one
        }
        return (await ListAsync(customerId, ct)).First(i => i.ProductId == productId);
    }

    public async Task<bool> RemoveAsync(Guid customerId, Guid productId, CancellationToken ct = default) =>
        await db.Set<WishlistItem>().Where(i => i.CustomerId == customerId && i.ProductId == productId).ExecuteDeleteAsync(ct) > 0;

    public async Task<int> ClearAsync(Guid customerId, CancellationToken ct = default) =>
        await db.Set<WishlistItem>().Where(i => i.CustomerId == customerId).ExecuteDeleteAsync(ct);

    public async Task<IReadOnlyList<WishlistDemandDto>> DemandAsync(CancellationToken ct = default)
    {
        var counts = await db.Set<WishlistItem>().AsNoTracking().GroupBy(i => i.ProductId).Select(g => new { ProductId = g.Key, N = g.Count() })
            .OrderByDescending(x => x.N).Take(500).ToListAsync(ct);
        var ids = counts.Select(c => c.ProductId).ToList();
        var products = await db.Set<Product>().AsNoTracking().Where(p => ids.Contains(p.Id)).Select(p => new { p.Id, p.Name, p.Sku }).ToListAsync(ct);
        return counts.Join(products, c => c.ProductId, p => p.Id, (c, p) => new WishlistDemandDto(p.Id, p.Name, p.Sku, c.N))
            .OrderByDescending(d => d.Wishlists).ThenBy(d => d.Name, StringComparer.Ordinal).ToList();
    }
}

/// <summary>The wishlist part of an export and an erasure: what the person saved is listed, and deleted on erasure.</summary>
internal sealed class WishlistCustomerData(PiranhaCommerceDbContext db) : ICustomerDataContributor
{
    public string Name => "wishlist";

    public Task<IReadOnlyList<string>> CheckErasureAsync(CustomerSubject subject, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<string>>([]);

    public async Task<object?> ExportAsync(CustomerSubject subject, CancellationToken ct = default)
    {
        var items = await db.Set<WishlistItem>().AsNoTracking().Where(i => i.CustomerId == subject.CustomerId).OrderBy(i => i.AddedUtc)
            .Select(i => new { i.ProductId, i.AddedUtc, i.PriceWhenAdded }).ToListAsync(ct);
        return items.Count == 0 ? null : items;
    }

    public async Task<int> EraseAsync(CustomerSubject subject, CancellationToken ct = default) =>
        await db.Set<WishlistItem>().Where(i => i.CustomerId == subject.CustomerId).ExecuteDeleteAsync(ct);
}

internal sealed class WishlistEndpoints : IApiEndpointContributor
{
    public void MapEndpoints(IEndpointRouteBuilder api)
    {
        api.MapGet("/wishlists/demand", async (IWishlistService svc, CancellationToken ct) => Results.Ok(await svc.DemandAsync(ct))).WithTags("Wishlists");
    }
}

internal sealed class WishlistStorefrontEndpoints : IStorefrontEndpointContributor
{
    public void MapEndpoints(IEndpointRouteBuilder storefront)
    {
        var g = storefront.MapGroup("/wishlist").WithTags("Storefront wishlist").AddEndpointFilter<ActiveCustomerFilter>();
        g.MapGet("/", async (ClaimsPrincipal user, IWishlistService svc, CancellationToken ct) => Results.Ok(await svc.ListAsync(user.CustomerId()!.Value, ct)));
        g.MapPost("/", async (WishlistAddInput input, ClaimsPrincipal user, IWishlistService svc, CancellationToken ct) =>
            Results.Ok(await svc.AddAsync(user.CustomerId()!.Value, input.ProductId, ct)));
        g.MapDelete("/{productId:guid}", async (Guid productId, ClaimsPrincipal user, IWishlistService svc, CancellationToken ct) =>
            await svc.RemoveAsync(user.CustomerId()!.Value, productId, ct) ? Results.NoContent() : Results.NotFound());
        g.MapDelete("/", async (ClaimsPrincipal user, IWishlistService svc, CancellationToken ct) =>
        {
            await svc.ClearAsync(user.CustomerId()!.Value, ct);
            return Results.NoContent();
        });
    }
}
