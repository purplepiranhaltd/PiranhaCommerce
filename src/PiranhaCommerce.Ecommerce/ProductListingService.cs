using Microsoft.EntityFrameworkCore;
using PiranhaCommerce.Catalog;
using PiranhaCommerce.Core.Validation;
using PiranhaCommerce.Data;

namespace PiranhaCommerce.Ecommerce;

public interface IProductListingService
{
    /// <summary>Null when the product does not exist; an unpublished listing when it exists but was never configured.</summary>
    Task<ProductListingDto?> GetAsync(Guid productId, CancellationToken ct = default);

    /// <summary>Creates or updates the listing. Null when the product does not exist.</summary>
    Task<ProductListingDto?> SetAsync(Guid productId, ProductListingInput input, CancellationToken ct = default);
}

internal sealed class ProductListingService(PiranhaDbContext db) : IProductListingService
{
    public async Task<ProductListingDto?> GetAsync(Guid productId, CancellationToken ct = default)
    {
        if (!await db.Set<Product>().AnyAsync(p => p.Id == productId, ct)) return null;
        var listing = await db.Set<ProductListing>().AsNoTracking()
            .Where(l => l.ProductId == productId).Select(l => new { l.Published, l.TaxClassId }).FirstOrDefaultAsync(ct);
        return new ProductListingDto(productId, listing?.Published ?? false, listing?.TaxClassId);
    }

    public async Task<ProductListingDto?> SetAsync(Guid productId, ProductListingInput input, CancellationToken ct = default)
    {
        if (!await db.Set<Product>().AnyAsync(p => p.Id == productId, ct)) return null;

        if (input.TaxClassId is { } classId && !await db.Set<TaxClass>().AnyAsync(c => c.Id == classId, ct))
            throw new PiranhaValidationException(nameof(ProductListingInput.TaxClassId), "That tax class does not exist.");

        var listing = await db.Set<ProductListing>().FirstOrDefaultAsync(l => l.ProductId == productId, ct);
        if (listing is null)
            db.Add(listing = new ProductListing { ProductId = productId });
        listing.Published = input.Published;
        listing.TaxClassId = input.TaxClassId;
        await db.SaveChangesAsync(ct);
        return new ProductListingDto(productId, listing.Published, listing.TaxClassId);
    }
}
