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

/// <summary>
/// A free-form label on products ("Summer", "Gift idea", "Bestseller"). Unlike a specification there is no fixed list: naming a tag on a product makes it when it is new.
/// A product can have many, and shoppers can browse by them.
/// </summary>
[Audited]
public class Tag : AuditableEntity
{
    public required string Name { get; set; }

    /// <summary>The name in upper case, trimmed: what makes names unique whatever their letter case.</summary>
    public required string NormalizedName { get; set; }

    /// <summary>What a shop front uses in an address. Always set and unique; made from the name unless one is given.</summary>
    public required string Slug { get; set; }
}

/// <summary>Says a product has this tag. Held on the main product, never on a variant.</summary>
public class ProductTag
{
    public Guid ProductId { get; set; }
    public Guid TagId { get; set; }
}

public sealed class TagInput
{
    [Required, StringLength(50, MinimumLength = 1)]
    public string Name { get; set; } = "";

    /// <summary>Lower case letters, digits and single hyphens, unique among tags. Left out, it is made from the name (a name with no letters or digits needs one given).</summary>
    [StringLength(200)]
    public string? Slug { get; set; }
}

/// <param name="ProductCount">Products (not variants) that have this tag.</param>
public sealed record TagDto(Guid Id, string Name, string Slug, int ProductCount, DateTimeOffset CreatedUtc, DateTimeOffset? UpdatedUtc);

public sealed record TagRefDto(Guid Id, string Name, string Slug);

/// <summary>Replaces the whole set of tags a product has.</summary>
public sealed class ProductTagsInput
{
    /// <summary>Tag names, in any letter case. One that does not exist yet is made. At most 50, each up to 50 characters.</summary>
    public List<string> Tags { get; set; } = [];
}

/// <param name="Inherited">True for a variant: it shows its parent's tags, which can only be changed on the parent.</param>
public sealed record ProductTagsDto(Guid ProductId, bool Inherited, IReadOnlyList<TagRefDto> Tags);

public interface ITagService
{
    /// <summary>Every tag with how many products have it; by name.</summary>
    Task<IReadOnlyList<TagDto>> ListAsync(CancellationToken ct = default);
    Task<TagDto?> GetAsync(Guid id, CancellationToken ct = default);
    Task<TagDto?> GetBySlugAsync(string slug, CancellationToken ct = default);
    Task<TagDto> CreateAsync(TagInput input, CancellationToken ct = default);

    /// <summary>Renames a tag everywhere it is used. A PUT replaces: a slug left out is made from the name again.</summary>
    Task<TagDto?> UpdateAsync(Guid id, TagInput input, CancellationToken ct = default);

    /// <summary>False when not found. The tag is taken off every product that has it: it is only a label.</summary>
    Task<bool> DeleteAsync(Guid id, CancellationToken ct = default);

    /// <summary>Null when the product does not exist. For a variant, its parent's tags.</summary>
    Task<ProductTagsDto?> GetProductAsync(Guid productId, CancellationToken ct = default);

    /// <summary>Null when the product does not exist. A validation error for a variant, a bad name, or more than 50. Names that match no tag make one.</summary>
    Task<ProductTagsDto?> SetProductAsync(Guid productId, IReadOnlyCollection<string> names, CancellationToken ct = default);

    /// <summary>Each product's tag names, by name (a variant shows its parent's). Products with none are left out. Used by the catalogue export.</summary>
    Task<IReadOnlyDictionary<Guid, IReadOnlyList<string>>> NamesForProductsAsync(IReadOnlyCollection<Guid> productIds, CancellationToken ct = default);
}

internal sealed class TagService(PiranhaDbContext db) : ITagService
{
    public const int MaxPerProduct = 50;

    public static string Normalize(string name) => name.Trim().ToUpperInvariant();

    private static string SlugFor(string? given, string name)
    {
        var slug = Seo.NormalizeSlug(given) ?? SpecificationService.Slugify(name);
        if (slug.Length == 0) throw new PiranhaValidationException(nameof(TagInput.Slug), $"Give a slug: \"{name}\" has no letters or digits to make one from.");
        return slug;
    }

    /// <summary>Filter and order first, then project: the projection counts each tag's products in the same query.</summary>
    private IQueryable<TagDto> Project(IQueryable<Tag> tags) =>
        tags.Select(t => new TagDto(t.Id, t.Name, t.Slug, db.Set<ProductTag>().Count(l => l.TagId == t.Id), t.CreatedUtc, t.UpdatedUtc));

    public async Task<IReadOnlyList<TagDto>> ListAsync(CancellationToken ct = default) =>
        ByName(await Project(db.Set<Tag>().AsNoTracking()).ToListAsync(ct), t => t.Name, t => t.Id);

    /// <summary>Tag names in one order on every database: SQL Server ignores letter case when sorting text and PostgreSQL does not, so the sort is done here.</summary>
    internal static List<T> ByName<T>(IEnumerable<T> items, Func<T, string> name, Func<T, Guid> id) =>
        items.OrderBy(name, StringComparer.OrdinalIgnoreCase).ThenBy(name, StringComparer.Ordinal).ThenBy(id).ToList();

    public Task<TagDto?> GetAsync(Guid id, CancellationToken ct = default) =>
        Project(db.Set<Tag>().AsNoTracking().Where(t => t.Id == id)).FirstOrDefaultAsync(ct);

    public Task<TagDto?> GetBySlugAsync(string slug, CancellationToken ct = default)
    {
        var s = slug.Trim().ToLowerInvariant();
        return Project(db.Set<Tag>().AsNoTracking().Where(t => t.Slug == s)).FirstOrDefaultAsync(ct);
    }

    public async Task<TagDto> CreateAsync(TagInput input, CancellationToken ct = default)
    {
        ModelValidator.ValidateOrThrow(input);
        var slug = SlugFor(input.Slug, input.Name);
        await ValidateAsync(input, slug, null, ct);
        var entity = new Tag { Name = input.Name.Trim(), NormalizedName = Normalize(input.Name), Slug = slug };
        db.Add(entity);
        await db.SaveChangesAsync(ct);
        return (await GetAsync(entity.Id, ct))!;
    }

    public async Task<TagDto?> UpdateAsync(Guid id, TagInput input, CancellationToken ct = default)
    {
        var entity = await db.Set<Tag>().FirstOrDefaultAsync(t => t.Id == id, ct);
        if (entity is null) return null;
        ModelValidator.ValidateOrThrow(input);
        var slug = SlugFor(input.Slug, input.Name);
        await ValidateAsync(input, slug, id, ct);
        entity.Name = input.Name.Trim();
        entity.NormalizedName = Normalize(input.Name);
        entity.Slug = slug;
        await db.SaveChangesAsync(ct);
        return await GetAsync(id, ct);
    }

    private async Task ValidateAsync(TagInput input, string slug, Guid? existingId, CancellationToken ct)
    {
        var normalized = Normalize(input.Name);
        if (await db.Set<Tag>().AnyAsync(t => t.NormalizedName == normalized && t.Id != existingId, ct))
            throw new PiranhaValidationException(nameof(TagInput.Name), "A tag with this name already exists.");
        if (await db.Set<Tag>().AnyAsync(t => t.Slug == slug && t.Id != existingId, ct))
            throw new PiranhaValidationException(nameof(TagInput.Slug), "A tag with this slug already exists.");
    }

    public async Task<bool> DeleteAsync(Guid id, CancellationToken ct = default)
    {
        if (!await db.Set<Tag>().AnyAsync(t => t.Id == id, ct)) return false;
        await db.Set<ProductTag>().Where(l => l.TagId == id).ExecuteDeleteAsync(ct);
        return await db.DeleteTrackedAsync<Tag>(t => t.Id == id, ct) > 0;
    }

    public async Task<ProductTagsDto?> GetProductAsync(Guid productId, CancellationToken ct = default)
    {
        if (!await db.Set<Product>().AnyAsync(p => p.Id == productId, ct)) return null;
        var parentId = await db.Set<ProductVariant>().AsNoTracking().Where(v => v.ProductId == productId).Select(v => (Guid?)v.ParentProductId).FirstOrDefaultAsync(ct);
        var holder = parentId ?? productId;
        var tags = await db.Set<ProductTag>().AsNoTracking().Where(l => l.ProductId == holder)
            .Join(db.Set<Tag>(), l => l.TagId, t => t.Id, (l, t) => new { t.Id, t.Name, t.Slug }).ToListAsync(ct);
        return new ProductTagsDto(productId, parentId is not null, ByName(tags, t => t.Name, t => t.Id).Select(t => new TagRefDto(t.Id, t.Name, t.Slug)).ToList());
    }

    public async Task<ProductTagsDto?> SetProductAsync(Guid productId, IReadOnlyCollection<string> names, CancellationToken ct = default)
    {
        if (!await db.Set<Product>().AnyAsync(p => p.Id == productId, ct)) return null;
        if (await db.Set<ProductVariant>().AnyAsync(v => v.ProductId == productId, ct))
            throw new PiranhaValidationException(nameof(ProductTagsInput.Tags), "A variant shows its parent's tags. Set them on the parent product.");

        // The same name in another letter case is the same tag.
        var wanted = new List<(string Name, string Normalized)>();
        foreach (var raw in names ?? [])
        {
            var name = raw?.Trim() ?? "";
            if (name.Length == 0) continue;
            if (name.Length > 50) throw new PiranhaValidationException(nameof(ProductTagsInput.Tags), $"A tag can be up to 50 characters: \"{name[..20]}...\" is longer.");
            if (!wanted.Any(w => w.Normalized == Normalize(name))) wanted.Add((name, Normalize(name)));
        }
        if (wanted.Count > MaxPerProduct) throw new PiranhaValidationException(nameof(ProductTagsInput.Tags), $"A product can have up to {MaxPerProduct} tags.");

        var normalized = wanted.Select(w => w.Normalized).ToList();
        var existing = await db.Set<Tag>().Where(t => normalized.Contains(t.NormalizedName)).ToListAsync(ct);
        var ids = new List<Guid>();
        foreach (var (name, norm) in wanted)
        {
            var tag = existing.FirstOrDefault(t => t.NormalizedName == norm);
            if (tag is null)
            {
                var slug = SlugFor(null, name);
                if (await db.Set<Tag>().AnyAsync(t => t.Slug == slug, ct) || existing.Any(t => t.Slug == slug) || db.ChangeTracker.Entries<Tag>().Any(e => e.State == EntityState.Added && e.Entity.Slug == slug))
                    throw new PiranhaValidationException(nameof(ProductTagsInput.Tags), $"\"{name}\" would have the same address (\"{slug}\") as another tag. Use that tag's name, or make this one in Catalog > Tags with a slug of its own.");
                tag = new Tag { Name = name, NormalizedName = norm, Slug = slug };
                db.Add(tag);
            }
            ids.Add(tag.Id);
        }

        var current = await db.Set<ProductTag>().Where(l => l.ProductId == productId).ToListAsync(ct);
        db.RemoveRange(current.Where(l => !ids.Contains(l.TagId)));
        var have = current.Select(l => l.TagId).ToHashSet();
        foreach (var id in ids.Where(id => !have.Contains(id))) db.Add(new ProductTag { ProductId = productId, TagId = id });
        try { await db.SaveChangesAsync(ct); }
        catch (DbUpdateException)
        {
            db.ChangeTracker.Clear(); // the same new tag made at the same moment elsewhere: the unique index decided
            throw new PiranhaConflictException("Tags changed at the same moment. Try again.");
        }
        return await GetProductAsync(productId, ct);
    }

    public async Task<IReadOnlyDictionary<Guid, IReadOnlyList<string>>> NamesForProductsAsync(IReadOnlyCollection<Guid> productIds, CancellationToken ct = default)
    {
        var result = new Dictionary<Guid, IReadOnlyList<string>>();
        if (productIds.Count == 0) return result;
        var parents = await db.Set<ProductVariant>().AsNoTracking().Where(v => productIds.Contains(v.ProductId))
            .Select(v => new { v.ProductId, v.ParentProductId }).ToDictionaryAsync(v => v.ProductId, v => v.ParentProductId, ct);
        var holders = productIds.Select(i => parents.GetValueOrDefault(i, i)).Distinct().ToList();
        var rows = await db.Set<ProductTag>().AsNoTracking().Where(l => holders.Contains(l.ProductId))
            .Join(db.Set<Tag>(), l => l.TagId, t => t.Id, (l, t) => new { l.ProductId, t.Name }).ToListAsync(ct);
        var byHolder = rows.GroupBy(r => r.ProductId).ToDictionary(g => g.Key, g => (IReadOnlyList<string>)g.Select(r => r.Name).OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ThenBy(n => n, StringComparer.Ordinal).ToList());
        foreach (var id in productIds)
            if (byHolder.TryGetValue(parents.GetValueOrDefault(id, id), out var names)) result[id] = names;
        return result;
    }
}

internal sealed class TagApiEndpoints : IApiEndpointContributor
{
    public void MapEndpoints(IEndpointRouteBuilder api)
    {
        var tags = api.MapGroup("/tags").WithTags("Tags");

        tags.MapGet("/", async (ITagService svc, CancellationToken ct) => Results.Ok(await svc.ListAsync(ct)));

        tags.MapGet("/{id:guid}", async (Guid id, ITagService svc, CancellationToken ct) =>
            await svc.GetAsync(id, ct) is { } t ? Results.Ok(t) : Results.NotFound());

        tags.MapGet("/by-slug/{slug}", async (string slug, ITagService svc, CancellationToken ct) =>
            await svc.GetBySlugAsync(slug, ct) is { } t ? Results.Ok(t) : Results.NotFound());

        tags.MapPost("/", async (TagInput input, ITagService svc, CancellationToken ct) =>
            {
                var created = await svc.CreateAsync(input, ct);
                return Results.Created($"/api/v1/tags/{created.Id}", created);
            })
            .RequireAuthorization(ApiPolicies.Write);

        tags.MapPut("/{id:guid}", async (Guid id, TagInput input, ITagService svc, CancellationToken ct) =>
                await svc.UpdateAsync(id, input, ct) is { } t ? Results.Ok(t) : Results.NotFound())
            .RequireAuthorization(ApiPolicies.Write);

        tags.MapDelete("/{id:guid}", async (Guid id, ITagService svc, CancellationToken ct) =>
                await svc.DeleteAsync(id, ct) ? Results.NoContent() : Results.NotFound())
            .RequireAuthorization(ApiPolicies.Write);

        var product = api.MapGroup("/products/{productId:guid}/tags").WithTags("Product tags");

        product.MapGet("/", async (Guid productId, ITagService svc, CancellationToken ct) =>
            await svc.GetProductAsync(productId, ct) is { } r ? Results.Ok(r) : Results.NotFound());

        product.MapPut("/", async (Guid productId, ProductTagsInput input, ITagService svc, CancellationToken ct) =>
                await svc.SetProductAsync(productId, input.Tags, ct) is { } r ? Results.Ok(r) : Results.NotFound())
            .RequireAuthorization(ApiPolicies.Write);
    }
}
