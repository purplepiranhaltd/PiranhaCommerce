using Microsoft.EntityFrameworkCore;
using PiranhaCommerce.Catalog;

namespace PiranhaCommerce.Ecommerce;

/// <summary>
/// Online-store data for a catalogue <see cref="Product"/> (one row per product that has been configured).
/// A product with no row is not listed online. EPOS would keep its own equivalent table.
/// </summary>
public class ProductListing
{
    public Guid ProductId { get; set; }
    public bool Published { get; set; }

    /// <summary>The product's tax class. Null means the store's default class.</summary>
    public Guid? TaxClassId { get; set; }

    /// <summary>The product's weight in grams, for shipping rates by weight. Null counts as nothing.</summary>
    public int? WeightGrams { get; set; }
}

public sealed class ProductListingInput
{
    public bool Published { get; set; }

    /// <summary>Empty means the default class. A PUT replaces the whole listing, so leaving it out clears a class set earlier.</summary>
    public Guid? TaxClassId { get; set; }

    /// <summary>Weight in grams (shipping rates by weight add these up). Also replaced by a PUT: leaving it out clears it.</summary>
    [System.ComponentModel.DataAnnotations.Range(0, 100_000_000)]
    public int? WeightGrams { get; set; }
}

public sealed record ProductListingDto(Guid ProductId, bool Published, Guid? TaxClassId = null, int? WeightGrams = null);

/// <summary>New variants start with their parent's online listing (published or not, tax class, weight), so they can be bought straight away.</summary>
internal sealed class VariantListingHandler(PiranhaCommerce.Data.PiranhaDbContext db) : IVariantCreatedHandler
{
    public async Task OnCreatedAsync(Guid parentProductId, IReadOnlyCollection<Guid> variantProductIds, CancellationToken ct = default)
    {
        var parent = await db.Set<ProductListing>().AsNoTracking().FirstOrDefaultAsync(l => l.ProductId == parentProductId, ct);
        if (parent is null) return;
        foreach (var id in variantProductIds)
            db.Add(new ProductListing { ProductId = id, Published = parent.Published, TaxClassId = parent.TaxClassId, WeightGrams = parent.WeightGrams });
        await db.SaveChangesAsync(ct);
    }
}

/// <summary>Adds the online listing to the catalogue import and export: <c>published</c> (true or false), <c>tax_class</c> (by name) and <c>weight_grams</c>.</summary>
internal sealed class ProductListingImportExtension(PiranhaCommerce.Data.PiranhaDbContext db, IProductListingService listings) : IProductImportExtension
{
    public IReadOnlyList<string> Columns { get; } = ["published", "tax_class", "weight_grams"];

    public async Task<IReadOnlyDictionary<Guid, IReadOnlyList<string?>>> ExportAsync(IReadOnlyCollection<Guid> productIds, CancellationToken ct = default)
    {
        var ids = productIds.ToList();
        var classes = await db.Set<TaxClass>().AsNoTracking().ToDictionaryAsync(c => c.Id, c => c.Name, ct);
        var rows = await db.Set<ProductListing>().AsNoTracking().Where(l => ids.Contains(l.ProductId)).ToListAsync(ct);
        return rows.ToDictionary(l => l.ProductId, l => (IReadOnlyList<string?>)
            [l.Published ? "true" : "false", l.TaxClassId is { } c ? classes.GetValueOrDefault(c) : null, l.WeightGrams?.ToString(System.Globalization.CultureInfo.InvariantCulture)]);
    }

    public async Task ImportAsync(Guid productId, IReadOnlyDictionary<string, string?> cells, bool created, CancellationToken ct = default)
    {
        var current = await listings.GetAsync(productId, ct) ?? throw new PiranhaCommerce.Core.Validation.PiranhaValidationException("published", "That product no longer exists.");
        var input = new ProductListingInput { Published = current.Published, TaxClassId = current.TaxClassId, WeightGrams = current.WeightGrams };

        if (cells.TryGetValue("published", out var published))
        {
            input.Published = published?.ToLowerInvariant() switch
            {
                "true" or "yes" or "1" => true,
                "false" or "no" or "0" or null => false,
                _ => throw new PiranhaCommerce.Core.Validation.PiranhaValidationException("published", "Use true or false."),
            };
        }
        if (cells.TryGetValue("tax_class", out var taxClass))
        {
            if (taxClass is null) input.TaxClassId = null; // back to the store's default class
            else
            {
                var name = taxClass.ToLower();
                input.TaxClassId = await db.Set<TaxClass>().AsNoTracking().Where(c => c.Name.ToLower() == name).Select(c => (Guid?)c.Id).FirstOrDefaultAsync(ct)
                    ?? throw new PiranhaCommerce.Core.Validation.PiranhaValidationException("tax_class", $"There is no tax class called {taxClass}.");
            }
        }
        if (cells.TryGetValue("weight_grams", out var weight))
        {
            if (weight is null) input.WeightGrams = null;
            else if (int.TryParse(weight, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var grams)) input.WeightGrams = grams;
            else throw new PiranhaCommerce.Core.Validation.PiranhaValidationException("weight_grams", "Use a whole number of grams.");
        }
        await listings.SetAsync(productId, input, ct);
    }
}
