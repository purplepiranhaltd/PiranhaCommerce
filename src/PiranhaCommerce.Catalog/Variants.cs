using System.ComponentModel.DataAnnotations;
using PiranhaCommerce.Core.Audit;
using Microsoft.EntityFrameworkCore;
using PiranhaCommerce.Core.Domain;
using PiranhaCommerce.Core.Validation;
using PiranhaCommerce.Data;

namespace PiranhaCommerce.Catalog;

/// <summary>
/// An option a product comes in (Size, Colour). A product with options is the "parent": it holds the shared name, description and categories and is
/// never bought itself. Each combination of values is a variant, which is an ordinary <see cref="Product"/> (own SKU, price, stock, barcodes, listing),
/// so stock, baskets, orders, sales and group prices all work on variants with no special cases.
/// </summary>
[Audited]
public class ProductOption : Entity
{
    public Guid ProductId { get; set; }
    public required string Name { get; set; }
    public int Position { get; set; }
}

[Audited]
public class ProductOptionValue : Entity
{
    public Guid OptionId { get; set; }
    public required string Value { get; set; }
    public int Position { get; set; }
}

/// <summary>Marks a product as a variant of a parent. The primary key is the variant product itself.</summary>
[Audited]
public class ProductVariant
{
    public Guid ProductId { get; set; }
    public Guid ParentProductId { get; set; }
}

/// <summary>One chosen value of a variant: exactly one row per option of the parent.</summary>
public class ProductVariantValue
{
    public Guid ProductId { get; set; }
    public Guid OptionId { get; set; }
    public Guid OptionValueId { get; set; }
}

public sealed class ProductOptionInput
{
    [Required, StringLength(100, MinimumLength = 1)]
    public string Name { get; set; } = "";

    /// <summary>The values to start with (more can be added later).</summary>
    public List<string> Values { get; set; } = [];
}

public sealed class ProductOptionRenameInput
{
    [Required, StringLength(100, MinimumLength = 1)]
    public string Name { get; set; } = "";
}

public sealed class ProductOptionValueInput
{
    [Required, StringLength(100, MinimumLength = 1)]
    public string Value { get; set; } = "";
}

public sealed class ProductVariantInput
{
    /// <summary>One value id for every option of the parent.</summary>
    public List<Guid> ValueIds { get; set; } = [];

    [StringLength(100)]
    public string? Sku { get; set; }

    /// <summary>Empty means the parent's price.</summary>
    [Range(typeof(decimal), "0", "999999999")]
    public decimal? Price { get; set; }
}

public sealed class GenerateVariantsInput
{
    /// <summary>The price for every variant made; empty means the parent's price.</summary>
    [Range(typeof(decimal), "0", "999999999")]
    public decimal? Price { get; set; }
}

public sealed record OptionValueDto(Guid Id, string Value);
public sealed record ProductOptionDto(Guid Id, string Name, IReadOnlyList<OptionValueDto> Values);
public sealed record VariantValueDto(Guid OptionId, string Option, Guid ValueId, string Value);
public sealed record VariantDto(Guid ProductId, string Name, string? Sku, decimal Price, IReadOnlyList<VariantValueDto> Values);

/// <param name="ParentProductId">Set when the product is itself a variant (then <c>Options</c> and <c>Variants</c> are empty and <c>Values</c> says which it is).</param>
public sealed record ProductVariantsDto(
    Guid ProductId, Guid? ParentProductId, IReadOnlyList<ProductOptionDto> Options, IReadOnlyList<VariantDto> Variants, IReadOnlyList<VariantValueDto> Values);

/// <summary>Lets another package react to variants being made (the online store copies the parent's listing onto them).</summary>
public interface IVariantCreatedHandler
{
    Task OnCreatedAsync(Guid parentProductId, IReadOnlyCollection<Guid> variantProductIds, CancellationToken ct = default);
}

public static class ProductVariants
{
    /// <summary>True when the product has options, so it is a parent that cannot be bought itself: a variant must be chosen.</summary>
    public static Task<bool> HasOptionsAsync(PiranhaDbContext db, Guid productId, CancellationToken ct = default) =>
        db.Set<ProductOption>().AnyAsync(o => o.ProductId == productId, ct);

    /// <summary>
    /// The categories each product counts as being in for pricing: its own, plus its parent's when it is a variant (variants are not put in
    /// categories themselves, so a category sale or group discount covers them through the parent).
    /// </summary>
    public static async Task<Dictionary<Guid, List<Guid>>> CategoriesAsync(PiranhaDbContext db, IReadOnlyCollection<Guid> productIds, CancellationToken ct = default)
    {
        var parentOf = await db.Set<ProductVariant>().AsNoTracking().Where(v => productIds.Contains(v.ProductId))
            .ToDictionaryAsync(v => v.ProductId, v => v.ParentProductId, ct);
        var all = productIds.Concat(parentOf.Values).Distinct().ToList();
        var direct = (await db.Set<ProductCategory>().AsNoTracking().Where(x => all.Contains(x.ProductId)).ToListAsync(ct))
            .GroupBy(x => x.ProductId).ToDictionary(g => g.Key, g => g.Select(x => x.CategoryId).ToList());
        var result = new Dictionary<Guid, List<Guid>>();
        foreach (var id in productIds)
        {
            var list = new List<Guid>(direct.GetValueOrDefault(id) ?? []);
            if (parentOf.TryGetValue(id, out var parent)) list.AddRange(direct.GetValueOrDefault(parent) ?? []);
            if (list.Count > 0) result[id] = list.Distinct().ToList();
        }
        return result;
    }
}

public interface IProductVariantService
{
    Task<ProductVariantsDto?> GetAsync(Guid productId, CancellationToken ct = default);
    Task<ProductVariantsDto?> AddOptionAsync(Guid productId, ProductOptionInput input, CancellationToken ct = default);
    Task<ProductVariantsDto?> RenameOptionAsync(Guid productId, Guid optionId, ProductOptionRenameInput input, CancellationToken ct = default);
    Task<ProductVariantsDto?> DeleteOptionAsync(Guid productId, Guid optionId, CancellationToken ct = default);
    Task<ProductVariantsDto?> AddValueAsync(Guid productId, Guid optionId, ProductOptionValueInput input, CancellationToken ct = default);
    Task<ProductVariantsDto?> DeleteValueAsync(Guid productId, Guid optionId, Guid valueId, CancellationToken ct = default);
    Task<ProductVariantsDto?> AddVariantAsync(Guid productId, ProductVariantInput input, CancellationToken ct = default);
    Task<ProductVariantsDto?> GenerateAsync(Guid productId, GenerateVariantsInput input, CancellationToken ct = default);
}

internal sealed class ProductVariantService(PiranhaDbContext db, IEnumerable<IVariantCreatedHandler> handlers) : IProductVariantService
{
    public const int MaxOptions = 3, MaxValuesPerOption = 50, MaxVariants = 200;

    public async Task<ProductVariantsDto?> GetAsync(Guid productId, CancellationToken ct = default)
    {
        if (!await db.Set<Product>().AnyAsync(p => p.Id == productId, ct)) return null;
        var parentId = await db.Set<ProductVariant>().AsNoTracking().Where(v => v.ProductId == productId).Select(v => (Guid?)v.ParentProductId).FirstOrDefaultAsync(ct);
        var names = await ValueNamesAsync(parentId ?? productId, ct);
        if (parentId is not null)
        {
            var own = await ValuesOfAsync([productId], names, ct);
            return new ProductVariantsDto(productId, parentId, [], [], own.GetValueOrDefault(productId) ?? []);
        }

        var options = await db.Set<ProductOption>().AsNoTracking().Where(o => o.ProductId == productId).OrderBy(o => o.Position).ThenBy(o => o.Name).ToListAsync(ct);
        var optionIds = options.Select(o => o.Id).ToList();
        var values = await db.Set<ProductOptionValue>().AsNoTracking().Where(v => optionIds.Contains(v.OptionId)).OrderBy(v => v.Position).ThenBy(v => v.Value).ToListAsync(ct);
        var optionDtos = options.Select(o => new ProductOptionDto(o.Id, o.Name, values.Where(v => v.OptionId == o.Id).Select(v => new OptionValueDto(v.Id, v.Value)).ToList())).ToList();

        var variants = await (from v in db.Set<ProductVariant>().AsNoTracking()
                              join p in db.Set<Product>().AsNoTracking() on v.ProductId equals p.Id
                              where v.ParentProductId == productId
                              orderby p.Name
                              select new { p.Id, p.Name, p.Sku, p.Price }).ToListAsync(ct);
        var perVariant = await ValuesOfAsync(variants.Select(v => v.Id).ToList(), names, ct);
        return new ProductVariantsDto(productId, null, optionDtos,
            variants.Select(v => new VariantDto(v.Id, v.Name, v.Sku, v.Price, perVariant.GetValueOrDefault(v.Id) ?? [])).ToList(), []);
    }

    public async Task<ProductVariantsDto?> AddOptionAsync(Guid productId, ProductOptionInput input, CancellationToken ct = default)
    {
        ModelValidator.ValidateOrThrow(input);
        if (!await db.Set<Product>().AnyAsync(p => p.Id == productId, ct)) return null;
        await RequireParentAsync(productId, ct);
        if (await db.Set<ProductVariant>().AnyAsync(v => v.ParentProductId == productId, ct))
            throw new PiranhaConflictException("This product already has variants, so a new option would leave them without a value. Delete the variants first.");
        var existing = await db.Set<ProductOption>().Where(o => o.ProductId == productId).Select(o => o.Name).ToListAsync(ct);
        if (existing.Count >= MaxOptions) throw new PiranhaValidationException("name", $"A product can have at most {MaxOptions} options.");
        var name = input.Name.Trim();
        if (existing.Any(n => string.Equals(n, name, StringComparison.OrdinalIgnoreCase)))
            throw new PiranhaValidationException("name", "This product already has an option with that name.");

        var values = CleanValues(input.Values);
        var option = new ProductOption { ProductId = productId, Name = name, Position = existing.Count };
        db.Add(option);
        for (var i = 0; i < values.Count; i++) db.Add(new ProductOptionValue { OptionId = option.Id, Value = values[i], Position = i });
        await db.SaveChangesAsync(ct);
        return await GetAsync(productId, ct);
    }

    public async Task<ProductVariantsDto?> RenameOptionAsync(Guid productId, Guid optionId, ProductOptionRenameInput input, CancellationToken ct = default)
    {
        ModelValidator.ValidateOrThrow(input);
        var option = await db.Set<ProductOption>().FirstOrDefaultAsync(o => o.Id == optionId && o.ProductId == productId, ct);
        if (option is null) return null;
        var name = input.Name.Trim();
        var others = await db.Set<ProductOption>().Where(o => o.ProductId == productId && o.Id != optionId).Select(o => o.Name).ToListAsync(ct);
        if (others.Any(n => string.Equals(n, name, StringComparison.OrdinalIgnoreCase)))
            throw new PiranhaValidationException("name", "This product already has an option with that name.");
        option.Name = name;
        await db.SaveChangesAsync(ct);
        return await GetAsync(productId, ct);
    }

    public async Task<ProductVariantsDto?> DeleteOptionAsync(Guid productId, Guid optionId, CancellationToken ct = default)
    {
        if (!await db.Set<ProductOption>().AnyAsync(o => o.Id == optionId && o.ProductId == productId, ct)) return null;
        if (await db.Set<ProductVariant>().AnyAsync(v => v.ParentProductId == productId, ct))
            throw new PiranhaConflictException("This product has variants that use this option. Delete the variants first.");
        await db.DeleteTrackedAsync<ProductOption>(o => o.Id == optionId, ct); // its values go with it
        return await GetAsync(productId, ct);
    }

    public async Task<ProductVariantsDto?> AddValueAsync(Guid productId, Guid optionId, ProductOptionValueInput input, CancellationToken ct = default)
    {
        ModelValidator.ValidateOrThrow(input);
        if (!await db.Set<ProductOption>().AnyAsync(o => o.Id == optionId && o.ProductId == productId, ct)) return null;
        var existing = await db.Set<ProductOptionValue>().Where(v => v.OptionId == optionId).Select(v => v.Value).ToListAsync(ct);
        if (existing.Count >= MaxValuesPerOption) throw new PiranhaValidationException("value", $"An option can have at most {MaxValuesPerOption} values.");
        var value = input.Value.Trim();
        if (existing.Any(v => string.Equals(v, value, StringComparison.OrdinalIgnoreCase)))
            throw new PiranhaValidationException("value", "This option already has that value.");
        db.Add(new ProductOptionValue { OptionId = optionId, Value = value, Position = existing.Count });
        await db.SaveChangesAsync(ct);
        return await GetAsync(productId, ct);
    }

    public async Task<ProductVariantsDto?> DeleteValueAsync(Guid productId, Guid optionId, Guid valueId, CancellationToken ct = default)
    {
        if (!await db.Set<ProductOption>().AnyAsync(o => o.Id == optionId && o.ProductId == productId, ct)) return null;
        if (!await db.Set<ProductOptionValue>().AnyAsync(v => v.Id == valueId && v.OptionId == optionId, ct)) return null;
        if (await db.Set<ProductVariantValue>().AnyAsync(v => v.OptionValueId == valueId, ct))
            throw new PiranhaConflictException("A variant uses this value. Delete that variant first.");
        await db.DeleteTrackedAsync<ProductOptionValue>(v => v.Id == valueId, ct);
        return await GetAsync(productId, ct);
    }

    public async Task<ProductVariantsDto?> AddVariantAsync(Guid productId, ProductVariantInput input, CancellationToken ct = default)
    {
        ModelValidator.ValidateOrThrow(input);
        var parent = await db.Set<Product>().AsNoTracking().FirstOrDefaultAsync(p => p.Id == productId, ct);
        if (parent is null) return null;
        var (options, values) = await LoadOptionsAsync(productId, ct);
        if (options.Count == 0) throw new PiranhaValidationException("valueIds", "Add an option to this product first.");

        var chosen = input.ValueIds.Distinct().ToList();
        var picked = values.Where(v => chosen.Contains(v.Id)).ToList();
        if (chosen.Count != options.Count || picked.Count != chosen.Count || options.Any(o => picked.Count(v => v.OptionId == o.Id) != 1))
            throw new PiranhaValidationException("valueIds", "Choose exactly one value for each option of this product.");

        var combos = await ExistingCombinationsAsync(productId, ct);
        if (combos.Contains(Key(chosen))) throw new PiranhaConflictException("This product already has a variant with those values.");
        if (combos.Count >= MaxVariants) throw new PiranhaValidationException("valueIds", $"A product can have at most {MaxVariants} variants.");

        var sku = string.IsNullOrWhiteSpace(input.Sku) ? null : input.Sku.Trim();
        if (sku is not null && await db.Set<Product>().AnyAsync(x => x.Sku != null && x.Sku.ToLower() == sku.ToLower(), ct))
            throw new PiranhaValidationException("sku", "A product with this SKU already exists.");

        var created = Make(parent, options, picked, sku, input.Price ?? parent.Price);
        await db.SaveChangesAsync(ct);
        await NotifyAsync(productId, [created], ct);
        return await GetAsync(productId, ct);
    }

    public async Task<ProductVariantsDto?> GenerateAsync(Guid productId, GenerateVariantsInput input, CancellationToken ct = default)
    {
        ModelValidator.ValidateOrThrow(input);
        var parent = await db.Set<Product>().AsNoTracking().FirstOrDefaultAsync(p => p.Id == productId, ct);
        if (parent is null) return null;
        var (options, values) = await LoadOptionsAsync(productId, ct);
        if (options.Count == 0 || options.Any(o => values.All(v => v.OptionId != o.Id)))
            throw new PiranhaValidationException("options", "Every option needs at least one value before variants can be made.");

        var existing = await ExistingCombinationsAsync(productId, ct);
        IEnumerable<List<ProductOptionValue>> product = [[]];
        foreach (var o in options)
            product = product.SelectMany(prefix => values.Where(v => v.OptionId == o.Id).OrderBy(v => v.Position).Select(v => new List<ProductOptionValue>([.. prefix, v])));
        var missing = product.Where(c => !existing.Contains(Key(c.Select(v => v.Id)))).ToList();
        if (existing.Count + missing.Count > MaxVariants)
            throw new PiranhaValidationException("options", $"That would make {existing.Count + missing.Count} variants; a product can have at most {MaxVariants}.");

        var made = missing.Select(c => Make(parent, options, c, null, input.Price ?? parent.Price)).ToList();
        if (made.Count > 0)
        {
            await db.SaveChangesAsync(ct);
            await NotifyAsync(productId, made, ct);
        }
        return await GetAsync(productId, ct);
    }

    private Guid Make(Product parent, List<ProductOption> options, List<ProductOptionValue> picked, string? sku, decimal price)
    {
        var ordered = options.Select(o => picked.First(v => v.OptionId == o.Id)).ToList();
        var product = new Product { Name = Trim($"{parent.Name} - {string.Join(" / ", ordered.Select(v => v.Value))}", 400), Sku = sku, Description = parent.Description, Price = price };
        db.Add(product);
        db.Add(new ProductVariant { ProductId = product.Id, ParentProductId = parent.Id });
        foreach (var v in ordered) db.Add(new ProductVariantValue { ProductId = product.Id, OptionId = v.OptionId, OptionValueId = v.Id });
        return product.Id;
    }

    private async Task NotifyAsync(Guid parentId, IReadOnlyCollection<Guid> variantIds, CancellationToken ct)
    {
        foreach (var h in handlers) await h.OnCreatedAsync(parentId, variantIds, ct);
    }

    /// <summary>A parent cannot itself be a variant: no nesting.</summary>
    private async Task RequireParentAsync(Guid productId, CancellationToken ct)
    {
        if (await db.Set<ProductVariant>().AnyAsync(v => v.ProductId == productId, ct))
            throw new PiranhaConflictException("This product is a variant of another product, so it cannot have options of its own.");
    }

    private async Task<(List<ProductOption> Options, List<ProductOptionValue> Values)> LoadOptionsAsync(Guid productId, CancellationToken ct)
    {
        await RequireParentAsync(productId, ct);
        var options = await db.Set<ProductOption>().AsNoTracking().Where(o => o.ProductId == productId).OrderBy(o => o.Position).ToListAsync(ct);
        var ids = options.Select(o => o.Id).ToList();
        return (options, await db.Set<ProductOptionValue>().AsNoTracking().Where(v => ids.Contains(v.OptionId)).ToListAsync(ct));
    }

    private static string Key(IEnumerable<Guid> valueIds) => string.Join(",", valueIds.Order());

    private async Task<HashSet<string>> ExistingCombinationsAsync(Guid parentId, CancellationToken ct)
    {
        var rows = await (from v in db.Set<ProductVariant>().AsNoTracking()
                          join x in db.Set<ProductVariantValue>().AsNoTracking() on v.ProductId equals x.ProductId
                          where v.ParentProductId == parentId
                          select new { x.ProductId, x.OptionValueId }).ToListAsync(ct);
        return rows.GroupBy(r => r.ProductId).Select(g => Key(g.Select(r => r.OptionValueId))).ToHashSet();
    }

    private async Task<Dictionary<Guid, (string Option, string Value)>> ValueNamesAsync(Guid parentId, CancellationToken ct)
    {
        var rows = await (from o in db.Set<ProductOption>().AsNoTracking()
                          join v in db.Set<ProductOptionValue>().AsNoTracking() on o.Id equals v.OptionId
                          where o.ProductId == parentId
                          select new { v.Id, Option = o.Name, v.Value }).ToListAsync(ct);
        return rows.ToDictionary(r => r.Id, r => (r.Option, r.Value));
    }

    private async Task<Dictionary<Guid, IReadOnlyList<VariantValueDto>>> ValuesOfAsync(List<Guid> variantIds, Dictionary<Guid, (string Option, string Value)> names, CancellationToken ct)
    {
        var rows = await db.Set<ProductVariantValue>().AsNoTracking().Where(x => variantIds.Contains(x.ProductId)).ToListAsync(ct);
        var optionIds = rows.Select(r => r.OptionId).Distinct().ToList();
        var optionPosition = await db.Set<ProductOption>().AsNoTracking().Where(o => optionIds.Contains(o.Id)).ToDictionaryAsync(o => o.Id, o => o.Position, ct);
        return rows.GroupBy(r => r.ProductId).ToDictionary(g => g.Key, g => (IReadOnlyList<VariantValueDto>)g
            .OrderBy(r => optionPosition.GetValueOrDefault(r.OptionId))
            .Select(r => new VariantValueDto(r.OptionId, names.GetValueOrDefault(r.OptionValueId).Option ?? "", r.OptionValueId, names.GetValueOrDefault(r.OptionValueId).Value ?? "")).ToList());
    }

    private static List<string> CleanValues(IEnumerable<string> raw)
    {
        var list = new List<string>();
        foreach (var v in raw.Select(v => v?.Trim() ?? ""))
        {
            if (v.Length == 0) continue;
            if (v.Length > 100) throw new PiranhaValidationException("values", "A value can be at most 100 characters.");
            if (!list.Any(x => string.Equals(x, v, StringComparison.OrdinalIgnoreCase))) list.Add(v);
        }
        if (list.Count > MaxValuesPerOption) throw new PiranhaValidationException("values", $"An option can have at most {MaxValuesPerOption} values.");
        return list;
    }

    private static string Trim(string s, int max) => s.Length <= max ? s : s[..max];
}
