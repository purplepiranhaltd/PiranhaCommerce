using System.ComponentModel.DataAnnotations;

namespace PiranhaCommerce.Ecommerce.Catalog;

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

    public bool Published { get; set; }
}

public sealed record ProductDto(
    Guid Id, string Name, string? Sku, string? Description, decimal Price, bool Published,
    DateTimeOffset CreatedUtc, DateTimeOffset? UpdatedUtc);
