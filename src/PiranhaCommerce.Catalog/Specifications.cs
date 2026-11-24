using System.ComponentModel.DataAnnotations;
using System.Text;
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

/// <summary>A fact about products that has a fixed list of answers, such as Finish (Matte, Satin, Glossy) or Skin type. Shown on the product page, and optionally offered to shoppers as a filter.</summary>
[Audited]
public class SpecAttribute : AuditableEntity
{
    public required string Name { get; set; }

    /// <summary>The name in upper case, trimmed: what makes names unique whatever their letter case.</summary>
    public required string NormalizedName { get; set; }

    /// <summary>What a shop front uses to name this attribute in an address. Always set and unique.</summary>
    public required string Slug { get; set; }

    public int DisplayOrder { get; set; }

    /// <summary>Whether shoppers can filter a listing by this attribute.</summary>
    public bool Filterable { get; set; } = true;
}

/// <summary>One of the answers an attribute allows (the attribute "Finish" has "Matte").</summary>
[Audited]
public class SpecValue : AuditableEntity
{
    public Guid AttributeId { get; set; }
    public required string Value { get; set; }
    public required string NormalizedValue { get; set; }

    /// <summary>Unique within the attribute. Always set.</summary>
    public required string Slug { get; set; }

    public int DisplayOrder { get; set; }
}

/// <summary>Says a product has this value. A product can have several values of one attribute (a cream suits "Dry" and "Sensitive" skin). Held on the main product, never on a variant.</summary>
public class ProductSpecValue
{
    public Guid ProductId { get; set; }
    public Guid ValueId { get; set; }
}

public sealed class SpecAttributeInput
{
    [Required, StringLength(200, MinimumLength = 1)]
    public string Name { get; set; } = "";

    /// <summary>Lower case letters, digits and single hyphens, unique among attributes. Unlike a brand's, an attribute's slug is never empty: leave it out and it is made from the name (a name with no letters or digits needs one given).</summary>
    [StringLength(200)]
    public string? Slug { get; set; }

    [Range(0, 1_000_000)]
    public int DisplayOrder { get; set; }

    public bool Filterable { get; set; } = true;
}

public sealed class SpecValueInput
{
    [Required, StringLength(200, MinimumLength = 1)]
    public string Value { get; set; } = "";

    /// <summary>Unique within the attribute. Left out, it is made from the value.</summary>
    [StringLength(200)]
    public string? Slug { get; set; }

    [Range(0, 1_000_000)]
    public int DisplayOrder { get; set; }
}

/// <param name="ProductCount">Products (not variants) that have this value.</param>
public sealed record SpecValueDto(Guid Id, Guid AttributeId, string Value, string Slug, int DisplayOrder, int ProductCount);

/// <param name="ProductCount">Products (not variants) that have any value of this attribute.</param>
public sealed record SpecAttributeDto(
    Guid Id, string Name, string Slug, int DisplayOrder, bool Filterable, IReadOnlyList<SpecValueDto> Values, int ProductCount, DateTimeOffset CreatedUtc, DateTimeOffset? UpdatedUtc);

/// <summary>Replaces the whole set of values a product has.</summary>
public sealed class ProductSpecsInput
{
    public List<Guid> ValueIds { get; set; } = [];
}

/// <param name="Inherited">True for a variant: it shows its parent's values, which can only be changed on the parent.</param>
/// <param name="ValueIds">The values the product has.</param>
public sealed record ProductSpecsDto(Guid ProductId, bool Inherited, IReadOnlyList<Guid> ValueIds, IReadOnlyList<ProductSpecGroupDto> Groups);

public sealed record ProductSpecGroupDto(Guid AttributeId, string Name, string Slug, IReadOnlyList<SpecValueRefDto> Values);

public sealed record SpecValueRefDto(Guid Id, string Value, string Slug);

public interface ISpecificationService
{
    /// <summary>Every attribute with its values and counts; by display order, then name (values likewise).</summary>
    Task<IReadOnlyList<SpecAttributeDto>> ListAsync(CancellationToken ct = default);
    Task<SpecAttributeDto?> GetAsync(Guid id, CancellationToken ct = default);
    Task<SpecAttributeDto?> GetBySlugAsync(string slug, CancellationToken ct = default);
    Task<SpecAttributeDto> CreateAsync(SpecAttributeInput input, CancellationToken ct = default);
    Task<SpecAttributeDto?> UpdateAsync(Guid id, SpecAttributeInput input, CancellationToken ct = default);

    /// <summary>False when not found. Throws <see cref="PiranhaCommerceConflictException"/> while any product has one of its values; otherwise its values go with it.</summary>
    Task<bool> DeleteAsync(Guid id, CancellationToken ct = default);

    /// <summary>Null when the attribute does not exist.</summary>
    Task<SpecValueDto?> AddValueAsync(Guid attributeId, SpecValueInput input, CancellationToken ct = default);
    Task<SpecValueDto?> UpdateValueAsync(Guid attributeId, Guid valueId, SpecValueInput input, CancellationToken ct = default);

    /// <summary>False when not found. Throws <see cref="PiranhaCommerceConflictException"/> while any product has the value.</summary>
    Task<bool> DeleteValueAsync(Guid attributeId, Guid valueId, CancellationToken ct = default);

    /// <summary>Null when the product does not exist. For a variant, its parent's.</summary>
    Task<ProductSpecsDto?> GetProductAsync(Guid productId, CancellationToken ct = default);

    /// <summary>Null when the product does not exist. A validation error for an unknown value, or for a variant.</summary>
    Task<ProductSpecsDto?> SetProductAsync(Guid productId, IReadOnlyCollection<Guid> valueIds, CancellationToken ct = default);

    /// <summary>Each product's values as "Attribute: Value" labels, attributes in display order (a variant shows its parent's). Products with none are left out. Used by the catalogue export.</summary>
    Task<IReadOnlyDictionary<Guid, IReadOnlyList<string>>> LabelsForProductsAsync(IReadOnlyCollection<Guid> productIds, CancellationToken ct = default);
}

internal sealed class SpecificationService(PiranhaCommerceDbContext db) : ISpecificationService
{
    public static string Normalize(string text) => text.Trim().ToUpperInvariant();

    /// <summary>"Skin type" becomes "skin-type". Empty when nothing usable is left (a name of only symbols).</summary>
    public static string Slugify(string text)
    {
        var sb = new StringBuilder();
        foreach (var c in text.Trim().ToLowerInvariant())
        {
            if (c is >= 'a' and <= 'z' or >= '0' and <= '9') sb.Append(c);
            else if (c == '&') sb.Append(" and ");
            else if (sb.Length > 0 && sb[^1] != '-') sb.Append('-');
        }
        var s = System.Text.RegularExpressions.Regex.Replace(sb.ToString(), "-+", "-").Trim('-');
        return s.Length > 200 ? s[..200].Trim('-') : s;
    }

    private static string SlugFor(string? given, string source, string field)
    {
        var slug = Seo.NormalizeSlug(given, field) ?? Slugify(source);
        if (slug.Length == 0) throw new PiranhaCommerceValidationException(field, "Give a slug: the name has no letters or digits to make one from.");
        return slug;
    }

    public async Task<IReadOnlyList<SpecAttributeDto>> ListAsync(CancellationToken ct = default)
    {
        var attributes = await db.Set<SpecAttribute>().AsNoTracking().OrderBy(a => a.DisplayOrder).ThenBy(a => a.Name).ThenBy(a => a.Id).ToListAsync(ct);
        return await ProjectAsync(attributes, ct);
    }

    public async Task<SpecAttributeDto?> GetAsync(Guid id, CancellationToken ct = default) =>
        (await ProjectAsync(await db.Set<SpecAttribute>().AsNoTracking().Where(a => a.Id == id).ToListAsync(ct), ct)).FirstOrDefault();

    public async Task<SpecAttributeDto?> GetBySlugAsync(string slug, CancellationToken ct = default)
    {
        var s = slug.Trim().ToLowerInvariant();
        return (await ProjectAsync(await db.Set<SpecAttribute>().AsNoTracking().Where(a => a.Slug == s).ToListAsync(ct), ct)).FirstOrDefault();
    }

    private async Task<IReadOnlyList<SpecAttributeDto>> ProjectAsync(List<SpecAttribute> attributes, CancellationToken ct)
    {
        if (attributes.Count == 0) return [];
        var ids = attributes.Select(a => a.Id).ToList();
        var values = await db.Set<SpecValue>().AsNoTracking().Where(v => ids.Contains(v.AttributeId)).OrderBy(v => v.DisplayOrder).ThenBy(v => v.Value).ThenBy(v => v.Id).ToListAsync(ct);
        var valueCounts = await db.Set<ProductSpecValue>().AsNoTracking().Where(l => values.Select(v => v.Id).Contains(l.ValueId))
            .GroupBy(l => l.ValueId).Select(g => new { Id = g.Key, N = g.Count() }).ToDictionaryAsync(x => x.Id, x => x.N, ct);
        var attributeCounts = await db.Set<ProductSpecValue>().AsNoTracking()
            .Join(db.Set<SpecValue>().Where(v => ids.Contains(v.AttributeId)), l => l.ValueId, v => v.Id, (l, v) => new { v.AttributeId, l.ProductId })
            .GroupBy(x => x.AttributeId).Select(g => new { Id = g.Key, N = g.Select(x => x.ProductId).Distinct().Count() }).ToDictionaryAsync(x => x.Id, x => x.N, ct);
        return attributes.Select(a => new SpecAttributeDto(
            a.Id, a.Name, a.Slug, a.DisplayOrder, a.Filterable,
            values.Where(v => v.AttributeId == a.Id).Select(v => new SpecValueDto(v.Id, v.AttributeId, v.Value, v.Slug, v.DisplayOrder, valueCounts.GetValueOrDefault(v.Id))).ToList(),
            attributeCounts.GetValueOrDefault(a.Id), a.CreatedUtc, a.UpdatedUtc)).ToList();
    }

    public async Task<SpecAttributeDto> CreateAsync(SpecAttributeInput input, CancellationToken ct = default)
    {
        ModelValidator.ValidateOrThrow(input);
        var slug = SlugFor(input.Slug, input.Name, nameof(SpecAttributeInput.Slug));
        await ValidateAsync(input, slug, null, ct);
        var entity = new SpecAttribute { Name = input.Name.Trim(), NormalizedName = Normalize(input.Name), Slug = slug };
        entity.DisplayOrder = input.DisplayOrder;
        entity.Filterable = input.Filterable;
        db.Add(entity);
        await db.SaveChangesAsync(ct);
        return (await GetAsync(entity.Id, ct))!;
    }

    public async Task<SpecAttributeDto?> UpdateAsync(Guid id, SpecAttributeInput input, CancellationToken ct = default)
    {
        var entity = await db.Set<SpecAttribute>().FirstOrDefaultAsync(a => a.Id == id, ct);
        if (entity is null) return null;
        ModelValidator.ValidateOrThrow(input);
        var slug = SlugFor(input.Slug, input.Name, nameof(SpecAttributeInput.Slug));
        await ValidateAsync(input, slug, id, ct);
        entity.Name = input.Name.Trim();
        entity.NormalizedName = Normalize(input.Name);
        entity.Slug = slug;
        entity.DisplayOrder = input.DisplayOrder;
        entity.Filterable = input.Filterable;
        await db.SaveChangesAsync(ct);
        return await GetAsync(id, ct);
    }

    private async Task ValidateAsync(SpecAttributeInput input, string slug, Guid? existingId, CancellationToken ct)
    {
        var normalized = Normalize(input.Name);
        if (await db.Set<SpecAttribute>().AnyAsync(a => a.NormalizedName == normalized && a.Id != existingId, ct))
            throw new PiranhaCommerceValidationException(nameof(SpecAttributeInput.Name), "An attribute with this name already exists.");
        if (await db.Set<SpecAttribute>().AnyAsync(a => a.Slug == slug && a.Id != existingId, ct))
            throw new PiranhaCommerceValidationException(nameof(SpecAttributeInput.Slug), "An attribute with this slug already exists.");
    }

    public async Task<bool> DeleteAsync(Guid id, CancellationToken ct = default)
    {
        if (!await db.Set<SpecAttribute>().AnyAsync(a => a.Id == id, ct)) return false;
        if (await db.Set<ProductSpecValue>().AnyAsync(l => db.Set<SpecValue>().Any(v => v.Id == l.ValueId && v.AttributeId == id), ct))
            throw new PiranhaCommerceConflictException("Products still have values of this attribute. Remove them from the products first.");
        await db.DeleteTrackedAsync<SpecValue>(v => v.AttributeId == id, ct);
        return await db.DeleteTrackedAsync<SpecAttribute>(a => a.Id == id, ct) > 0;
    }

    public async Task<SpecValueDto?> AddValueAsync(Guid attributeId, SpecValueInput input, CancellationToken ct = default)
    {
        if (!await db.Set<SpecAttribute>().AnyAsync(a => a.Id == attributeId, ct)) return null;
        ModelValidator.ValidateOrThrow(input);
        var slug = SlugFor(input.Slug, input.Value, nameof(SpecValueInput.Slug));
        await ValidateValueAsync(attributeId, input, slug, null, ct);
        var entity = new SpecValue { AttributeId = attributeId, Value = input.Value.Trim(), NormalizedValue = Normalize(input.Value), Slug = slug, DisplayOrder = input.DisplayOrder };
        db.Add(entity);
        await db.SaveChangesAsync(ct);
        return new SpecValueDto(entity.Id, attributeId, entity.Value, entity.Slug, entity.DisplayOrder, 0);
    }

    public async Task<SpecValueDto?> UpdateValueAsync(Guid attributeId, Guid valueId, SpecValueInput input, CancellationToken ct = default)
    {
        var entity = await db.Set<SpecValue>().FirstOrDefaultAsync(v => v.Id == valueId && v.AttributeId == attributeId, ct);
        if (entity is null) return null;
        ModelValidator.ValidateOrThrow(input);
        var slug = SlugFor(input.Slug, input.Value, nameof(SpecValueInput.Slug));
        await ValidateValueAsync(attributeId, input, slug, valueId, ct);
        entity.Value = input.Value.Trim();
        entity.NormalizedValue = Normalize(input.Value);
        entity.Slug = slug;
        entity.DisplayOrder = input.DisplayOrder;
        await db.SaveChangesAsync(ct);
        var count = await db.Set<ProductSpecValue>().CountAsync(l => l.ValueId == valueId, ct);
        return new SpecValueDto(entity.Id, attributeId, entity.Value, entity.Slug, entity.DisplayOrder, count);
    }

    private async Task ValidateValueAsync(Guid attributeId, SpecValueInput input, string slug, Guid? existingId, CancellationToken ct)
    {
        var normalized = Normalize(input.Value);
        if (await db.Set<SpecValue>().AnyAsync(v => v.AttributeId == attributeId && v.NormalizedValue == normalized && v.Id != existingId, ct))
            throw new PiranhaCommerceValidationException(nameof(SpecValueInput.Value), "This attribute already has that value.");
        if (await db.Set<SpecValue>().AnyAsync(v => v.AttributeId == attributeId && v.Slug == slug && v.Id != existingId, ct))
            throw new PiranhaCommerceValidationException(nameof(SpecValueInput.Slug), "This attribute already has a value with that slug.");
    }

    public async Task<bool> DeleteValueAsync(Guid attributeId, Guid valueId, CancellationToken ct = default)
    {
        if (!await db.Set<SpecValue>().AnyAsync(v => v.Id == valueId && v.AttributeId == attributeId, ct)) return false;
        if (await db.Set<ProductSpecValue>().AnyAsync(l => l.ValueId == valueId, ct))
            throw new PiranhaCommerceConflictException("Products still have this value. Remove it from the products first.");
        return await db.DeleteTrackedAsync<SpecValue>(v => v.Id == valueId, ct) > 0;
    }

    public async Task<ProductSpecsDto?> GetProductAsync(Guid productId, CancellationToken ct = default)
    {
        if (!await db.Set<Product>().AnyAsync(p => p.Id == productId, ct)) return null;
        var parentId = await db.Set<ProductVariant>().AsNoTracking().Where(v => v.ProductId == productId).Select(v => (Guid?)v.ParentProductId).FirstOrDefaultAsync(ct);
        var holder = parentId ?? productId;
        var rows = await db.Set<ProductSpecValue>().AsNoTracking().Where(l => l.ProductId == holder)
            .Join(db.Set<SpecValue>(), l => l.ValueId, v => v.Id, (l, v) => v)
            .Join(db.Set<SpecAttribute>(), v => v.AttributeId, a => a.Id, (v, a) => new { a.Id, a.Name, a.Slug, AttributeOrder = a.DisplayOrder, ValueId = v.Id, v.Value, ValueSlug = v.Slug, ValueOrder = v.DisplayOrder })
            .ToListAsync(ct);
        var groups = rows.GroupBy(r => new { r.Id, r.Name, r.Slug, r.AttributeOrder }).OrderBy(g => g.Key.AttributeOrder).ThenBy(g => g.Key.Name)
            .Select(g => new ProductSpecGroupDto(g.Key.Id, g.Key.Name, g.Key.Slug,
                g.OrderBy(r => r.ValueOrder).ThenBy(r => r.Value).Select(r => new SpecValueRefDto(r.ValueId, r.Value, r.ValueSlug)).ToList())).ToList();
        return new ProductSpecsDto(productId, parentId is not null, groups.SelectMany(g => g.Values.Select(v => v.Id)).ToList(), groups);
    }

    public async Task<IReadOnlyDictionary<Guid, IReadOnlyList<string>>> LabelsForProductsAsync(IReadOnlyCollection<Guid> productIds, CancellationToken ct = default)
    {
        var result = new Dictionary<Guid, IReadOnlyList<string>>();
        if (productIds.Count == 0) return result;
        var parents = await db.Set<ProductVariant>().AsNoTracking().Where(v => productIds.Contains(v.ProductId))
            .Select(v => new { v.ProductId, v.ParentProductId }).ToDictionaryAsync(v => v.ProductId, v => v.ParentProductId, ct);
        var holders = productIds.Select(i => parents.GetValueOrDefault(i, i)).Distinct().ToList();
        var rows = await db.Set<ProductSpecValue>().AsNoTracking().Where(l => holders.Contains(l.ProductId))
            .Join(db.Set<SpecValue>(), l => l.ValueId, v => v.Id, (l, v) => new { l.ProductId, v.AttributeId, v.Value, ValueOrder = v.DisplayOrder })
            .Join(db.Set<SpecAttribute>(), x => x.AttributeId, a => a.Id, (x, a) => new { x.ProductId, a.Name, AttributeOrder = a.DisplayOrder, x.Value, x.ValueOrder })
            .ToListAsync(ct);
        var byHolder = rows.GroupBy(r => r.ProductId).ToDictionary(g => g.Key, g => (IReadOnlyList<string>)g
            .OrderBy(r => r.AttributeOrder).ThenBy(r => r.Name).ThenBy(r => r.ValueOrder).ThenBy(r => r.Value).Select(r => $"{r.Name}: {r.Value}").ToList());
        foreach (var id in productIds)
            if (byHolder.TryGetValue(parents.GetValueOrDefault(id, id), out var labels)) result[id] = labels;
        return result;
    }

    public async Task<ProductSpecsDto?> SetProductAsync(Guid productId, IReadOnlyCollection<Guid> valueIds, CancellationToken ct = default)
    {
        if (!await db.Set<Product>().AnyAsync(p => p.Id == productId, ct)) return null;
        if (await db.Set<ProductVariant>().AnyAsync(v => v.ProductId == productId, ct))
            throw new PiranhaCommerceValidationException(nameof(ProductSpecsInput.ValueIds), "A variant shows its parent's specifications. Set them on the parent product.");
        var wanted = valueIds.Distinct().ToList();
        if (wanted.Count != valueIds.Count) throw new PiranhaCommerceValidationException(nameof(ProductSpecsInput.ValueIds), "A value is listed twice.");
        if (wanted.Count > 0 && await db.Set<SpecValue>().CountAsync(v => wanted.Contains(v.Id), ct) != wanted.Count)
            throw new PiranhaCommerceValidationException(nameof(ProductSpecsInput.ValueIds), "One of those values does not exist.");

        var current = await db.Set<ProductSpecValue>().Where(l => l.ProductId == productId).ToListAsync(ct);
        db.RemoveRange(current.Where(l => !wanted.Contains(l.ValueId)));
        var have = current.Select(l => l.ValueId).ToHashSet();
        foreach (var id in wanted.Where(id => !have.Contains(id))) db.Add(new ProductSpecValue { ProductId = productId, ValueId = id });
        await db.SaveChangesAsync(ct);
        return await GetProductAsync(productId, ct);
    }
}

internal sealed class SpecificationApiEndpoints : IApiEndpointContributor
{
    public void MapEndpoints(IEndpointRouteBuilder api)
    {
        var specs = api.MapGroup("/specifications").WithTags("Specifications");

        specs.MapGet("/", async (ISpecificationService svc, CancellationToken ct) => Results.Ok(await svc.ListAsync(ct)));

        specs.MapGet("/{id:guid}", async (Guid id, ISpecificationService svc, CancellationToken ct) =>
            await svc.GetAsync(id, ct) is { } a ? Results.Ok(a) : Results.NotFound());

        specs.MapGet("/by-slug/{slug}", async (string slug, ISpecificationService svc, CancellationToken ct) =>
            await svc.GetBySlugAsync(slug, ct) is { } a ? Results.Ok(a) : Results.NotFound());

        specs.MapPost("/", async (SpecAttributeInput input, ISpecificationService svc, CancellationToken ct) =>
            {
                var created = await svc.CreateAsync(input, ct);
                return Results.Created($"/api/v1/specifications/{created.Id}", created);
            })
            .RequireAuthorization(ApiPolicies.Write);

        specs.MapPut("/{id:guid}", async (Guid id, SpecAttributeInput input, ISpecificationService svc, CancellationToken ct) =>
                await svc.UpdateAsync(id, input, ct) is { } a ? Results.Ok(a) : Results.NotFound())
            .RequireAuthorization(ApiPolicies.Write);

        specs.MapDelete("/{id:guid}", async (Guid id, ISpecificationService svc, CancellationToken ct) =>
                await svc.DeleteAsync(id, ct) ? Results.NoContent() : Results.NotFound())
            .RequireAuthorization(ApiPolicies.Write);

        specs.MapPost("/{id:guid}/values", async (Guid id, SpecValueInput input, ISpecificationService svc, CancellationToken ct) =>
                await svc.AddValueAsync(id, input, ct) is { } v ? Results.Created($"/api/v1/specifications/{id}/values/{v.Id}", v) : Results.NotFound())
            .RequireAuthorization(ApiPolicies.Write);

        specs.MapPut("/{id:guid}/values/{valueId:guid}", async (Guid id, Guid valueId, SpecValueInput input, ISpecificationService svc, CancellationToken ct) =>
                await svc.UpdateValueAsync(id, valueId, input, ct) is { } v ? Results.Ok(v) : Results.NotFound())
            .RequireAuthorization(ApiPolicies.Write);

        specs.MapDelete("/{id:guid}/values/{valueId:guid}", async (Guid id, Guid valueId, ISpecificationService svc, CancellationToken ct) =>
                await svc.DeleteValueAsync(id, valueId, ct) ? Results.NoContent() : Results.NotFound())
            .RequireAuthorization(ApiPolicies.Write);

        var product = api.MapGroup("/products/{productId:guid}/specifications").WithTags("Product specifications");

        product.MapGet("/", async (Guid productId, ISpecificationService svc, CancellationToken ct) =>
            await svc.GetProductAsync(productId, ct) is { } r ? Results.Ok(r) : Results.NotFound());

        product.MapPut("/", async (Guid productId, ProductSpecsInput input, ISpecificationService svc, CancellationToken ct) =>
                await svc.SetProductAsync(productId, input.ValueIds, ct) is { } r ? Results.Ok(r) : Results.NotFound())
            .RequireAuthorization(ApiPolicies.Write);
    }
}
