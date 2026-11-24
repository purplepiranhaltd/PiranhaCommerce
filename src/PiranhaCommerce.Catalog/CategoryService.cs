using Microsoft.EntityFrameworkCore;
using PiranhaCommerce.Core.Validation;
using PiranhaCommerce.Data;

namespace PiranhaCommerce.Catalog;

public interface ICategoryService
{
    /// <summary>Every category, flat, with <c>ParentId</c>; ordered by display order, name, id.</summary>
    Task<IReadOnlyList<CategoryDto>> ListAsync(CancellationToken ct = default);
    Task<CategoryDto?> GetAsync(Guid id, CancellationToken ct = default);
    Task<CategoryDto?> GetBySlugAsync(string slug, CancellationToken ct = default);
    Task<CategoryDto> CreateAsync(CategoryInput input, CancellationToken ct = default);
    Task<CategoryDto?> UpdateAsync(Guid id, CategoryInput input, CancellationToken ct = default);

    /// <summary>False when not found. Throws <see cref="PiranhaCommerceConflictException"/> while the category still has child categories.</summary>
    Task<bool> DeleteAsync(Guid id, CancellationToken ct = default);

    /// <summary>Null when the product does not exist.</summary>
    Task<ProductCategoriesDto?> GetProductCategoriesAsync(Guid productId, CancellationToken ct = default);

    /// <summary>Replaces the product's categories. Null when the product does not exist; unknown category ids are a validation error.</summary>
    Task<ProductCategoriesDto?> SetProductCategoriesAsync(Guid productId, IReadOnlyCollection<Guid> categoryIds, CancellationToken ct = default);
}

internal sealed class CategoryService(PiranhaCommerceDbContext db) : ICategoryService
{
    private const int MaxDepth = 100; // guards the ancestor walk even if stored data were already corrupt

    public async Task<IReadOnlyList<CategoryDto>> ListAsync(CancellationToken ct = default)
        => await db.Set<Category>().AsNoTracking()
            .OrderBy(c => c.DisplayOrder).ThenBy(c => c.Name).ThenBy(c => c.Id)
            .Select(c => new CategoryDto(c.Id, c.Name, c.Description, c.ParentId, c.DisplayOrder, c.Active, c.CreatedUtc, c.UpdatedUtc, c.Slug, c.MetaTitle, c.MetaDescription))
            .ToListAsync(ct);

    public async Task<CategoryDto?> GetAsync(Guid id, CancellationToken ct = default)
        => await db.Set<Category>().AsNoTracking().Where(c => c.Id == id)
            .Select(c => new CategoryDto(c.Id, c.Name, c.Description, c.ParentId, c.DisplayOrder, c.Active, c.CreatedUtc, c.UpdatedUtc, c.Slug, c.MetaTitle, c.MetaDescription))
            .FirstOrDefaultAsync(ct);

    public async Task<CategoryDto?> GetBySlugAsync(string slug, CancellationToken ct = default)
    {
        var s = slug.Trim().ToLowerInvariant();
        return await db.Set<Category>().AsNoTracking().Where(c => c.Slug == s)
            .Select(c => new CategoryDto(c.Id, c.Name, c.Description, c.ParentId, c.DisplayOrder, c.Active, c.CreatedUtc, c.UpdatedUtc, c.Slug, c.MetaTitle, c.MetaDescription))
            .FirstOrDefaultAsync(ct);
    }

    public async Task<CategoryDto> CreateAsync(CategoryInput input, CancellationToken ct = default)
    {
        ModelValidator.ValidateOrThrow(input);
        await ValidateSlugAsync(input, null, ct);
        await ValidateParentAsync(null, input.ParentId, ct);
        var entity = new Category { Name = input.Name.Trim() };
        Apply(entity, input);
        db.Add(entity);
        await db.SaveChangesAsync(ct);
        return ToDto(entity);
    }

    public async Task<CategoryDto?> UpdateAsync(Guid id, CategoryInput input, CancellationToken ct = default)
    {
        var entity = await db.Set<Category>().FirstOrDefaultAsync(c => c.Id == id, ct);
        if (entity is null) return null;
        ModelValidator.ValidateOrThrow(input);
        await ValidateSlugAsync(input, id, ct);
        await ValidateParentAsync(id, input.ParentId, ct);
        Apply(entity, input);
        await db.SaveChangesAsync(ct);
        return ToDto(entity);
    }

    public async Task<bool> DeleteAsync(Guid id, CancellationToken ct = default)
    {
        if (!await db.Set<Category>().AnyAsync(c => c.Id == id, ct)) return false;
        if (await db.Set<Category>().AnyAsync(c => c.ParentId == id, ct))
            throw new PiranhaCommerceConflictException("This category has sub-categories. Move or delete them first.");
        // Links to products are removed by the database (cascade).
        return await db.DeleteTrackedAsync<Category>(c => c.Id == id, ct) > 0;
    }

    public async Task<ProductCategoriesDto?> GetProductCategoriesAsync(Guid productId, CancellationToken ct = default)
    {
        if (!await db.Set<Product>().AnyAsync(p => p.Id == productId, ct)) return null;
        var ids = await db.Set<ProductCategory>().AsNoTracking().Where(l => l.ProductId == productId)
            .Select(l => l.CategoryId).OrderBy(i => i).ToListAsync(ct);
        return new ProductCategoriesDto(productId, ids);
    }

    public async Task<ProductCategoriesDto?> SetProductCategoriesAsync(Guid productId, IReadOnlyCollection<Guid> categoryIds, CancellationToken ct = default)
    {
        if (!await db.Set<Product>().AnyAsync(p => p.Id == productId, ct)) return null;

        var wanted = categoryIds.Distinct().ToList();
        var known = await db.Set<Category>().Where(c => wanted.Contains(c.Id)).Select(c => c.Id).ToListAsync(ct);
        var unknown = wanted.Except(known).ToList();
        if (unknown.Count > 0)
            throw new PiranhaCommerceValidationException(nameof(ProductCategoriesInput.CategoryIds), "Unknown category ids: " + string.Join(", ", unknown));

        var existing = await db.Set<ProductCategory>().Where(l => l.ProductId == productId).ToListAsync(ct);
        db.RemoveRange(existing.Where(l => !wanted.Contains(l.CategoryId)));
        foreach (var id in wanted.Except(existing.Select(l => l.CategoryId)))
            db.Add(new ProductCategory { ProductId = productId, CategoryId = id });
        await db.SaveChangesAsync(ct);
        return new ProductCategoriesDto(productId, wanted.Order().ToList());
    }

    /// <summary>The parent must exist and must not be the category itself or one of its descendants (no cycles).</summary>
    private async Task ValidateParentAsync(Guid? selfId, Guid? parentId, CancellationToken ct)
    {
        if (parentId is null) return;
        const string field = nameof(CategoryInput.ParentId);

        if (parentId == selfId)
            throw new PiranhaCommerceValidationException(field, "A category cannot be its own parent.");

        var cursor = parentId;
        for (var depth = 0; cursor is not null; depth++)
        {
            if (depth > MaxDepth)
                throw new PiranhaCommerceValidationException(field, "The category tree is too deep.");

            var node = await db.Set<Category>().AsNoTracking().Where(c => c.Id == cursor)
                .Select(c => new { c.Id, c.ParentId }).FirstOrDefaultAsync(ct);
            if (node is null)
                throw new PiranhaCommerceValidationException(field, depth == 0 ? "The parent category does not exist." : "The category tree is inconsistent.");
            if (node.Id == selfId)
                throw new PiranhaCommerceValidationException(field, "That parent is below this category; moving it there would create a cycle.");
            cursor = node.ParentId;
        }
    }

    private async Task ValidateSlugAsync(CategoryInput input, Guid? existingId, CancellationToken ct)
    {
        if (Seo.NormalizeSlug(input.Slug) is { } slug && await db.Set<Category>().AnyAsync(c => c.Slug == slug && c.Id != existingId, ct))
            throw new PiranhaCommerceValidationException(nameof(CategoryInput.Slug), "A category with this slug already exists.");
    }

    private static void Apply(Category e, CategoryInput i)
    {
        e.Slug = Seo.NormalizeSlug(i.Slug);
        e.MetaTitle = string.IsNullOrWhiteSpace(i.MetaTitle) ? null : i.MetaTitle.Trim();
        e.MetaDescription = string.IsNullOrWhiteSpace(i.MetaDescription) ? null : i.MetaDescription.Trim();
        e.Name = i.Name.Trim();
        e.Description = i.Description;
        e.ParentId = i.ParentId;
        e.DisplayOrder = i.DisplayOrder;
        e.Active = i.Active;
    }

    private static CategoryDto ToDto(Category c) =>
        new(c.Id, c.Name, c.Description, c.ParentId, c.DisplayOrder, c.Active, c.CreatedUtc, c.UpdatedUtc, c.Slug, c.MetaTitle, c.MetaDescription);
}
