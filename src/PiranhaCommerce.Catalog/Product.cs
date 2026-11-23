using PiranhaCommerce.Core.Domain;
using PiranhaCommerce.Core.Audit;

namespace PiranhaCommerce.Catalog;

/// <summary>
/// A sellable item, independent of sales channel. Channel-specific data (online listing, EPOS till settings)
/// lives in the channel's own package in a table keyed by <see cref="Entity.Id"/>.
/// </summary>
[Audited]
public class Product : AuditableEntity
{
    public required string Name { get; set; }
    public string? Sku { get; set; }
    public string? Description { get; set; }
    public decimal Price { get; set; }

    /// <summary>The address segment a shop front uses for this item (lower case letters, digits and single hyphens). Unique. Empty means none.</summary>
    public string? Slug { get; set; }
    public string? MetaTitle { get; set; }
    public string? MetaDescription { get; set; }

    /// <summary>The product's brand, if any. Variants have none of their own: they show their parent's.</summary>
    public Guid? BrandId { get; set; }
}
