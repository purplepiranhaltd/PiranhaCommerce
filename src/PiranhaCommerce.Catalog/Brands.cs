using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using PiranhaCommerce.Api;
using PiranhaCommerce.Core.Audit;
using PiranhaCommerce.Core.Domain;
using PiranhaCommerce.Core.Validation;
using PiranhaCommerce.Data;

namespace PiranhaCommerce.Catalog;

/// <summary>A maker or label whose products are sold (shared by every sales channel). A product has at most one.</summary>
[Audited]
public class Brand : AuditableEntity
{
    public required string Name { get; set; }

    /// <summary>The name in upper case, trimmed: what makes names unique whatever their letter case.</summary>
    public required string NormalizedName { get; set; }

    public string? Description { get; set; }
    public int DisplayOrder { get; set; }
    public bool Active { get; set; } = true;

    /// <summary>The address segment a shop front uses for this brand (lower case letters, digits and single hyphens). Unique. Empty means none.</summary>
    public string? Slug { get; set; }
    public string? MetaTitle { get; set; }
    public string? MetaDescription { get; set; }
}

public sealed class BrandInput
{
    [Required, StringLength(200, MinimumLength = 1)]
    public string Name { get; set; } = "";

    [StringLength(4000)]
    public string? Description { get; set; }

    [Range(0, 1_000_000)]
    public int DisplayOrder { get; set; }

    /// <summary>An inactive brand is hidden from shoppers, products and all; staff still see it.</summary>
    public bool Active { get; set; } = true;

    /// <summary>Lower case letters, digits and single hyphens, unique among brands (it is lower-cased for you). A PUT replaces it: leave it out to clear it.</summary>
    [StringLength(200)]
    public string? Slug { get; set; }

    [StringLength(200)]
    public string? MetaTitle { get; set; }

    [StringLength(500)]
    public string? MetaDescription { get; set; }
}

/// <param name="ProductCount">Products (not variants) that carry this brand.</param>
public sealed record BrandDto(
    Guid Id, string Name, string? Description, int DisplayOrder, bool Active, string? Slug, string? MetaTitle, string? MetaDescription,
    int ProductCount, DateTimeOffset CreatedUtc, DateTimeOffset? UpdatedUtc);

/// <summary>Sets or clears a product's brand.</summary>
public sealed class ProductBrandInput
{
    /// <summary>The brand, or null for none.</summary>
    public Guid? BrandId { get; set; }
}

/// <param name="Inherited">True for a variant: it shows its parent's brand, and the brand can only be changed on the parent.</param>
public sealed record ProductBrandDto(Guid ProductId, Guid? BrandId, string? BrandName, bool Inherited);

public interface IBrandService
{
    /// <summary>Every brand, with how many products carry it; by display order, then name.</summary>
    Task<IReadOnlyList<BrandDto>> ListAsync(CancellationToken ct = default);
    Task<BrandDto?> GetAsync(Guid id, CancellationToken ct = default);
    Task<BrandDto?> GetBySlugAsync(string slug, CancellationToken ct = default);
    Task<BrandDto> CreateAsync(BrandInput input, CancellationToken ct = default);
    Task<BrandDto?> UpdateAsync(Guid id, BrandInput input, CancellationToken ct = default);

    /// <summary>False when not found. Throws <see cref="PiranhaCommerceConflictException"/> while products still carry the brand.</summary>
    Task<bool> DeleteAsync(Guid id, CancellationToken ct = default);

    /// <summary>Null when the product does not exist. For a variant, its parent's brand.</summary>
    Task<ProductBrandDto?> GetProductBrandAsync(Guid productId, CancellationToken ct = default);

    /// <summary>Null when the product does not exist. A validation error for an unknown brand, or for a variant (set it on the parent).</summary>
    Task<ProductBrandDto?> SetProductBrandAsync(Guid productId, Guid? brandId, CancellationToken ct = default);

    /// <summary>The brand with this name (any letter case), or null.</summary>
    Task<Guid?> FindIdByNameAsync(string name, CancellationToken ct = default);

    /// <summary>The brand with this name, made when there is none. Used by the catalogue import.</summary>
    Task<Guid> GetOrCreateByNameAsync(string name, CancellationToken ct = default);

    /// <summary>The names of the brands the given products carry (a variant shows its parent's), by product id. Products with none are left out.</summary>
    Task<IReadOnlyDictionary<Guid, string>> NamesForProductsAsync(IReadOnlyCollection<Guid> productIds, CancellationToken ct = default);
}

internal sealed class BrandService(PiranhaCommerceDbContext db) : IBrandService
{
    public static string Normalize(string name) => name.Trim().ToUpperInvariant();

    /// <summary>Filter and order the brands first, then project: the projection counts each brand's products in the same query.</summary>
    private IQueryable<BrandDto> Project(IQueryable<Brand> brands) =>
        brands.Select(b => new BrandDto(
            b.Id, b.Name, b.Description, b.DisplayOrder, b.Active, b.Slug, b.MetaTitle, b.MetaDescription,
            db.Set<Product>().Count(p => p.BrandId == b.Id), b.CreatedUtc, b.UpdatedUtc));

    public async Task<IReadOnlyList<BrandDto>> ListAsync(CancellationToken ct = default) =>
        await Project(db.Set<Brand>().AsNoTracking().OrderBy(b => b.DisplayOrder).ThenBy(b => b.Name).ThenBy(b => b.Id)).ToListAsync(ct);

    public Task<BrandDto?> GetAsync(Guid id, CancellationToken ct = default) =>
        Project(db.Set<Brand>().AsNoTracking().Where(b => b.Id == id)).FirstOrDefaultAsync(ct);

    public Task<BrandDto?> GetBySlugAsync(string slug, CancellationToken ct = default)
    {
        var s = slug.Trim().ToLowerInvariant();
        return Project(db.Set<Brand>().AsNoTracking().Where(b => b.Slug == s)).FirstOrDefaultAsync(ct);
    }

    public async Task<BrandDto> CreateAsync(BrandInput input, CancellationToken ct = default)
    {
        ModelValidator.ValidateOrThrow(input);
        await ValidateAsync(input, null, ct);
        var entity = new Brand { Name = input.Name.Trim(), NormalizedName = Normalize(input.Name) };
        Apply(entity, input);
        db.Add(entity);
        await db.SaveChangesAsync(ct);
        return (await GetAsync(entity.Id, ct))!;
    }

    public async Task<BrandDto?> UpdateAsync(Guid id, BrandInput input, CancellationToken ct = default)
    {
        var entity = await db.Set<Brand>().FirstOrDefaultAsync(b => b.Id == id, ct);
        if (entity is null) return null;
        ModelValidator.ValidateOrThrow(input);
        await ValidateAsync(input, id, ct);
        Apply(entity, input);
        await db.SaveChangesAsync(ct);
        return await GetAsync(id, ct);
    }

    public async Task<bool> DeleteAsync(Guid id, CancellationToken ct = default)
    {
        if (!await db.Set<Brand>().AnyAsync(b => b.Id == id, ct)) return false;
        if (await db.Set<Product>().AnyAsync(p => p.BrandId == id, ct))
            throw new PiranhaCommerceConflictException("Products still carry this brand. Move them to another brand, or clear it, first.");
        return await db.DeleteTrackedAsync<Brand>(b => b.Id == id, ct) > 0;
    }

    public async Task<ProductBrandDto?> GetProductBrandAsync(Guid productId, CancellationToken ct = default)
    {
        if (!await db.Set<Product>().AnyAsync(p => p.Id == productId, ct)) return null;
        var parentId = await db.Set<ProductVariant>().AsNoTracking().Where(v => v.ProductId == productId).Select(v => (Guid?)v.ParentProductId).FirstOrDefaultAsync(ct);
        var holder = parentId ?? productId;
        var brand = await db.Set<Product>().AsNoTracking().Where(p => p.Id == holder && p.BrandId != null)
            .Select(p => db.Set<Brand>().Where(b => b.Id == p.BrandId).Select(b => new { b.Id, b.Name }).FirstOrDefault()).FirstOrDefaultAsync(ct);
        return new ProductBrandDto(productId, brand?.Id, brand?.Name, parentId is not null);
    }

    public async Task<ProductBrandDto?> SetProductBrandAsync(Guid productId, Guid? brandId, CancellationToken ct = default)
    {
        var product = await db.Set<Product>().FirstOrDefaultAsync(p => p.Id == productId, ct);
        if (product is null) return null;
        if (await db.Set<ProductVariant>().AnyAsync(v => v.ProductId == productId, ct))
            throw new PiranhaCommerceValidationException(nameof(ProductBrandInput.BrandId), "A variant shows its parent's brand. Set the brand on the parent product.");
        if (brandId is { } id && !await db.Set<Brand>().AnyAsync(b => b.Id == id, ct))
            throw new PiranhaCommerceValidationException(nameof(ProductBrandInput.BrandId), "That brand does not exist.");
        product.BrandId = brandId;
        await db.SaveChangesAsync(ct);
        return await GetProductBrandAsync(productId, ct);
    }

    public async Task<Guid?> FindIdByNameAsync(string name, CancellationToken ct = default)
    {
        var normalized = Normalize(name);
        return await db.Set<Brand>().AsNoTracking().Where(b => b.NormalizedName == normalized).Select(b => (Guid?)b.Id).FirstOrDefaultAsync(ct);
    }

    public async Task<Guid> GetOrCreateByNameAsync(string name, CancellationToken ct = default)
    {
        if (await FindIdByNameAsync(name, ct) is { } existing) return existing;
        var created = await CreateAsync(new BrandInput { Name = name }, ct);
        return created.Id;
    }

    public async Task<IReadOnlyDictionary<Guid, string>> NamesForProductsAsync(IReadOnlyCollection<Guid> productIds, CancellationToken ct = default)
    {
        if (productIds.Count == 0) return new Dictionary<Guid, string>();
        var parents = await db.Set<ProductVariant>().AsNoTracking().Where(v => productIds.Contains(v.ProductId))
            .Select(v => new { v.ProductId, v.ParentProductId }).ToDictionaryAsync(v => v.ProductId, v => v.ParentProductId, ct);
        var holders = productIds.Select(i => parents.GetValueOrDefault(i, i)).Distinct().ToList();
        var names = await db.Set<Product>().AsNoTracking().Where(p => holders.Contains(p.Id) && p.BrandId != null)
            .Select(p => new { p.Id, Name = db.Set<Brand>().Where(b => b.Id == p.BrandId).Select(b => b.Name).FirstOrDefault() })
            .ToDictionaryAsync(x => x.Id, x => x.Name, ct);
        var result = new Dictionary<Guid, string>();
        foreach (var id in productIds)
            if (names.TryGetValue(parents.GetValueOrDefault(id, id), out var n) && n is not null) result[id] = n;
        return result;
    }

    private async Task ValidateAsync(BrandInput input, Guid? existingId, CancellationToken ct)
    {
        var normalized = Normalize(input.Name);
        if (await db.Set<Brand>().AnyAsync(b => b.NormalizedName == normalized && b.Id != existingId, ct))
            throw new PiranhaCommerceValidationException(nameof(BrandInput.Name), "A brand with this name already exists.");
        if (Seo.NormalizeSlug(input.Slug) is { } slug && await db.Set<Brand>().AnyAsync(b => b.Slug == slug && b.Id != existingId, ct))
            throw new PiranhaCommerceValidationException(nameof(BrandInput.Slug), "A brand with this slug already exists.");
    }

    private static void Apply(Brand e, BrandInput i)
    {
        e.Name = i.Name.Trim();
        e.NormalizedName = Normalize(i.Name);
        e.Slug = Seo.NormalizeSlug(i.Slug);
        e.MetaTitle = string.IsNullOrWhiteSpace(i.MetaTitle) ? null : i.MetaTitle.Trim();
        e.MetaDescription = string.IsNullOrWhiteSpace(i.MetaDescription) ? null : i.MetaDescription.Trim();
        e.Description = i.Description;
        e.DisplayOrder = i.DisplayOrder;
        e.Active = i.Active;
    }
}

internal sealed class BrandApiEndpoints : IApiEndpointContributor
{
    public void MapEndpoints(IEndpointRouteBuilder api)
    {
        var brands = api.MapGroup("/brands").WithTags("Brands");

        brands.MapGet("/", async (IBrandService svc, CancellationToken ct) => Results.Ok(await svc.ListAsync(ct)));

        brands.MapGet("/{id:guid}", async (Guid id, IBrandService svc, CancellationToken ct) =>
            await svc.GetAsync(id, ct) is { } b ? Results.Ok(b) : Results.NotFound());

        brands.MapGet("/by-slug/{slug}", async (string slug, IBrandService svc, CancellationToken ct) =>
            await svc.GetBySlugAsync(slug, ct) is { } b ? Results.Ok(b) : Results.NotFound());

        brands.MapPost("/", async (BrandInput input, IBrandService svc, CancellationToken ct) =>
            {
                var created = await svc.CreateAsync(input, ct);
                return Results.Created($"/api/v1/brands/{created.Id}", created);
            })
            .RequireAuthorization(ApiPolicies.Write);

        brands.MapPut("/{id:guid}", async (Guid id, BrandInput input, IBrandService svc, CancellationToken ct) =>
                await svc.UpdateAsync(id, input, ct) is { } b ? Results.Ok(b) : Results.NotFound())
            .RequireAuthorization(ApiPolicies.Write);

        brands.MapDelete("/{id:guid}", async (Guid id, IBrandService svc, CancellationToken ct) =>
                await svc.DeleteAsync(id, ct) ? Results.NoContent() : Results.NotFound())
            .RequireAuthorization(ApiPolicies.Write);

        var productBrand = api.MapGroup("/products/{productId:guid}/brand").WithTags("Product brand");

        productBrand.MapGet("/", async (Guid productId, IBrandService svc, CancellationToken ct) =>
            await svc.GetProductBrandAsync(productId, ct) is { } r ? Results.Ok(r) : Results.NotFound());

        productBrand.MapPut("/", async (Guid productId, ProductBrandInput input, IBrandService svc, CancellationToken ct) =>
                await svc.SetProductBrandAsync(productId, input.BrandId, ct) is { } r ? Results.Ok(r) : Results.NotFound())
            .RequireAuthorization(ApiPolicies.Write);
    }
}
