using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using PiranhaCommerce.Api;
using PiranhaCommerce.Catalog;
using PiranhaCommerce.Core.Domain;
using PiranhaCommerce.Core.Validation;
using PiranhaCommerce.Data;

namespace PiranhaCommerce.Ecommerce;

public enum ProductRelationKind
{
    /// <summary>"You may also like": shown on the product's own page.</summary>
    Related = 0,

    /// <summary>"Goes well with": shown with the product in the basket (a case for the phone).</summary>
    CrossSell = 1,
}

/// <summary>
/// A product the shop suggests alongside another. Kept per product and kind, in the order staff chose. One-way: making B related to A does not make A related to B.
/// The suggested product has no foreign key (the database would have two cascade paths to products); a suggestion whose product is gone is simply never shown.
/// </summary>
public class ProductRelation : Entity
{
    public Guid ProductId { get; set; }
    public Guid RelatedProductId { get; set; }
    public ProductRelationKind Kind { get; set; }
    public int SortOrder { get; set; }
}

public sealed class ProductRelationsInput
{
    /// <summary>Products to suggest on this product's page, in order. A PUT replaces the whole list. At most 20.</summary>
    [MaxLength(20)] public List<Guid> Related { get; set; } = [];

    /// <summary>Products to suggest with this one in the basket, in order. At most 20.</summary>
    [MaxLength(20)] public List<Guid> CrossSell { get; set; } = [];
}

public sealed record ProductRelationDto(Guid ProductId, string Name, string? Sku, decimal Price, bool Published);

/// <summary>What staff see: every suggestion, including products that are not on sale.</summary>
public sealed record ProductRelationsDto(IReadOnlyList<ProductRelationDto> Related, IReadOnlyList<ProductRelationDto> CrossSell);

/// <param name="Available">True while the product can be bought now: in stock where online orders take it from.</param>
public sealed record SuggestedProductDto(Guid ProductId, string Name, string? Sku, decimal Price, bool Available);

/// <summary>What shoppers see: only products on sale.</summary>
public sealed record SuggestionsDto(IReadOnlyList<SuggestedProductDto> Related, IReadOnlyList<SuggestedProductDto> CrossSell);

public interface IProductRelationService
{
    /// <summary>Null when the product does not exist.</summary>
    Task<ProductRelationsDto?> GetAsync(Guid productId, CancellationToken ct = default);

    /// <summary>
    /// Replaces the product's suggestions. Null when the product does not exist. Validation error for a product suggested for itself, one that does not exist,
    /// a variant (suggest the main product), or the same product twice in a list.
    /// </summary>
    Task<ProductRelationsDto?> SetAsync(Guid productId, ProductRelationsInput input, CancellationToken ct = default);

    /// <summary>What to show shoppers on a product's page. Null when the product does not exist or is not on sale.</summary>
    Task<SuggestionsDto?> ForProductAsync(Guid productId, CancellationToken ct = default);

    /// <summary>
    /// Cross-sell suggestions for a basket: the cross-sell products of the given products that are on sale, leaving out anything already in the basket,
    /// those suggested by the most products first. At most 12.
    /// </summary>
    Task<IReadOnlyList<SuggestedProductDto>> CrossSellForBasketAsync(IReadOnlyCollection<Guid> productIds, CancellationToken ct = default);
}

internal sealed class ProductRelationService(PiranhaDbContext db, IOptions<EcommerceOptions> options) : IProductRelationService
{
    public const int MaxPerKind = 20;
    public const int MaxBasketSuggestions = 12;

    /// <summary>The parent product a variant belongs to, else the product itself.</summary>
    private async Task<Guid> ParentAsync(Guid productId, CancellationToken ct) =>
        await db.Set<ProductVariant>().AsNoTracking().Where(v => v.ProductId == productId).Select(v => (Guid?)v.ParentProductId).FirstOrDefaultAsync(ct) ?? productId;

    public async Task<ProductRelationsDto?> GetAsync(Guid productId, CancellationToken ct = default)
    {
        if (!await db.Set<Product>().AnyAsync(p => p.Id == productId, ct)) return null;
        var rows = await db.Set<ProductRelation>().AsNoTracking().Where(r => r.ProductId == productId).OrderBy(r => r.SortOrder).ToListAsync(ct);
        var ids = rows.Select(r => r.RelatedProductId).ToList();
        var products = await db.Set<Product>().AsNoTracking().Where(p => ids.Contains(p.Id)).Select(p => new { p.Id, p.Name, p.Sku, p.Price }).ToDictionaryAsync(p => p.Id, ct);
        var published = (await db.Set<ProductListing>().AsNoTracking().Where(l => ids.Contains(l.ProductId) && l.Published).Select(l => l.ProductId).ToListAsync(ct)).ToHashSet();
        List<ProductRelationDto> Of(ProductRelationKind kind) => rows.Where(r => r.Kind == kind && products.ContainsKey(r.RelatedProductId))
            .Select(r => { var p = products[r.RelatedProductId]; return new ProductRelationDto(p.Id, p.Name, p.Sku, p.Price, published.Contains(p.Id)); }).ToList();
        return new ProductRelationsDto(Of(ProductRelationKind.Related), Of(ProductRelationKind.CrossSell));
    }

    public async Task<ProductRelationsDto?> SetAsync(Guid productId, ProductRelationsInput input, CancellationToken ct = default)
    {
        ModelValidator.ValidateOrThrow(input);
        if (!await db.Set<Product>().AnyAsync(p => p.Id == productId, ct)) return null;
        if (await db.Set<ProductVariant>().AnyAsync(v => v.ProductId == productId, ct))
            throw new PiranhaValidationException("productId", "Suggestions belong to the main product, not to one of its variants.");

        foreach (var (name, list) in new[] { (nameof(input.Related), input.Related), (nameof(input.CrossSell), input.CrossSell) })
        {
            if (list.Distinct().Count() != list.Count) throw new PiranhaValidationException(name, "A product can be in a list once.");
            if (list.Contains(productId)) throw new PiranhaValidationException(name, "A product cannot be suggested for itself.");
        }
        var all = input.Related.Concat(input.CrossSell).Distinct().ToList();
        if (all.Count > 0)
        {
            if (await db.Set<Product>().CountAsync(p => all.Contains(p.Id), ct) != all.Count)
                throw new PiranhaValidationException(nameof(input.Related), "One of the products does not exist.");
            if (await db.Set<ProductVariant>().AnyAsync(v => all.Contains(v.ProductId), ct))
                throw new PiranhaValidationException(nameof(input.Related), "Suggest the main product, not one of its variants.");
        }

        var strategy = db.Database.CreateExecutionStrategy();
        var expected = input.Related.Count + input.CrossSell.Count;
        await strategy.ExecuteInTransactionAsync(
            async token =>
            {
                db.ChangeTracker.Clear();
                await db.Set<ProductRelation>().Where(r => r.ProductId == productId).ExecuteDeleteAsync(token);
                var order = 0;
                foreach (var id in input.Related) db.Add(new ProductRelation { ProductId = productId, RelatedProductId = id, Kind = ProductRelationKind.Related, SortOrder = order++ });
                order = 0;
                foreach (var id in input.CrossSell) db.Add(new ProductRelation { ProductId = productId, RelatedProductId = id, Kind = ProductRelationKind.CrossSell, SortOrder = order++ });
                await db.SaveChangesAsync(token);
                return true;
            },
            async token => await db.Set<ProductRelation>().AsNoTracking().CountAsync(r => r.ProductId == productId, token) == expected,
            ct);
        return await GetAsync(productId, ct);
    }

    public async Task<SuggestionsDto?> ForProductAsync(Guid productId, CancellationToken ct = default)
    {
        var id = await ParentAsync(productId, ct);
        if (!await db.Set<ProductListing>().AsNoTracking().AnyAsync(l => l.ProductId == id && l.Published, ct)) return null;
        var rows = await db.Set<ProductRelation>().AsNoTracking().Where(r => r.ProductId == id).OrderBy(r => r.SortOrder).ToListAsync(ct);
        var shown = await ShownAsync(rows.Select(r => r.RelatedProductId).Distinct().ToList(), ct);
        IReadOnlyList<SuggestedProductDto> Of(ProductRelationKind kind) => rows.Where(r => r.Kind == kind && shown.ContainsKey(r.RelatedProductId)).Select(r => shown[r.RelatedProductId]).ToList();
        return new SuggestionsDto(Of(ProductRelationKind.Related), Of(ProductRelationKind.CrossSell));
    }

    public async Task<IReadOnlyList<SuggestedProductDto>> CrossSellForBasketAsync(IReadOnlyCollection<Guid> productIds, CancellationToken ct = default)
    {
        if (productIds.Count == 0) return [];
        // A variant in the basket counts as its parent: that is where the suggestions are kept.
        var sources = new HashSet<Guid>(productIds);
        var variants = await db.Set<ProductVariant>().AsNoTracking().Where(v => productIds.Contains(v.ProductId)).Select(v => new { v.ProductId, v.ParentProductId }).ToListAsync(ct);
        foreach (var v in variants) sources.Add(v.ParentProductId);
        var inBasket = sources.Concat(productIds).ToHashSet();
        var sourceList = sources.ToList();

        var rows = await db.Set<ProductRelation>().AsNoTracking().Where(r => r.Kind == ProductRelationKind.CrossSell && sourceList.Contains(r.ProductId)).ToListAsync(ct);
        var wanted = rows.Where(r => !inBasket.Contains(r.RelatedProductId)).GroupBy(r => r.RelatedProductId)
            .Select(g => new { Id = g.Key, Votes = g.Select(r => r.ProductId).Distinct().Count(), Best = g.Min(r => r.SortOrder) })
            .OrderByDescending(x => x.Votes).ThenBy(x => x.Best).ThenBy(x => x.Id).ToList();
        var shown = await ShownAsync(wanted.Select(w => w.Id).ToList(), ct);
        return wanted.Where(w => shown.ContainsKey(w.Id)).Select(w => shown[w.Id]).Take(MaxBasketSuggestions).ToList();
    }

    /// <summary>The products in <paramref name="ids"/> that are on sale, with whether each can be bought now.</summary>
    private async Task<Dictionary<Guid, SuggestedProductDto>> ShownAsync(List<Guid> ids, CancellationToken ct)
    {
        if (ids.Count == 0) return [];
        var location = options.Value.StockLocationId ?? StockLocations.DefaultId;
        var published = (await db.Set<ProductListing>().AsNoTracking().Where(l => ids.Contains(l.ProductId) && l.Published).Select(l => l.ProductId).ToListAsync(ct)).ToHashSet();
        var inStock = (await db.Set<StockLevel>().AsNoTracking().Where(l => ids.Contains(l.ProductId) && l.LocationId == location && l.Quantity > 0).Select(l => l.ProductId).ToListAsync(ct)).ToHashSet();
        return (await db.Set<Product>().AsNoTracking().Where(p => ids.Contains(p.Id)).Select(p => new { p.Id, p.Name, p.Sku, p.Price }).ToListAsync(ct))
            .Where(p => published.Contains(p.Id)).ToDictionary(p => p.Id, p => new SuggestedProductDto(p.Id, p.Name, p.Sku, p.Price, inStock.Contains(p.Id)));
    }
}

internal sealed class ProductRelationEndpoints : IApiEndpointContributor
{
    public void MapEndpoints(IEndpointRouteBuilder api)
    {
        var g = api.MapGroup("/products/{productId:guid}/relations").WithTags("Related products");
        g.MapGet("/", async (Guid productId, IProductRelationService svc, CancellationToken ct) =>
            await svc.GetAsync(productId, ct) is { } r ? Results.Ok(r) : Results.NotFound());
        g.MapPut("/", async (Guid productId, ProductRelationsInput input, IProductRelationService svc, CancellationToken ct) =>
            await svc.SetAsync(productId, input, ct) is { } r ? Results.Ok(r) : Results.NotFound()).RequireAuthorization(ApiPolicies.Write);
    }
}

internal sealed class ProductRelationStorefrontEndpoints : IStorefrontEndpointContributor
{
    public void MapEndpoints(IEndpointRouteBuilder storefront)
    {
        storefront.MapGet("/products/{productId:guid}/suggestions", async (Guid productId, IProductRelationService svc, CancellationToken ct) =>
            await svc.ForProductAsync(productId, ct) is { } r ? Results.Ok(r) : Results.NotFound()).AllowAnonymous().WithTags("Storefront suggestions");

        // For a basket: the product ids come from the caller (the shop front knows what is in the basket), so this works for guest baskets and signed-in ones alike.
        storefront.MapGet("/cross-sell", async (Guid[]? productIds, IProductRelationService svc, CancellationToken ct) =>
            Results.Ok(await svc.CrossSellForBasketAsync((productIds ?? []).Take(100).ToList(), ct))).AllowAnonymous().WithTags("Storefront suggestions");
    }
}
