using System.Security.Claims;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using PiranhaCommerce.Api;
using PiranhaCommerce.Catalog;
using PiranhaCommerce.Core.Paging;
using PiranhaCommerce.Customers;
using PiranhaCommerce.Data;

namespace PiranhaCommerce.Ecommerce;

public enum ShopSort { Name = 0, PriceLowToHigh = 1, PriceHighToLow = 2, Newest = 3 }

/// <summary>What a shopper browsing the shop asks for. All of it is optional.</summary>
public sealed class ShopProductQuery
{
    /// <summary>A category (its sub-categories count too), by id.</summary>
    public Guid? CategoryId { get; set; }

    /// <summary>A category by its slug, when the id is not known.</summary>
    public string? CategorySlug { get; set; }

    /// <summary>Words to find in the name, SKU or description (every word must appear).</summary>
    public string? Search { get; set; }

    public ShopSort Sort { get; set; }

    /// <summary>Limits are on the product's regular price, before any sale or group price.</summary>
    public decimal? MinPrice { get; set; }
    public decimal? MaxPrice { get; set; }

    /// <summary>Only products that can be bought now.</summary>
    public bool InStockOnly { get; set; }

    /// <summary>Brands by slug (a brand with no slug by id). A product matches when it carries any of them. Names that match no brand are ignored, and when none match, nothing does.</summary>
    public List<string> Brands { get; set; } = [];

    /// <summary>
    /// Specification values as <c>attribute:value</c> slugs (<c>finish:matte</c>). Values of one attribute are alternatives (any of them); different attributes all apply.
    /// An attribute that is unknown or not filterable is ignored. Values that match none of a known attribute's values make nothing match.
    /// </summary>
    public List<string> Specs { get; set; } = [];

    /// <summary>Tags by slug. A product matches when it has any of them. Slugs that match no tag are ignored, and when none match, nothing does.</summary>
    public List<string> Tags { get; set; } = [];

    public int? Page { get; set; }
    public int? PageSize { get; set; }

    /// <summary>The destination, for tax-inclusive prices (the shop's home country when empty).</summary>
    public string? CountryCode { get; set; }
}

public sealed record ShopImageDto(Guid Id, string Url, string? AltText, int? Width, int? Height);

public sealed record ShopCategoryDto(
    Guid Id, string Name, string? Slug, string? Description, string? MetaTitle, string? MetaDescription, int ProductCount, IReadOnlyList<ShopCategoryDto> Children);

public sealed record ShopCategoryRefDto(Guid Id, string Name, string? Slug);

public sealed record ShopBrandRefDto(Guid Id, string Name, string? Slug);

/// <param name="ProductCount">Products on sale that carry the brand.</param>
public sealed record ShopBrandDto(Guid Id, string Name, string? Slug, string? Description, string? MetaTitle, string? MetaDescription, int ProductCount);

/// <param name="Key">What to send back to select it (a brand's slug, for example).</param>
/// <param name="Count">How many products match with this value chosen and every other filter as it is.</param>
public sealed record ShopFacetValueDto(string Key, string Label, int Count, bool Selected);

/// <param name="Key">The query name the facet's values are sent in (<c>brand</c>, or <c>spec</c> for every specification attribute).</param>
/// <param name="Attribute">For a specification facet, the attribute's slug (its values' keys are <c>attribute:value</c>). Empty for the brand facet.</param>
public sealed record ShopFacetDto(string Key, string Name, IReadOnlyList<ShopFacetValueDto> Values, string? Attribute = null);

public sealed record ShopTagRefDto(string Name, string Slug);

/// <param name="ProductCount">Products on sale that have the tag.</param>
public sealed record ShopTagDto(Guid Id, string Name, string Slug, int ProductCount);

/// <summary>One line of a product's specification table: an attribute and the values the product has.</summary>
public sealed record ShopSpecDto(string Name, string Slug, IReadOnlyList<string> Values);

/// <param name="Price">What this shopper pays. A product with variants shows its cheapest variant's price here (<paramref name="HasVariants"/> says so).</param>
/// <param name="Available">Can be bought now (a product with variants: at least one variant can).</param>
/// <param name="Left">How many are left, only when few are (so a page can say "Only 3 left").</param>
public sealed record ShopProductSummaryDto(
    Guid Id, string Name, string? Sku, string? Slug, string? Summary, ShopperPriceDto Price, bool HasVariants, bool Available, int? Left,
    ShopImageDto? Image, int ReviewCount, decimal ReviewAverage, ShopBrandRefDto? Brand = null);

/// <param name="Images">The variant's own pictures. Empty means use the product's.</param>
public sealed record ShopVariantDto(
    Guid ProductId, string Name, string? Sku, IReadOnlyList<VariantValueDto> Values, ShopperPriceDto Price, bool Available, int? Left, IReadOnlyList<ShopImageDto> Images);

/// <param name="SelectedVariantId">Set when the product was asked for by one of its variants' ids.</param>
public sealed record ShopProductDto(
    Guid Id, string Name, string? Sku, string? Slug, string? Description, string? MetaTitle, string? MetaDescription, ShopperPriceDto Price, bool HasVariants,
    bool Available, int? Left, int? MinOrderQuantity, int? MaxOrderQuantity, IReadOnlyList<ShopImageDto> Images, IReadOnlyList<ShopCategoryRefDto> Categories,
    IReadOnlyList<ProductOptionDto> Options, IReadOnlyList<ShopVariantDto> Variants, ReviewSummaryDto Reviews, Guid? SelectedVariantId = null, ShopBrandRefDto? Brand = null, IReadOnlyList<ShopSpecDto>? Specs = null, IReadOnlyList<ShopTagRefDto>? Tags = null);

/// <summary>
/// What a shopper sees of the catalogue: only categories that are switched on and products that are published, with the price this shopper pays
/// (sales, group prices, quantity breaks and tax, from the same calculation baskets and checkout use), whether each can be bought, pictures and review totals.
/// </summary>
public interface IShopCatalogueService
{
    /// <summary>The category tree: active categories only (a switched-off category hides its sub-categories), with the number of products on sale in each.</summary>
    Task<IReadOnlyList<ShopCategoryDto>> CategoriesAsync(CancellationToken ct = default);

    Task<PagedResult<ShopProductSummaryDto>> ListAsync(ShopProductQuery query, Guid? customerId, CancellationToken ct = default);

    /// <summary>Active brands that have products on sale, with how many, by display order then name.</summary>
    Task<IReadOnlyList<ShopBrandDto>> BrandsAsync(CancellationToken ct = default);

    /// <summary>Tags that have products on sale, with how many, by name. Tags are for browsing by link (<c>tag=slug</c>); they are not a filter facet.</summary>
    Task<IReadOnlyList<ShopTagDto>> TagsAsync(CancellationToken ct = default);

    /// <summary>
    /// The filters a shopper can still use on this listing, with how many products each would give. Each facet is counted with every other filter
    /// applied except its own, so choosing one value shows what the others would add (values within one facet are alternatives; facets narrow each other).
    /// A value is left out when it would give nothing and is not chosen.
    /// </summary>
    Task<IReadOnlyList<ShopFacetDto>> FacetsAsync(ShopProductQuery query, CancellationToken ct = default);

    /// <summary>Null when there is no such product or it is not on sale. A variant's id gives its parent, with <c>SelectedVariantId</c> set.</summary>
    Task<ShopProductDto?> GetAsync(Guid productId, Guid? customerId, string? countryCode = null, CancellationToken ct = default);

    Task<ShopProductDto?> GetBySlugAsync(string slug, Guid? customerId, string? countryCode = null, CancellationToken ct = default);
}

internal static class ShopStock
{
    /// <summary>
    /// Which of these products can be bought now: they have stock where online orders take it from, or (for a product that comes in options,
    /// which holds no stock itself) any of their variants does.
    /// </summary>
    public static async Task<HashSet<Guid>> InStockAsync(PiranhaDbContext db, IReadOnlyCollection<Guid> ids, Guid location, CancellationToken ct)
    {
        var own = await db.Set<StockLevel>().AsNoTracking().Where(l => ids.Contains(l.ProductId) && l.LocationId == location && l.Quantity > 0).Select(l => l.ProductId).ToListAsync(ct);
        var viaVariants = await db.Set<ProductVariant>().AsNoTracking().Where(v => ids.Contains(v.ParentProductId)
                && db.Set<StockLevel>().Any(l => l.ProductId == v.ProductId && l.LocationId == location && l.Quantity > 0))
            .Select(v => v.ParentProductId).ToListAsync(ct);
        return [.. own, .. viaVariants];
    }
}

internal static class ShopImages
{
    /// <summary>Each product's main picture, in one query.</summary>
    public static async Task<Dictionary<Guid, ShopImageDto>> FirstAsync(PiranhaDbContext db, IReadOnlyCollection<Guid> ids, CancellationToken ct)
    {
        var rows = await db.Set<ProductImage>().AsNoTracking().Where(i => ids.Contains(i.ProductId)).OrderBy(i => i.Position).ThenBy(i => i.Id).ToListAsync(ct);
        return rows.GroupBy(i => i.ProductId).ToDictionary(g => g.Key, g => { var i = g.First(); return new ShopImageDto(i.Id, ProductImageUrls.Prefix + i.Id, i.AltText, i.Width, i.Height); });
    }
}

internal sealed class ShopCatalogueService(PiranhaDbContext db, IGroupPricingService pricing, IReviewService reviews, IOptions<EcommerceOptions> options) : IShopCatalogueService
{
    public const int LowStockAt = 5;

    private Guid Location => options.Value.StockLocationId ?? StockLocations.DefaultId;

    private sealed record CategoryRow(Guid Id, string Name, string? Slug, string? Description, string? MetaTitle, string? MetaDescription, Guid? ParentId, int DisplayOrder);

    private async Task<List<CategoryRow>> ActiveCategoriesAsync(CancellationToken ct)
    {
        var all = await db.Set<Category>().AsNoTracking().OrderBy(c => c.DisplayOrder).ThenBy(c => c.Name).ThenBy(c => c.Id)
            .Select(c => new { c.Id, c.Name, c.Slug, c.Description, c.MetaTitle, c.MetaDescription, c.ParentId, c.DisplayOrder, c.Active }).ToListAsync(ct);
        var byId = all.ToDictionary(c => c.Id);
        bool Shown(Guid? id, int depth = 0) => id is null || (depth < 100 && byId.TryGetValue(id.Value, out var c) && c.Active && Shown(c.ParentId, depth + 1));
        return all.Where(c => c.Active && Shown(c.ParentId)).Select(c => new CategoryRow(c.Id, c.Name, c.Slug, c.Description, c.MetaTitle, c.MetaDescription, c.ParentId, c.DisplayOrder)).ToList();
    }

    /// <summary>Products a shopper can see in listings: published and not a variant (a variant is reached through its parent).</summary>
    private IQueryable<Product> Listed() => db.Set<Product>().AsNoTracking()
        .Where(p => db.Set<ProductListing>().Any(l => l.ProductId == p.Id && l.Published) && !db.Set<ProductVariant>().Any(v => v.ProductId == p.Id));

    public async Task<IReadOnlyList<ShopCategoryDto>> CategoriesAsync(CancellationToken ct = default)
    {
        var rows = await ActiveCategoriesAsync(ct);
        var links = await Listed().SelectMany(p => db.Set<ProductCategory>().Where(c => c.ProductId == p.Id).Select(c => new { c.CategoryId, ProductId = p.Id })).ToListAsync(ct);
        var direct = links.GroupBy(l => l.CategoryId).ToDictionary(g => g.Key, g => g.Select(l => l.ProductId).ToHashSet());
        var children = rows.ToLookup(r => r.ParentId);

        (ShopCategoryDto Dto, HashSet<Guid> Products) Build(CategoryRow row)
        {
            var products = new HashSet<Guid>(direct.GetValueOrDefault(row.Id) ?? []);
            var kids = new List<ShopCategoryDto>();
            foreach (var child in children[row.Id])
            {
                var (dto, childProducts) = Build(child);
                kids.Add(dto);
                products.UnionWith(childProducts);
            }
            return (new ShopCategoryDto(row.Id, row.Name, row.Slug, row.Description, row.MetaTitle, row.MetaDescription, products.Count, kids), products);
        }

        var known = rows.Select(r => r.Id).ToHashSet();
        return rows.Where(r => r.ParentId is null || !known.Contains(r.ParentId.Value)).Select(r => Build(r).Dto).ToList();
    }

    /// <summary>Active brands as a shopper sees them: not switched off.</summary>
    private async Task<List<BrandRow>> ShownBrandsAsync(CancellationToken ct) =>
        (await db.Set<Brand>().AsNoTracking().Where(b => b.Active).OrderBy(b => b.DisplayOrder).ThenBy(b => b.Name).ThenBy(b => b.Id)
            .Select(b => new BrandRow(b.Id, b.Name, b.Slug, b.Description, b.MetaTitle, b.MetaDescription)).ToListAsync(ct));

    private sealed record BrandRow(Guid Id, string Name, string? Slug, string? Description, string? MetaTitle, string? MetaDescription)
    {
        public string Key => Slug ?? Id.ToString();
    }

    private const string BrandFacet = "brand";

    private static string SpecFacet(string attributeSlug) => "spec:" + attributeSlug;

    private sealed record SpecAttributeRow(Guid Id, string Name, string Slug, List<SpecValueRow> Values);
    private sealed record SpecValueRow(Guid Id, string Value, string Slug);

    /// <summary>The attributes shoppers can filter by that have at least one value, with their values in display order.</summary>
    private async Task<List<SpecAttributeRow>> FilterableSpecsAsync(CancellationToken ct)
    {
        var attributes = await db.Set<SpecAttribute>().AsNoTracking().Where(a => a.Filterable).OrderBy(a => a.DisplayOrder).ThenBy(a => a.Name).ThenBy(a => a.Id)
            .Select(a => new { a.Id, a.Name, a.Slug }).ToListAsync(ct);
        var ids = attributes.Select(a => a.Id).ToList();
        var values = await db.Set<SpecValue>().AsNoTracking().Where(v => ids.Contains(v.AttributeId)).OrderBy(v => v.DisplayOrder).ThenBy(v => v.Value).ThenBy(v => v.Id)
            .Select(v => new { v.Id, v.AttributeId, v.Value, v.Slug }).ToListAsync(ct);
        return attributes.Select(a => new SpecAttributeRow(a.Id, a.Name, a.Slug, values.Where(v => v.AttributeId == a.Id).Select(v => new SpecValueRow(v.Id, v.Value, v.Slug)).ToList()))
            .Where(a => a.Values.Count > 0).ToList();
    }

    /// <summary>Splits <c>attribute:value</c> keys into the chosen value slugs of each attribute, lower-cased.</summary>
    private static Dictionary<string, HashSet<string>> ChosenSpecs(IEnumerable<string> specs)
    {
        var chosen = new Dictionary<string, HashSet<string>>();
        foreach (var raw in specs)
        {
            var at = raw.IndexOf(':');
            if (at <= 0 || at == raw.Length - 1) continue;
            var attribute = raw[..at].Trim().ToLowerInvariant();
            var value = raw[(at + 1)..].Trim().ToLowerInvariant();
            if (attribute.Length == 0 || value.Length == 0) continue;
            if (!chosen.TryGetValue(attribute, out var set)) chosen[attribute] = set = [];
            set.Add(value);
        }
        return chosen;
    }

    /// <summary>
    /// The products a query selects, as a query to count or page. Null when nothing can match (an unknown category, or brands that match none).
    /// <paramref name="leaveOut"/> names a facet whose own filter is skipped, so its counts show the alternatives.
    /// </summary>
    private async Task<IQueryable<Product>?> FilteredAsync(ShopProductQuery q, string? leaveOut, CancellationToken ct)
    {
        var query = Listed();

        if (q.CategoryId is not null || !string.IsNullOrWhiteSpace(q.CategorySlug))
        {
            var rows = await ActiveCategoriesAsync(ct);
            var slug = q.CategorySlug?.Trim().ToLowerInvariant();
            var root = rows.FirstOrDefault(r => q.CategoryId is not null ? r.Id == q.CategoryId : r.Slug == slug);
            if (root is null) return null;
            var ids = new HashSet<Guid> { root.Id };
            for (var grew = true; grew;) { grew = false; foreach (var r in rows.Where(r => r.ParentId is { } p && ids.Contains(p))) grew |= ids.Add(r.Id); }
            var inCategories = ids.ToList();
            query = query.Where(p => db.Set<ProductCategory>().Any(c => c.ProductId == p.Id && inCategories.Contains(c.CategoryId)));
        }

        foreach (var word in (q.Search ?? "").ToLowerInvariant().Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries).Take(8))
        {
            var w = word; // ToLower on both sides: case-insensitive on every provider
            query = query.Where(p => p.Name.ToLower().Contains(w) || (p.Sku != null && p.Sku.ToLower().Contains(w)) || (p.Description != null && p.Description.ToLower().Contains(w))
                || db.Set<ProductTag>().Any(l => l.ProductId == p.Id && db.Set<Tag>().Any(t => t.Id == l.TagId && t.Name.ToLower().Contains(w))));
        }

        if (q.MinPrice is { } min) query = query.Where(p => p.Price >= min);
        if (q.MaxPrice is { } max) query = query.Where(p => p.Price <= max);
        if (q.InStockOnly)
        {
            var location = Location;
            query = query.Where(p => db.Set<StockLevel>().Any(l => l.LocationId == location && l.Quantity > 0
                && (l.ProductId == p.Id || db.Set<ProductVariant>().Any(v => v.ParentProductId == p.Id && v.ProductId == l.ProductId))));
        }

        if (leaveOut != BrandFacet && q.Brands.Count > 0)
        {
            var wanted = q.Brands.Select(b => b.Trim().ToLowerInvariant()).Where(b => b.Length > 0).ToHashSet();
            var matching = (await ShownBrandsAsync(ct)).Where(b => wanted.Contains(b.Key.ToLowerInvariant())).Select(b => (Guid?)b.Id).ToList();
            if (matching.Count == 0) return null;
            query = query.Where(p => matching.Contains(p.BrandId));
        }

        if (q.Tags.Count > 0)
        {
            var wanted = q.Tags.Select(t => t.Trim().ToLowerInvariant()).Where(t => t.Length > 0).ToList();
            var tagIds = await db.Set<Tag>().AsNoTracking().Where(t => wanted.Contains(t.Slug)).Select(t => t.Id).ToListAsync(ct);
            if (tagIds.Count == 0) return null;
            query = query.Where(p => db.Set<ProductTag>().Any(l => l.ProductId == p.Id && tagIds.Contains(l.TagId)));
        }

        if (q.Specs.Count > 0)
        {
            var chosen = ChosenSpecs(q.Specs);
            foreach (var attribute in await FilterableSpecsAsync(ct))
            {
                if (leaveOut == SpecFacet(attribute.Slug) || !chosen.TryGetValue(attribute.Slug, out var wanted)) continue;
                var valueIds = attribute.Values.Where(v => wanted.Contains(v.Slug)).Select(v => v.Id).ToList();
                if (valueIds.Count == 0) return null;
                query = query.Where(p => db.Set<ProductSpecValue>().Any(l => l.ProductId == p.Id && valueIds.Contains(l.ValueId)));
            }
        }

        return query;
    }

    public async Task<IReadOnlyList<ShopBrandDto>> BrandsAsync(CancellationToken ct = default)
    {
        var counts = await Listed().Where(p => p.BrandId != null).GroupBy(p => p.BrandId).Select(g => new { Id = g.Key, N = g.Count() }).ToListAsync(ct);
        var n = counts.ToDictionary(c => c.Id!.Value, c => c.N);
        return (await ShownBrandsAsync(ct)).Where(b => n.ContainsKey(b.Id)).Select(b => new ShopBrandDto(b.Id, b.Name, b.Slug, b.Description, b.MetaTitle, b.MetaDescription, n[b.Id])).ToList();
    }

    public async Task<IReadOnlyList<ShopTagDto>> TagsAsync(CancellationToken ct = default)
    {
        var counts = await Listed().SelectMany(p => db.Set<ProductTag>().Where(l => l.ProductId == p.Id).Select(l => l.TagId)).GroupBy(id => id)
            .Select(g => new { Id = g.Key, N = g.Count() }).ToDictionaryAsync(x => x.Id, x => x.N, ct);
        var ids = counts.Keys.ToList();
        var tags = await db.Set<Tag>().AsNoTracking().Where(t => ids.Contains(t.Id)).OrderBy(t => t.Name).ThenBy(t => t.Id).Select(t => new { t.Id, t.Name, t.Slug }).ToListAsync(ct);
        return tags.Select(t => new ShopTagDto(t.Id, t.Name, t.Slug, counts[t.Id])).ToList();
    }

    public async Task<IReadOnlyList<ShopFacetDto>> FacetsAsync(ShopProductQuery q, CancellationToken ct = default)
    {
        var facets = new List<ShopFacetDto>();

        if (await FilteredAsync(q, BrandFacet, ct) is { } forBrands)
        {
            var counts = await forBrands.Where(p => p.BrandId != null).GroupBy(p => p.BrandId).Select(g => new { Id = g.Key, N = g.Count() }).ToListAsync(ct);
            var n = counts.ToDictionary(c => c.Id!.Value, c => c.N);
            var chosen = q.Brands.Select(b => b.Trim().ToLowerInvariant()).ToHashSet();
            var values = (await ShownBrandsAsync(ct))
                .Select(b => new ShopFacetValueDto(b.Key, b.Name, n.GetValueOrDefault(b.Id), chosen.Contains(b.Key.ToLowerInvariant())))
                .Where(v => v.Count > 0 || v.Selected).ToList();
            if (values.Count > 0) facets.Add(new ShopFacetDto(BrandFacet, "Brand", values));
        }

        var chosenSpecs = ChosenSpecs(q.Specs);
        foreach (var attribute in await FilterableSpecsAsync(ct))
        {
            if (await FilteredAsync(q, SpecFacet(attribute.Slug), ct) is not { } forAttribute) continue;
            var valueIds = attribute.Values.Select(v => v.Id).ToList();
            var n = await db.Set<ProductSpecValue>().AsNoTracking().Where(l => valueIds.Contains(l.ValueId) && forAttribute.Select(p => p.Id).Contains(l.ProductId))
                .GroupBy(l => l.ValueId).Select(g => new { Id = g.Key, N = g.Count() }).ToDictionaryAsync(x => x.Id, x => x.N, ct);
            var picked = chosenSpecs.GetValueOrDefault(attribute.Slug) ?? [];
            var values = attribute.Values.Select(v => new ShopFacetValueDto(attribute.Slug + ":" + v.Slug, v.Value, n.GetValueOrDefault(v.Id), picked.Contains(v.Slug)))
                .Where(v => v.Count > 0 || v.Selected).ToList();
            if (values.Count > 0) facets.Add(new ShopFacetDto("spec", attribute.Name, values, attribute.Slug));
        }

        return facets;
    }

    public async Task<PagedResult<ShopProductSummaryDto>> ListAsync(ShopProductQuery q, Guid? customerId, CancellationToken ct = default)
    {
        var (page, size) = Paging.Normalize(q.Page, q.PageSize);
        if (await FilteredAsync(q, null, ct) is not { } query) return new PagedResult<ShopProductSummaryDto>([], page, size, 0);

        var total = await query.CountAsync(ct);
        var ordered = q.Sort switch
        {
            ShopSort.PriceLowToHigh => query.OrderBy(p => p.Price).ThenBy(p => p.Name).ThenBy(p => p.Id),
            ShopSort.PriceHighToLow => query.OrderByDescending(p => p.Price).ThenBy(p => p.Name).ThenBy(p => p.Id),
            ShopSort.Newest => query.OrderByDescending(p => p.CreatedUtc).ThenBy(p => p.Name).ThenBy(p => p.Id),
            _ => query.OrderBy(p => p.Name).ThenBy(p => p.Id),
        };
        var products = await ordered.Skip((page - 1) * size).Take(size).Select(p => new { p.Id, p.Name, p.Sku, p.Slug, p.Description, p.BrandId }).ToListAsync(ct);
        var parentIds = products.Select(p => p.Id).ToList();

        var variants = await PublishedVariantsAsync(parentIds, ct);
        var priced = parentIds.Concat(variants.Values.SelectMany(v => v)).ToList();
        var prices = (await pricing.PricesAsync(customerId, priced, q.CountryCode, ct)).ToDictionary(x => x.ProductId);
        var stock = await StockAsync(priced, ct);
        var images = await FirstImagesAsync(parentIds, ct);
        var review = await reviews.SummariesAsync(parentIds, ct);
        var shownBrands = (await ShownBrandsAsync(ct)).ToDictionary(b => b.Id);

        var items = products.Select(p =>
        {
            var vs = variants.GetValueOrDefault(p.Id) ?? [];
            var (price, available, left) = Summarise(p.Id, vs, prices, stock);
            var (n, avg) = review.GetValueOrDefault(p.Id);
            return new ShopProductSummaryDto(p.Id, p.Name, p.Sku, p.Slug, Shorten(p.Description), price, vs.Count > 0, available, left, images.GetValueOrDefault(p.Id), n, avg, BrandRef(p.BrandId, shownBrands));
        }).ToList();
        return new PagedResult<ShopProductSummaryDto>(items, page, size, total);
    }

    private static ShopBrandRefDto? BrandRef(Guid? id, Dictionary<Guid, BrandRow> shown) =>
        id is { } b && shown.TryGetValue(b, out var row) ? new ShopBrandRefDto(row.Id, row.Name, row.Slug) : null;

    public async Task<ShopProductDto?> GetAsync(Guid productId, Guid? customerId, string? countryCode = null, CancellationToken ct = default)
    {
        var parentId = await db.Set<ProductVariant>().AsNoTracking().Where(v => v.ProductId == productId).Select(v => (Guid?)v.ParentProductId).FirstOrDefaultAsync(ct);
        return await BuildAsync(parentId ?? productId, parentId is null ? null : productId, customerId, countryCode, ct);
    }

    public async Task<ShopProductDto?> GetBySlugAsync(string slug, Guid? customerId, string? countryCode = null, CancellationToken ct = default)
    {
        var s = (slug ?? "").Trim().ToLowerInvariant();
        if (s.Length == 0) return null;
        var id = await db.Set<Product>().AsNoTracking().Where(p => p.Slug == s).Select(p => (Guid?)p.Id).FirstOrDefaultAsync(ct);
        return id is null ? null : await GetAsync(id.Value, customerId, countryCode, ct);
    }

    private async Task<ShopProductDto?> BuildAsync(Guid id, Guid? selectedVariant, Guid? customerId, string? countryCode, CancellationToken ct)
    {
        var product = await db.Set<Product>().AsNoTracking().Where(p => p.Id == id).FirstOrDefaultAsync(ct);
        var listing = await db.Set<ProductListing>().AsNoTracking().FirstOrDefaultAsync(l => l.ProductId == id && l.Published, ct);
        if (product is null || listing is null || await db.Set<ProductVariant>().AnyAsync(v => v.ProductId == id, ct)) return null;

        var variantIds = (await PublishedVariantsAsync([id], ct)).GetValueOrDefault(id) ?? [];
        var all = new List<Guid>(variantIds) { id };
        var prices = (await pricing.PricesAsync(customerId, all, countryCode, ct)).ToDictionary(x => x.ProductId);
        var stock = await StockAsync(all, ct);
        var (price, available, left) = Summarise(id, variantIds, prices, stock);

        var imageRows = await db.Set<ProductImage>().AsNoTracking().Where(i => all.Contains(i.ProductId)).OrderBy(i => i.Position).ThenBy(i => i.Id).ToListAsync(ct);
        ShopImageDto Image(ProductImage i) => new(i.Id, ProductImageUrls.Prefix + i.Id, i.AltText, i.Width, i.Height);

        var options = new List<ProductOptionDto>();
        var variants = new List<ShopVariantDto>();
        if (variantIds.Count > 0)
        {
            var optionRows = await db.Set<ProductOption>().AsNoTracking().Where(o => o.ProductId == id).OrderBy(o => o.Position).ThenBy(o => o.Id).ToListAsync(ct);
            var optionIds = optionRows.Select(o => o.Id).ToList();
            var valueRows = await db.Set<ProductOptionValue>().AsNoTracking().Where(v => optionIds.Contains(v.OptionId)).OrderBy(v => v.Position).ThenBy(v => v.Id).ToListAsync(ct);
            foreach (var o in optionRows) options.Add(new ProductOptionDto(o.Id, o.Name, valueRows.Where(v => v.OptionId == o.Id).Select(v => new OptionValueDto(v.Id, v.Value)).ToList()));

            var chosen = await db.Set<ProductVariantValue>().AsNoTracking().Where(v => variantIds.Contains(v.ProductId)).ToListAsync(ct);
            var names = await db.Set<Product>().AsNoTracking().Where(p => variantIds.Contains(p.Id)).Select(p => new { p.Id, p.Name, p.Sku }).ToDictionaryAsync(p => p.Id, ct);
            foreach (var vid in variantIds)
            {
                var values = options.SelectMany(o => chosen.Where(c => c.ProductId == vid && c.OptionId == o.Id)
                    .Select(c => new VariantValueDto(o.Id, o.Name, c.OptionValueId, o.Values.First(x => x.Id == c.OptionValueId).Value))).ToList();
                var (vp, va, vl) = Summarise(vid, [], prices, stock);
                variants.Add(new ShopVariantDto(vid, names[vid].Name, names[vid].Sku, values, vp, va, vl, imageRows.Where(i => i.ProductId == vid).Select(Image).ToList()));
            }
            variants = variants.OrderBy(v => string.Join("/", v.Values.Select(x => options.First(o => o.Id == x.OptionId).Values.ToList().FindIndex(y => y.Id == x.ValueId).ToString("D4")))).ToList();
        }

        var categories = await ActiveCategoriesAsync(ct);
        var inCategory = await db.Set<ProductCategory>().AsNoTracking().Where(c => c.ProductId == id).Select(c => c.CategoryId).ToListAsync(ct);
        var refs = categories.Where(c => inCategory.Contains(c.Id)).Select(c => new ShopCategoryRefDto(c.Id, c.Name, c.Slug)).ToList();
        var summary = await reviews.SummaryAsync(id, ct) ?? new ReviewSummaryDto(id, 0, 0m, [0, 0, 0, 0, 0]);

        return new ShopProductDto(id, product.Name, product.Sku, product.Slug, product.Description, product.MetaTitle, product.MetaDescription, price, variants.Count > 0,
            available, left, listing.MinOrderQuantity, listing.MaxOrderQuantity, imageRows.Where(i => i.ProductId == id).Select(Image).ToList(), refs, options, variants, summary, selectedVariant,
            BrandRef(product.BrandId, (await ShownBrandsAsync(ct)).ToDictionary(b => b.Id)), await SpecsOfAsync(id, ct), await TagsOfAsync(id, ct));
    }

    private async Task<IReadOnlyList<ShopTagRefDto>> TagsOfAsync(Guid productId, CancellationToken ct) =>
        await db.Set<ProductTag>().AsNoTracking().Where(l => l.ProductId == productId).Join(db.Set<Tag>(), l => l.TagId, t => t.Id, (l, t) => new { t.Name, t.Slug })
            .OrderBy(t => t.Name).Select(t => new ShopTagRefDto(t.Name, t.Slug)).ToListAsync(ct);

    /// <summary>The product's specification table: each attribute it has values for (filterable or not), by display order.</summary>
    private async Task<IReadOnlyList<ShopSpecDto>> SpecsOfAsync(Guid productId, CancellationToken ct)
    {
        var rows = await db.Set<ProductSpecValue>().AsNoTracking().Where(l => l.ProductId == productId)
            .Join(db.Set<SpecValue>(), l => l.ValueId, v => v.Id, (l, v) => v)
            .Join(db.Set<SpecAttribute>(), v => v.AttributeId, a => a.Id, (v, a) => new { a.Name, a.Slug, AttributeOrder = a.DisplayOrder, v.Value, ValueOrder = v.DisplayOrder })
            .ToListAsync(ct);
        return rows.GroupBy(r => new { r.Name, r.Slug, r.AttributeOrder }).OrderBy(g => g.Key.AttributeOrder).ThenBy(g => g.Key.Name)
            .Select(g => new ShopSpecDto(g.Key.Name, g.Key.Slug, g.OrderBy(r => r.ValueOrder).ThenBy(r => r.Value).Select(r => r.Value).ToList())).ToList();
    }

    /// <summary>The cheapest price among the variants (or the product's own), whether any can be bought, and how many are left when few are.</summary>
    private static (ShopperPriceDto Price, bool Available, int? Left) Summarise(
        Guid id, IReadOnlyList<Guid> variants, Dictionary<Guid, ShopperPriceDto> prices, Dictionary<Guid, decimal> stock)
    {
        var buyable = variants.Count > 0 ? variants : [id];
        var price = buyable.Where(prices.ContainsKey).Select(b => prices[b]).OrderBy(p => p.Price).FirstOrDefault() ?? prices.GetValueOrDefault(id) ?? new ShopperPriceDto(id, 0m, 0m, false);
        var quantity = buyable.Sum(b => Math.Max(0m, stock.GetValueOrDefault(b)));
        var available = buyable.Any(b => stock.GetValueOrDefault(b) > 0);
        return (price, available, available && variants.Count == 0 && quantity <= LowStockAt ? (int)Math.Floor(quantity) : null);
    }

    private async Task<Dictionary<Guid, List<Guid>>> PublishedVariantsAsync(IReadOnlyCollection<Guid> parentIds, CancellationToken ct)
    {
        if (parentIds.Count == 0) return [];
        var rows = await db.Set<ProductVariant>().AsNoTracking().Where(v => parentIds.Contains(v.ParentProductId) && db.Set<ProductListing>().Any(l => l.ProductId == v.ProductId && l.Published))
            .Select(v => new { v.ParentProductId, v.ProductId }).ToListAsync(ct);
        return rows.GroupBy(r => r.ParentProductId).ToDictionary(g => g.Key, g => g.Select(r => r.ProductId).OrderBy(x => x).ToList());
    }

    private async Task<Dictionary<Guid, decimal>> StockAsync(IReadOnlyCollection<Guid> ids, CancellationToken ct)
    {
        var location = Location;
        return await db.Set<StockLevel>().AsNoTracking().Where(l => ids.Contains(l.ProductId) && l.LocationId == location).ToDictionaryAsync(l => l.ProductId, l => l.Quantity, ct);
    }

    private Task<Dictionary<Guid, ShopImageDto>> FirstImagesAsync(IReadOnlyCollection<Guid> ids, CancellationToken ct) => ShopImages.FirstAsync(db, ids, ct);

    private static string? Shorten(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var t = text.Trim();
        var firstLine = t.Split('\n', 2)[0].Trim();
        return firstLine.Length <= 160 ? firstLine : firstLine[..157].TrimEnd() + "...";
    }
}

internal sealed class ShopCatalogueEndpoints : IStorefrontEndpointContributor
{
    public void MapEndpoints(IEndpointRouteBuilder storefront)
    {
        // Open to everyone. A shopper who sends their token also sees their own prices (groups); anyone else sees sale and regular prices.
        storefront.MapGet("/categories", async (IShopCatalogueService svc, CancellationToken ct) => Results.Ok(await svc.CategoriesAsync(ct))).AllowAnonymous().WithTags("Storefront catalogue");

        storefront.MapGet("/products", async (Guid? categoryId, string? category, string? q, ShopSort? sort, decimal? minPrice, decimal? maxPrice, bool? inStock, string[]? brand, string[]? spec, string[]? tag, int? page, int? pageSize, string? countryCode,
                ClaimsPrincipal user, IShopCatalogueService svc, CancellationToken ct) =>
            Results.Ok(await svc.ListAsync(new ShopProductQuery
            {
                CategoryId = categoryId, CategorySlug = category, Search = q, Sort = sort ?? ShopSort.Name, MinPrice = minPrice, MaxPrice = maxPrice,
                InStockOnly = inStock == true, Brands = [.. brand ?? []], Specs = [.. spec ?? []], Tags = [.. tag ?? []], Page = page, PageSize = pageSize, CountryCode = countryCode,
            }, user.CustomerId(), ct))).AllowAnonymous().WithTags("Storefront catalogue");

        // The filters left for a listing, with counts. Takes the same query values as /products.
        storefront.MapGet("/products/facets", async (Guid? categoryId, string? category, string? q, decimal? minPrice, decimal? maxPrice, bool? inStock, string[]? brand, string[]? spec, string[]? tag,
                IShopCatalogueService svc, CancellationToken ct) =>
            Results.Ok(await svc.FacetsAsync(new ShopProductQuery
            {
                CategoryId = categoryId, CategorySlug = category, Search = q, MinPrice = minPrice, MaxPrice = maxPrice, InStockOnly = inStock == true, Brands = [.. brand ?? []], Specs = [.. spec ?? []], Tags = [.. tag ?? []],
            }, ct))).AllowAnonymous().WithTags("Storefront catalogue");

        storefront.MapGet("/tags", async (IShopCatalogueService svc, CancellationToken ct) => Results.Ok(await svc.TagsAsync(ct))).AllowAnonymous().WithTags("Storefront catalogue");

        storefront.MapGet("/brands", async (IShopCatalogueService svc, CancellationToken ct) => Results.Ok(await svc.BrandsAsync(ct))).AllowAnonymous().WithTags("Storefront catalogue");

        storefront.MapGet("/products/{productId:guid}", async (Guid productId, string? countryCode, ClaimsPrincipal user, IShopCatalogueService svc, CancellationToken ct) =>
            await svc.GetAsync(productId, user.CustomerId(), countryCode, ct) is { } p ? Results.Ok(p) : Results.NotFound()).AllowAnonymous().WithTags("Storefront catalogue");

        storefront.MapGet("/products/by-slug/{slug}", async (string slug, string? countryCode, ClaimsPrincipal user, IShopCatalogueService svc, CancellationToken ct) =>
            await svc.GetBySlugAsync(slug, user.CustomerId(), countryCode, ct) is { } p ? Results.Ok(p) : Results.NotFound()).AllowAnonymous().WithTags("Storefront catalogue");
    }
}
