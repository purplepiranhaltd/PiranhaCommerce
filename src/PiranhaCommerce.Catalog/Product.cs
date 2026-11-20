using PiranhaCommerce.Core.Domain;

namespace PiranhaCommerce.Catalog;

/// <summary>
/// A sellable item, independent of sales channel. Channel-specific data (online listing, EPOS till settings)
/// lives in the channel's own package in a table keyed by <see cref="Entity.Id"/>.
/// </summary>
public class Product : AuditableEntity
{
    public required string Name { get; set; }
    public string? Sku { get; set; }
    public string? Description { get; set; }
    public decimal Price { get; set; }
}
