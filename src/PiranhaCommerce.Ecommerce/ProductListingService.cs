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
            .Where(l => l.ProductId == productId).Select(l => new { l.Published, l.TaxClassId, l.WeightGrams, l.MinOrderQuantity, l.MaxOrderQuantity }).FirstOrDefaultAsync(ct);
        return new ProductListingDto(productId, listing?.Published ?? false, listing?.TaxClassId, listing?.WeightGrams, listing?.MinOrderQuantity, listing?.MaxOrderQuantity);
    }

    public async Task<ProductListingDto?> SetAsync(Guid productId, ProductListingInput input, CancellationToken ct = default)
    {
        if (!await db.Set<Product>().AnyAsync(p => p.Id == productId, ct)) return null;
        ModelValidator.ValidateOrThrow(input);

        if (input.TaxClassId is { } classId && !await db.Set<TaxClass>().AnyAsync(c => c.Id == classId, ct))
            throw new PiranhaValidationException(nameof(ProductListingInput.TaxClassId), "That tax class does not exist.");

        if (input.MinOrderQuantity is { } lo && input.MaxOrderQuantity is { } hi && lo > hi)
            throw new PiranhaValidationException(nameof(ProductListingInput.MaxOrderQuantity), "The maximum cannot be less than the minimum.");

        var listing = await db.Set<ProductListing>().FirstOrDefaultAsync(l => l.ProductId == productId, ct);
        if (listing is null)
            db.Add(listing = new ProductListing { ProductId = productId });
        listing.Published = input.Published;
        listing.TaxClassId = input.TaxClassId;
        listing.WeightGrams = input.WeightGrams;
        listing.MinOrderQuantity = input.MinOrderQuantity;
        listing.MaxOrderQuantity = input.MaxOrderQuantity;
        await db.SaveChangesAsync(ct);
        return new ProductListingDto(productId, listing.Published, listing.TaxClassId, listing.WeightGrams, listing.MinOrderQuantity, listing.MaxOrderQuantity);
    }
}
