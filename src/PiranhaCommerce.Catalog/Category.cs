using PiranhaCommerce.Core.Domain;

namespace PiranhaCommerce.Catalog;

/// <summary>A node in the product category tree (shared by every sales channel). Top-level categories have no parent.</summary>
public class Category : AuditableEntity
{
    public required string Name { get; set; }
    public string? Description { get; set; }
    public Guid? ParentId { get; set; }
    public int DisplayOrder { get; set; }
    public bool Active { get; set; } = true;
}

/// <summary>Links a product to a category. A product can be in several categories.</summary>
public class ProductCategory
{
    public Guid ProductId { get; set; }
    public Guid CategoryId { get; set; }
}
