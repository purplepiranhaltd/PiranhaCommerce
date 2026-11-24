using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using PiranhaCommerce.Api;
using PiranhaCommerce.Catalog;
using PiranhaCommerce.Core.Audit;
using PiranhaCommerce.Core.Domain;
using PiranhaCommerce.Core.Validation;
using PiranhaCommerce.Customers;
using PiranhaCommerce.Data;
using PiranhaCommerce.Core.Identity;

namespace PiranhaCommerce.Ecommerce;

/// <summary>
/// A quantity break: buy at least <see cref="MinQuantity"/> and each costs <see cref="Price"/>, or <see cref="Percent"/> off the regular price.
/// Optionally only for the members of one customer group. Several can apply; the cheapest wins, and never dearer than any other price the buyer gets.
/// </summary>
[Audited]
public class ProductPriceBreak : Entity
{
    public Guid ProductId { get; set; }
    /// <summary>Null for everyone.</summary>
    public Guid? GroupId { get; set; }
    public int MinQuantity { get; set; }
    /// <summary>The unit price at this quantity. Exactly one of <see cref="Price"/> and <see cref="Percent"/> is set.</summary>
    public decimal? Price { get; set; }
    public decimal? Percent { get; set; }
}

public sealed class PriceBreakInput : IValidatableObject
{
    /// <summary>2 or more (one is the normal price).</summary>
    [Range(2, 1_000_000)] public int MinQuantity { get; set; }
    /// <summary>Unit price at this quantity. Give this or <see cref="Percent"/>.</summary>
    [Range(typeof(decimal), "0", "1000000000")] public decimal? Price { get; set; }
    /// <summary>Percentage off the regular price, above 0 and up to 100, at most 2 decimal places.</summary>
    [Range(typeof(decimal), "0.01", "100")] public decimal? Percent { get; set; }
    /// <summary>Only for members of this customer group; empty for everyone.</summary>
    public Guid? GroupId { get; set; }

    public IEnumerable<ValidationResult> Validate(ValidationContext context)
    {
        if ((Price is null) == (Percent is null)) yield return new ValidationResult("Give either a price or a percentage.", [nameof(Price)]);
        if (Price is { } p && decimal.Round(p, 4) != p) yield return new ValidationResult("A price can have at most 4 decimal places.", [nameof(Price)]);
        if (Percent is { } c && decimal.Round(c, 2) != c) yield return new ValidationResult("The percentage can have at most 2 decimal places.", [nameof(Percent)]);
    }
}

public sealed class PriceBreaksInput
{
    /// <summary>The product's whole list: a PUT replaces it. At most 20; one per quantity and group.</summary>
    [MaxLength(20)] public List<PriceBreakInput> Breaks { get; set; } = [];
}

/// <param name="UnitPrice">What each costs at this quantity (the percentage worked out from the product's regular price).</param>
public sealed record PriceBreakDto(Guid Id, int MinQuantity, decimal? Price, decimal? Percent, Guid? GroupId, string? GroupName, decimal UnitPrice);

/// <summary>A cheaper unit price a shopper gets by buying more.</summary>
public sealed record ShopperPriceBreakDto(int MinQuantity, decimal UnitPrice, decimal? UnitPriceIncludingTax = null);

public interface IPriceBreakService
{
    /// <summary>Lowest quantity first. Null when the product does not exist.</summary>
    Task<IReadOnlyList<PriceBreakDto>?> ListAsync(Guid productId, CancellationToken ct = default);

    /// <summary>Replaces the product's breaks. Null when the product does not exist; validation error for a bad entry, a duplicate quantity (for the same group) or an unknown group.</summary>
    [RequiresPermission(Permissions.PricesManage)]
    Task<IReadOnlyList<PriceBreakDto>?> SetAsync(Guid productId, PriceBreaksInput input, CancellationToken ct = default);
}

internal static class PriceBreaks
{
    public static decimal UnitPrice(ProductPriceBreak b, decimal regular) => b.Price ?? GroupPricing.Percentage(regular, b.Percent ?? 0m);

    /// <summary>
    /// Lowers each product's price in <paramref name="prices"/> to a quantity break it has earned, if that is cheaper. <paramref name="regular"/> is the regular
    /// price (percentages come off it). Products not in <paramref name="quantities"/> count as one, which no break covers.
    /// </summary>
    public static async Task ApplyAsync(PiranhaCommerceDbContext db, Guid? customerId, IReadOnlyDictionary<Guid, decimal> regular, IReadOnlyDictionary<Guid, int> quantities, Dictionary<Guid, decimal> prices, CancellationToken ct)
    {
        var ids = quantities.Where(q => q.Value >= 2 && prices.ContainsKey(q.Key)).Select(q => q.Key).ToList();
        if (ids.Count == 0) return;
        var breaks = await db.Set<ProductPriceBreak>().AsNoTracking().Where(b => ids.Contains(b.ProductId)).ToListAsync(ct);
        if (breaks.Count == 0) return;
        var groups = await GroupsOfAsync(db, customerId, ct);
        foreach (var b in breaks)
        {
            if (b.MinQuantity > quantities[b.ProductId] || (b.GroupId is { } g && !groups.Contains(g))) continue;
            var unit = UnitPrice(b, regular[b.ProductId]);
            if (unit < prices[b.ProductId]) prices[b.ProductId] = unit;
        }
    }

    /// <summary>The breaks this shopper can use on each product that would be cheaper than <paramref name="single"/>, the price for one. Lowest quantity first, one per quantity.</summary>
    public static async Task<Dictionary<Guid, List<(int MinQuantity, decimal UnitPrice)>>> ForShopperAsync(PiranhaCommerceDbContext db, Guid? customerId, IReadOnlyDictionary<Guid, decimal> regular, IReadOnlyDictionary<Guid, decimal> single, CancellationToken ct)
    {
        var ids = regular.Keys.ToList();
        var breaks = await db.Set<ProductPriceBreak>().AsNoTracking().Where(b => ids.Contains(b.ProductId)).ToListAsync(ct);
        var result = new Dictionary<Guid, List<(int, decimal)>>();
        if (breaks.Count == 0) return result;
        var groups = await GroupsOfAsync(db, customerId, ct);
        foreach (var product in breaks.GroupBy(b => b.ProductId))
        {
            var list = new List<(int MinQuantity, decimal UnitPrice)>();
            var best = single[product.Key];
            foreach (var quantity in product.Where(b => b.GroupId is null || groups.Contains(b.GroupId.Value)).GroupBy(b => b.MinQuantity).OrderBy(g => g.Key))
            {
                var unit = quantity.Min(b => UnitPrice(b, regular[product.Key]));
                if (unit < best) { best = unit; list.Add((quantity.Key, unit)); } // only a real improvement on a smaller quantity is worth showing
            }
            if (list.Count > 0) result[product.Key] = list;
        }
        return result;
    }

    private static async Task<HashSet<Guid>> GroupsOfAsync(PiranhaCommerceDbContext db, Guid? customerId, CancellationToken ct) =>
        customerId is { } c ? (await db.Set<CustomerGroupMember>().AsNoTracking().Where(m => m.CustomerId == c).Select(m => m.GroupId).ToListAsync(ct)).ToHashSet() : [];
}

/// <summary>Minimum and maximum order quantities, set on the online listing. Checked when a line is set on a basket and again at checkout.</summary>
internal static class OrderQuantityRules
{
    public static string? Problem(string name, int quantity, int? min, int? max)
    {
        if (min is { } lo && quantity < lo) return $"\"{name}\" must be ordered in quantities of at least {lo}.";
        if (max is { } hi && quantity > hi) return $"\"{name}\" can be ordered in quantities of at most {hi}.";
        return null;
    }
}

internal sealed class PriceBreakService(PiranhaCommerceDbContext db) : IPriceBreakService
{
    public async Task<IReadOnlyList<PriceBreakDto>?> ListAsync(Guid productId, CancellationToken ct = default)
    {
        var regular = await db.Set<Product>().AsNoTracking().Where(p => p.Id == productId).Select(p => (decimal?)p.Price).FirstOrDefaultAsync(ct);
        if (regular is null) return null;
        var groups = await db.Set<CustomerGroup>().AsNoTracking().ToDictionaryAsync(g => g.Id, g => g.Name, ct);
        return (await db.Set<ProductPriceBreak>().AsNoTracking().Where(b => b.ProductId == productId).ToListAsync(ct))
            .OrderBy(b => b.MinQuantity).ThenBy(b => b.GroupId.HasValue).ThenBy(b => groups.GetValueOrDefault(b.GroupId ?? Guid.Empty), StringComparer.OrdinalIgnoreCase)
            .Select(b => new PriceBreakDto(b.Id, b.MinQuantity, b.Price, b.Percent, b.GroupId, b.GroupId is { } g ? groups.GetValueOrDefault(g) : null, PriceBreaks.UnitPrice(b, regular.Value))).ToList();
    }

    public async Task<IReadOnlyList<PriceBreakDto>?> SetAsync(Guid productId, PriceBreaksInput input, CancellationToken ct = default)
    {
        ModelValidator.ValidateOrThrow(input);
        foreach (var b in input.Breaks) ModelValidator.ValidateOrThrow(b);
        if (!await db.Set<Product>().AnyAsync(p => p.Id == productId, ct)) return null;
        if (input.Breaks.GroupBy(b => (b.GroupId, b.MinQuantity)).Any(g => g.Count() > 1))
            throw new PiranhaCommerceValidationException(nameof(PriceBreaksInput.Breaks), "Each quantity can appear once for everyone and once for each group.");
        var groupIds = input.Breaks.Where(b => b.GroupId is not null).Select(b => b.GroupId!.Value).Distinct().ToList();
        if (groupIds.Count > 0 && await db.Set<CustomerGroup>().CountAsync(g => groupIds.Contains(g.Id), ct) != groupIds.Count)
            throw new PiranhaCommerceValidationException(nameof(PriceBreakInput.GroupId), "One of the customer groups does not exist.");

        var strategy = db.Database.CreateExecutionStrategy();
        await strategy.ExecuteInTransactionAsync(
            async token =>
            {
                db.ChangeTracker.Clear();
                await db.DeleteTrackedAsync<ProductPriceBreak>(b => b.ProductId == productId, token);
                foreach (var b in input.Breaks.OrderBy(b => b.MinQuantity))
                    db.Add(new ProductPriceBreak { ProductId = productId, GroupId = b.GroupId, MinQuantity = b.MinQuantity, Price = b.Price, Percent = b.Percent });
                await db.SaveChangesAsync(token);
                return true;
            },
            async token => await db.Set<ProductPriceBreak>().AsNoTracking().CountAsync(b => b.ProductId == productId, token) == input.Breaks.Count,
            ct);
        return await ListAsync(productId, ct);
    }
}

internal sealed class PriceBreakEndpoints : IApiEndpointContributor
{
    public void MapEndpoints(IEndpointRouteBuilder api)
    {
        var g = api.MapGroup("/products/{productId:guid}/price-breaks").WithTags("Quantity breaks").RequirePermission(Permissions.CatalogView);
        g.MapGet("/", async (Guid productId, IPriceBreakService svc, CancellationToken ct) =>
            await svc.ListAsync(productId, ct) is { } rows ? Results.Ok(rows) : Results.NotFound());
        g.MapPut("/", async (Guid productId, PriceBreaksInput input, IPriceBreakService svc, CancellationToken ct) =>
                await svc.SetAsync(productId, input, ct) is { } rows ? Results.Ok(rows) : Results.NotFound())
            .RequirePermission(Permissions.PricesManage);
    }
}
