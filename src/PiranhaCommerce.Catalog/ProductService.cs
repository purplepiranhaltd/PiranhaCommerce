using Microsoft.EntityFrameworkCore;
using PiranhaCommerce.Core.Paging;
using PiranhaCommerce.Core.Validation;
using PiranhaCommerce.Data;

namespace PiranhaCommerce.Catalog;

/// <summary>Single place for product rules, shared by the API and the admin UI.</summary>
public interface IProductService
{
    Task<PagedResult<ProductDto>> ListAsync(int? page, int? pageSize, string? search, CancellationToken ct = default);
    Task<ProductDto?> GetAsync(Guid id, CancellationToken ct = default);
    Task<ProductDto> CreateAsync(ProductInput input, CancellationToken ct = default);
    Task<ProductDto?> UpdateAsync(Guid id, ProductInput input, CancellationToken ct = default);
    Task<bool> DeleteAsync(Guid id, CancellationToken ct = default);
}

internal sealed class ProductService(PiranhaDbContext db) : IProductService
{
    public async Task<PagedResult<ProductDto>> ListAsync(int? page, int? pageSize, string? search, CancellationToken ct = default)
    {
        var (p, size) = Paging.Normalize(page, pageSize);
        var query = db.Set<Product>().AsNoTracking();
        if (!string.IsNullOrWhiteSpace(search))
        {
            // ToLower on both sides: case-insensitive on every provider (SQL Server's default collation already is; PostgreSQL's is not).
            var s = search.Trim().ToLower();
            query = query.Where(x => x.Name.ToLower().Contains(s) || (x.Sku != null && x.Sku.ToLower().Contains(s)));
        }

        var total = await query.CountAsync(ct);
        var items = await query.OrderBy(x => x.Name).ThenBy(x => x.Id) // stable order for paging
            .Skip((p - 1) * size).Take(size)
            .Select(x => new ProductDto(x.Id, x.Name, x.Sku, x.Description, x.Price, x.CreatedUtc, x.UpdatedUtc))
            .ToListAsync(ct);
        return new PagedResult<ProductDto>(items, p, size, total);
    }

    public async Task<ProductDto?> GetAsync(Guid id, CancellationToken ct = default)
        => await db.Set<Product>().AsNoTracking().Where(x => x.Id == id)
            .Select(x => new ProductDto(x.Id, x.Name, x.Sku, x.Description, x.Price, x.CreatedUtc, x.UpdatedUtc))
            .FirstOrDefaultAsync(ct);

    public async Task<ProductDto> CreateAsync(ProductInput input, CancellationToken ct = default)
    {
        await ValidateAsync(input, null, ct);
        var entity = new Product { Name = input.Name.Trim() };
        Apply(entity, input);
        db.Add(entity);
        await db.SaveChangesAsync(ct);
        return ToDto(entity);
    }

    public async Task<ProductDto?> UpdateAsync(Guid id, ProductInput input, CancellationToken ct = default)
    {
        var entity = await db.Set<Product>().FirstOrDefaultAsync(x => x.Id == id, ct); // load, then modify
        if (entity is null) return null;
        await ValidateAsync(input, id, ct);
        Apply(entity, input);
        await db.SaveChangesAsync(ct);
        return ToDto(entity);
    }

    public async Task<bool> DeleteAsync(Guid id, CancellationToken ct = default)
    {
        // Stock movements are an audit ledger; a product with history cannot be deleted (barcodes, categories and the level cascade).
        if (await db.Set<StockMovement>().AnyAsync(m => m.ProductId == id, ct))
            throw new PiranhaConflictException("This product has stock history and cannot be deleted.");
        try
        {
            return await db.Set<Product>().Where(x => x.Id == id).ExecuteDeleteAsync(ct) > 0;
        }
        catch (DbUpdateException)
        {
            throw new PiranhaConflictException("This product is still in use and cannot be deleted.");
        }
    }

    private async Task ValidateAsync(ProductInput input, Guid? existingId, CancellationToken ct)
    {
        ModelValidator.ValidateOrThrow(input);
        if (!string.IsNullOrWhiteSpace(input.Sku))
        {
            var sku = input.Sku.Trim().ToLower();   // SKUs are case-insensitive on every provider
            if (await db.Set<Product>().AnyAsync(x => x.Sku != null && x.Sku.ToLower() == sku && x.Id != existingId, ct))
                throw new PiranhaValidationException(nameof(ProductInput.Sku), "A product with this SKU already exists.");
        }
    }

    private static void Apply(Product e, ProductInput i)
    {
        e.Name = i.Name.Trim();
        e.Sku = string.IsNullOrWhiteSpace(i.Sku) ? null : i.Sku.Trim();
        e.Description = i.Description;
        e.Price = i.Price;
    }

    private static ProductDto ToDto(Product x) =>
        new(x.Id, x.Name, x.Sku, x.Description, x.Price, x.CreatedUtc, x.UpdatedUtc);
}
