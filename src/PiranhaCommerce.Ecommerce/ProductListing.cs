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
}

public sealed class ProductListingInput
{
    public bool Published { get; set; }
}

public sealed record ProductListingDto(Guid ProductId, bool Published);
