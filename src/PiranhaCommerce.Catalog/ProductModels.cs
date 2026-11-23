using System.ComponentModel.DataAnnotations;

namespace PiranhaCommerce.Catalog;

/// <summary>Create/update payload.</summary>
public sealed class ProductInput
{
    [Required, StringLength(400, MinimumLength = 1)]
    public string Name { get; set; } = "";

    [StringLength(100)]
    public string? Sku { get; set; }

    [StringLength(4000)]
    public string? Description { get; set; }

    [Range(typeof(decimal), "0", "999999999")]
    public decimal Price { get; set; }

    /// <summary>Lower case letters, digits and single hyphens, unique among products (it is lower-cased for you). A PUT replaces it: leave it out to clear it.</summary>
    [StringLength(200)]
    public string? Slug { get; set; }

    [StringLength(200)]
    public string? MetaTitle { get; set; }

    [StringLength(500)]
    public string? MetaDescription { get; set; }
}

public sealed record ProductDto(
    Guid Id, string Name, string? Sku, string? Description, decimal Price,
    DateTimeOffset CreatedUtc, DateTimeOffset? UpdatedUtc, string? Slug = null, string? MetaTitle = null, string? MetaDescription = null);
