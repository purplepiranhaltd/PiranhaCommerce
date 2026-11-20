using System.ComponentModel.DataAnnotations;

namespace PiranhaCommerce.Catalog;

public sealed class CategoryInput
{
    [Required, StringLength(200, MinimumLength = 1)]
    public string Name { get; set; } = "";

    [StringLength(4000)]
    public string? Description { get; set; }

    public Guid? ParentId { get; set; }

    [Range(0, 1_000_000)]
    public int DisplayOrder { get; set; }

    public bool Active { get; set; } = true;
}

public sealed record CategoryDto(
    Guid Id, string Name, string? Description, Guid? ParentId, int DisplayOrder, bool Active,
    DateTimeOffset CreatedUtc, DateTimeOffset? UpdatedUtc);

/// <summary>Replaces the full set of categories a product belongs to.</summary>
public sealed class ProductCategoriesInput
{
    public List<Guid> CategoryIds { get; set; } = [];
}

public sealed record ProductCategoriesDto(Guid ProductId, IReadOnlyList<Guid> CategoryIds);

/// <summary>Orders a flat category list as a depth-first tree (siblings by DisplayOrder, then Name) for display.</summary>
public static class CategoryTree
{
    public static List<(CategoryDto Category, int Depth)> Flatten(IEnumerable<CategoryDto> categories)
    {
        var all = categories.ToList();
        var ids = all.Select(c => c.Id).ToHashSet();
        var byParent = all.ToLookup(c => c.ParentId is { } p && ids.Contains(p) ? p : (Guid?)null);
        var result = new List<(CategoryDto, int)>(all.Count);

        void Walk(Guid? parent, int depth)
        {
            foreach (var c in byParent[parent].OrderBy(c => c.DisplayOrder).ThenBy(c => c.Name).ThenBy(c => c.Id))
            {
                result.Add((c, depth));
                Walk(c.Id, depth + 1);
            }
        }
        Walk(null, 0);
        return result;
    }

    /// <summary>Ids of <paramref name="id"/> and every category below it.</summary>
    public static HashSet<Guid> SelfAndDescendants(IEnumerable<CategoryDto> categories, Guid id)
    {
        var all = categories.ToList();
        var set = new HashSet<Guid> { id };
        bool added;
        do
        {
            added = false;
            foreach (var c in all)
                if (c.ParentId is { } p && set.Contains(p) && set.Add(c.Id)) added = true;
        } while (added);
        return set;
    }
}
