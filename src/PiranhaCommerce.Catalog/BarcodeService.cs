using Microsoft.EntityFrameworkCore;
using PiranhaCommerce.Core.Validation;
using PiranhaCommerce.Data;
using PiranhaCommerce.Core.Identity;

namespace PiranhaCommerce.Catalog;

public interface IBarcodeService
{
    /// <summary>Null when the product does not exist.</summary>
    Task<ProductBarcodesDto?> GetAsync(Guid productId, CancellationToken ct = default);

    /// <summary>
    /// Replaces the product's barcodes. Null when the product does not exist. Invalid codes are a validation error;
    /// a code already used by another product (in any equivalent GTIN form) is a <see cref="PiranhaCommerceConflictException"/>.
    /// </summary>
    [RequiresPermission(Permissions.CatalogEdit)]
    Task<ProductBarcodesDto?> SetAsync(Guid productId, IReadOnlyCollection<string> codes, CancellationToken ct = default);

    /// <summary>Finds the product a scanned code belongs to (matches equivalent GTIN forms). Null when unknown.</summary>
    Task<ProductDto?> FindProductAsync(string code, CancellationToken ct = default);
}

internal sealed class BarcodeService(PiranhaCommerceDbContext db) : IBarcodeService
{
    public async Task<ProductBarcodesDto?> GetAsync(Guid productId, CancellationToken ct = default)
    {
        if (!await db.Set<Product>().AnyAsync(p => p.Id == productId, ct)) return null;
        return await LoadAsync(productId, ct);
    }

    public async Task<ProductBarcodesDto?> SetAsync(Guid productId, IReadOnlyCollection<string> codes, CancellationToken ct = default)
    {
        if (!await db.Set<Product>().AnyAsync(p => p.Id == productId, ct)) return null;

        // Normalise first (validation), then collapse codes that are the same barcode in different forms.
        var wanted = new Dictionary<string, string>();
        foreach (var raw in codes)
            wanted.TryAdd(BarcodeNormalizer.Normalize(raw, "Codes"), raw.Trim());

        var normalized = wanted.Keys.ToList();
        var taken = await db.Set<ProductBarcode>().AsNoTracking()
            .Where(b => normalized.Contains(b.NormalizedCode) && b.ProductId != productId)
            .Select(b => b.Code).ToListAsync(ct);
        if (taken.Count > 0)
            throw new PiranhaCommerceConflictException("Already used by another product: " + string.Join(", ", taken));

        var existing = await db.Set<ProductBarcode>().Where(b => b.ProductId == productId).ToListAsync(ct);
        db.RemoveRange(existing.Where(b => !wanted.ContainsKey(b.NormalizedCode)));
        foreach (var (norm, display) in wanted.Where(w => existing.All(b => b.NormalizedCode != w.Key)))
            db.Add(new ProductBarcode { ProductId = productId, Code = display, NormalizedCode = norm });

        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException)
        {
            // Lost a race with another writer claiming the same code (the unique index is the real guarantee).
            db.ChangeTracker.Clear();
            throw new PiranhaCommerceConflictException("One of those barcodes was just assigned to another product.");
        }
        return await LoadAsync(productId, ct);
    }

    public async Task<ProductDto?> FindProductAsync(string code, CancellationToken ct = default)
    {
        var normalized = BarcodeNormalizer.Normalize(code, "barcode");
        return await db.Set<ProductBarcode>().AsNoTracking()
            .Where(b => b.NormalizedCode == normalized)
            .Join(db.Set<Product>(), b => b.ProductId, p => p.Id, (_, p) => p)
            .Select(p => new ProductDto(p.Id, p.Name, p.Sku, p.Description, p.Price, p.CreatedUtc, p.UpdatedUtc, p.Slug, p.MetaTitle, p.MetaDescription))
            .FirstOrDefaultAsync(ct);
    }

    private async Task<ProductBarcodesDto> LoadAsync(Guid productId, CancellationToken ct)
    {
        var rows = await db.Set<ProductBarcode>().AsNoTracking().Where(b => b.ProductId == productId)
            .OrderBy(b => b.NormalizedCode).Select(b => new ProductBarcodeDto(b.Id, b.Code, b.NormalizedCode)).ToListAsync(ct);
        return new ProductBarcodesDto(productId, rows);
    }
}
