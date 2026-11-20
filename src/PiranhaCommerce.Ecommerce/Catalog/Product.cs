using PiranhaCommerce.Core.Domain;

namespace PiranhaCommerce.Ecommerce.Catalog;

public class Product : AuditableEntity
{
    public required string Name { get; set; }
    public string? Sku { get; set; }
    public string? Description { get; set; }
    public decimal Price { get; set; }
    public bool Published { get; set; }
}
