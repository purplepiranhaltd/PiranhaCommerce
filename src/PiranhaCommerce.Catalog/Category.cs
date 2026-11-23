using PiranhaCommerce.Core.Domain;
using PiranhaCommerce.Core.Audit;

namespace PiranhaCommerce.Catalog;

/// <summary>A node in the product category tree (shared by every sales channel). Top-level categories have no parent.</summary>
[Audited]
public class Category : AuditableEntity
{
    public required string Name { get; set; }
    public string? Description { get; set; }
    public Guid? ParentId { get; set; }
    public int DisplayOrder { get; set; }
    public bool Active { get; set; } = true;

    /// <summary>The address segment a shop front uses for this item (lower case letters, digits and single hyphens). Unique. Empty means none.</summary>
    public string? Slug { get; set; }
    public string? MetaTitle { get; set; }
    public string? MetaDescription { get; set; }
}

/// <summary>Links a product to a category. A product can be in several categories.</summary>
public class ProductCategory
{
    public Guid ProductId { get; set; }
    public Guid CategoryId { get; set; }
}
