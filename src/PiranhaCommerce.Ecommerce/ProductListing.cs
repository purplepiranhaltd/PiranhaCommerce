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
