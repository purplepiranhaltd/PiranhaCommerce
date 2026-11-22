using System.ComponentModel.DataAnnotations;
using Microsoft.EntityFrameworkCore;
using PiranhaCommerce.Core.Domain;
using PiranhaCommerce.Core.Validation;
using PiranhaCommerce.Data;

namespace PiranhaCommerce.Ecommerce;

/// <summary>A way of getting an order to the buyer, at a fixed price (optionally free above a spend).</summary>
public class ShippingMethod : AuditableEntity
{
    public required string Name { get; set; }
    public decimal Price { get; set; }
    /// <summary>Shipping is free when the basket subtotal reaches this amount.</summary>
    public decimal? FreeOver { get; set; }
    public bool Active { get; set; } = true;
    public int Position { get; set; }
}

/// <summary>
/// A kind of goods taxed alike (Standard, Reduced, Zero ...). Each product has one (the default class when none is chosen) and
/// each <see cref="TaxRule"/> belongs to one, so the same country can charge different rates on different goods.
/// </summary>
public class TaxClass : AuditableEntity
{
    public required string Name { get; set; }

    /// <summary>The class of products with none chosen, and of shipping. Exactly one class is the default.</summary>
    public bool IsDefault { get; set; }
}

public static class TaxClasses
{
    /// <summary>The "Standard" class every store starts with (and every tax rule created before classes existed belongs to).</summary>
    public static readonly Guid StandardId = new("00000000-0000-0000-0000-0000000000a1");
}

/// <summary>
/// Tax charged on top of the price (prices are tax-exclusive) for one <see cref="TaxClass"/>, by the country the order ships to.
/// <see cref="CountryCode"/> "*" is the fallback for countries without a rule of their own for that class. A class with no rule
/// for the country (and no fallback) is not taxed there.
/// </summary>
public class TaxRule : AuditableEntity
{
    public const string Fallback = "*";

    public Guid TaxClassId { get; set; } = TaxClasses.StandardId;
    public required string CountryCode { get; set; }
    public required string Name { get; set; }
    /// <summary>Percent, e.g. 20 for 20%.</summary>
    public decimal Rate { get; set; }
}

public sealed class ShippingMethodInput
{
    [Required, StringLength(100, MinimumLength = 1)] public string Name { get; set; } = "";
    [Range(0, 1_000_000)] public decimal Price { get; set; }
    [Range(0, 1_000_000_000)] public decimal? FreeOver { get; set; }
    public bool Active { get; set; } = true;
    [Range(0, 10_000)] public int Position { get; set; }
}

/// <param name="RateCount">How many rate rows the method has. Zero means the flat <paramref name="Price"/> applies everywhere.</param>
public sealed record ShippingMethodDto(Guid Id, string Name, decimal Price, decimal? FreeOver, bool Active, int Position, int RateCount = 0);

/// <summary>
/// One price band of a shipping method: for a destination country (or <c>*</c> for every other country) and a basket weight from
/// <see cref="MinWeightGrams"/> (inclusive) up to <see cref="MaxWeightGrams"/> (exclusive; null for no upper limit).
/// A method with no rate rows charges its flat price everywhere. With rate rows, a destination or weight no row covers is not served by the method.
/// </summary>
public class ShippingRate : Entity
{
    public Guid ShippingMethodId { get; set; }
    public required string CountryCode { get; set; }
    public int MinWeightGrams { get; set; }
    public int? MaxWeightGrams { get; set; }
    public decimal Price { get; set; }
}

public sealed class ShippingRateInput
{
    [Required, RegularExpression(@"^(\*|[A-Za-z]{2})$", ErrorMessage = "Use a two-letter country code such as GB, or * for every other country.")]
    public string CountryCode { get; set; } = "";
    [Range(0, 100_000_000)] public int MinWeightGrams { get; set; }
    [Range(1, 100_000_000)] public int? MaxWeightGrams { get; set; }
    [Range(0, 1_000_000)] public decimal Price { get; set; }
}

/// <summary>Replaces the whole set of rates of a method. An empty list goes back to the flat price.</summary>
public sealed class ShippingRatesInput
{
    public List<ShippingRateInput> Rates { get; set; } = [];
}

public sealed record ShippingRateDto(Guid Id, string CountryCode, int MinWeightGrams, int? MaxWeightGrams, decimal Price);

/// <summary>A shipping method a basket can use for a destination, with what it would cost.</summary>
public sealed record ShippingOptionDto(Guid Id, string Name, decimal Price);

/// <summary>Works out what a method charges for a destination and weight, before any free-over rule.</summary>
public static class ShippingRates
{
    public const string Fallback = "*";

    /// <summary>
    /// The flat price when the method has no rates. Otherwise the rate for the destination: rows for that exact country take
    /// precedence over the <c>*</c> rows, and the row whose weight band holds the basket weight sets the price. Null when nothing covers it
    /// (the method is not available for that destination and basket).
    /// </summary>
    public static decimal? PriceFor(ShippingMethod method, IReadOnlyList<ShippingRate> rates, string countryCode, long weightGrams)
    {
        if (rates.Count == 0) return method.Price;
        var exact = rates.Where(r => string.Equals(r.CountryCode, countryCode, StringComparison.OrdinalIgnoreCase)).ToList();
        var pool = exact.Count > 0 ? exact : rates.Where(r => r.CountryCode == Fallback).ToList();
        return pool.FirstOrDefault(r => weightGrams >= r.MinWeightGrams && (r.MaxWeightGrams is null || weightGrams < r.MaxWeightGrams))?.Price;
    }

    /// <summary>The first problem with a set of rows, or null: a band must end after it starts, and bands for one country must not overlap.</summary>
    public static string? Problem(IReadOnlyList<ShippingRateInput> rates)
    {
        foreach (var r in rates)
            if (r.MaxWeightGrams is { } max && max <= r.MinWeightGrams) return "A weight band must end above where it starts.";
        foreach (var group in rates.GroupBy(r => r.CountryCode.ToUpperInvariant()))
        {
            var ordered = group.OrderBy(r => r.MinWeightGrams).ToList();
            for (var i = 1; i < ordered.Count; i++)
                if (ordered[i - 1].MaxWeightGrams is null || ordered[i - 1].MaxWeightGrams > ordered[i].MinWeightGrams)
                    return $"The weight bands for {group.Key} overlap.";
        }
        return null;
    }
}

internal static class ShippingPricing
{
    /// <summary>Total weight in grams. A product with no weight set counts as nothing.</summary>
    public static async Task<long> WeightAsync(PiranhaDbContext db, IReadOnlyCollection<(Guid ProductId, int Quantity)> lines, CancellationToken ct)
    {
        var ids = lines.Select(l => l.ProductId).ToList();
        var weights = (await db.Set<ProductListing>().AsNoTracking().Where(l => ids.Contains(l.ProductId) && l.WeightGrams != null)
            .Select(l => new { l.ProductId, l.WeightGrams }).ToListAsync(ct)).ToDictionary(x => x.ProductId, x => (long)x.WeightGrams!.Value);
        return lines.Sum(l => l.Quantity * weights.GetValueOrDefault(l.ProductId));
    }

    /// <summary>What the method charges for this destination and weight; null when it does not serve them.</summary>
    public static async Task<decimal?> PriceAsync(PiranhaDbContext db, ShippingMethod method, string countryCode, long weightGrams, CancellationToken ct)
    {
        var rates = await db.Set<ShippingRate>().AsNoTracking().Where(r => r.ShippingMethodId == method.Id).ToListAsync(ct);
        return ShippingRates.PriceFor(method, rates, countryCode, weightGrams);
    }
}

public sealed class TaxClassInput
{
    [Required, StringLength(50, MinimumLength = 1)] public string Name { get; set; } = "";

    /// <summary>Make this the default class. A default cannot be turned off here: make another class the default instead.</summary>
    public bool IsDefault { get; set; }
}

public sealed record TaxClassDto(Guid Id, string Name, bool IsDefault);

public sealed class TaxRuleInput
{
    /// <summary>The class the rule is for. Empty means the default class.</summary>
    public Guid? TaxClassId { get; set; }
    [Required, RegularExpression(@"^(\*|[A-Za-z]{2})$", ErrorMessage = "Use a two-letter country code such as GB, or * for every other country.")]
    public string CountryCode { get; set; } = "";
    [Required, StringLength(50, MinimumLength = 1)] public string Name { get; set; } = "";
    [Range(0, 100)] public decimal Rate { get; set; }
}

public sealed record TaxRuleDto(Guid Id, string CountryCode, string Name, decimal Rate, Guid TaxClassId, string? TaxClassName);

/// <summary>One line of the tax summary: how much tax at which named rate.</summary>
public sealed record TaxLineDto(string Name, decimal Rate, decimal Amount);

public sealed class QuoteInput
{
    public Guid? ShippingMethodId { get; set; }
    [Required, RegularExpression("^[A-Za-z]{2}$")] public string CountryCode { get; set; } = "";
}

/// <param name="TaxName">The rate name when everything taxed shares one, "Tax" when rates are mixed, null when nothing is taxed.</param>
/// <param name="TaxRate">The shared rate, or 0 when rates are mixed or nothing is taxed. See <paramref name="Taxes"/> for the detail.</param>
/// <param name="DiscountTotal">What the promo code takes off the goods (0 when none applies). Total = Subtotal - DiscountTotal + ShippingTotal + TaxTotal.</param>
/// <param name="VoucherApplied">How much of the total a gift voucher on the basket would pay (a way of paying: it never changes the price or the tax). AmountDue is what is left.</param>
/// <param name="PromotionProblem">Set when the basket holds a code that cannot be used right now. The quote then ignores it, and checkout would refuse.</param>
public sealed record QuoteDto(
    string Currency, decimal Subtotal, decimal ShippingTotal, string? TaxName, decimal TaxRate, decimal TaxTotal, decimal Total,
    IReadOnlyList<TaxLineDto> Taxes, decimal DiscountTotal = 0m, string? PromotionCode = null, string? PromotionProblem = null,
    decimal VoucherApplied = 0m, decimal AmountDue = 0m, string? VoucherLast4 = null, string? VoucherProblem = null,
    IReadOnlyList<QuoteLineDto>? Lines = null, decimal ShippingTax = 0m);

/// <summary>One basket line as it will be charged: the goods after its share of any discount, its tax, and the two added together, so a shop can show tax-inclusive line prices.</summary>
public sealed record QuoteLineDto(
    Guid ProductId, string Name, int Quantity, decimal UnitPrice, decimal LineTotal, decimal DiscountAmount, string? TaxName, decimal TaxRate, decimal TaxAmount, decimal LineTotalIncludingTax);

/// <summary>The one place totals are worked out, used by the quote and by checkout so they can never disagree.</summary>
public static class OrderPricing
{
    /// <param name="price">What the method charges here when that is not its flat price (a rate for the destination and weight).</param>
    public static decimal ShippingFor(ShippingMethod? method, decimal subtotal, decimal? price = null)
        => method is null ? 0m : method.FreeOver is { } free && subtotal >= free ? 0m : price ?? method.Price;

    /// <summary>
    /// Every money figure of an order, worked out in one place so the quote and checkout can never disagree:
    /// the discount is shared across the lines, shipping is priced on the goods AFTER the discount (so "free over 50" means what the
    /// shopper actually pays for goods), then tax is charged per line on its discounted price, and on shipping, each rounded to the
    /// penny on its own (the way an invoice adds up). The discount never reduces shipping or tax directly.
    /// </summary>
    /// <param name="lines">Each line's total and its product's tax class (null means the default class).</param>
    /// <param name="discount">What the promo code takes off the goods; limited here to the goods total.</param>
    public static TaxCalculation Calculate(
        IReadOnlyList<(decimal LineTotal, Guid? TaxClassId)> lines, decimal discount, ShippingMethod? method,
        IReadOnlyList<TaxRule> rules, Guid defaultClassId, string countryCode, decimal? shippingPrice = null)
    {
        static decimal Round(decimal v) => Math.Round(v, 2, MidpointRounding.AwayFromZero);
        TaxedAmount Apply(decimal net, Guid classId)
        {
            var rule = RuleFor(rules, classId, countryCode);
            return rule is null ? new TaxedAmount(0m, null, 0m) : new TaxedAmount(Round(net * rule.Rate / 100m), rule.Name, rule.Rate);
        }

        var shares = PromotionRules.Allocate(lines.Select(l => l.LineTotal).ToList(), discount);
        var goodsAfterDiscount = lines.Sum(l => l.LineTotal) - shares.Sum();
        var shipping = ShippingFor(method, goodsAfterDiscount, shippingPrice);

        var perLine = lines.Select((l, i) => Apply(l.LineTotal - shares[i], l.TaxClassId ?? defaultClassId)).ToList();
        var forShipping = Apply(shipping, defaultClassId);
        var tax = perLine.Sum(l => l.Amount) + forShipping.Amount;

        var breakdown = perLine.Append(forShipping).Where(t => t.Name is not null)
            .GroupBy(t => (Name: t.Name!, t.Rate))
            .Select(g => new TaxLineDto(g.Key.Name, g.Key.Rate, g.Sum(t => t.Amount)))
            .OrderByDescending(t => t.Rate).ThenBy(t => t.Name).ToList();
        var (name, rate) = breakdown.Count switch { 0 => ((string?)null, 0m), 1 => (breakdown[0].Name, breakdown[0].Rate), _ => ("Tax", 0m) };
        return new TaxCalculation(perLine, forShipping, tax, goodsAfterDiscount + shipping + tax, name, rate, breakdown, shipping, shares.Sum(), shares);
    }

    /// <summary>A price with tax added at a rate, rounded to the penny (for showing a unit price; charges are worked out per line by <see cref="Calculate"/>).</summary>
    public static decimal IncludingTax(decimal price, decimal ratePercent) => price + Math.Round(price * ratePercent / 100m, 2, MidpointRounding.AwayFromZero);

    /// <summary>The class's rule for the country, else its fallback rule, else none (not taxed).</summary>
    public static TaxRule? RuleFor(IEnumerable<TaxRule> rules, Guid taxClassId, string countryCode)
    {
        var list = rules.Where(r => r.TaxClassId == taxClassId).ToList();
        return list.FirstOrDefault(r => string.Equals(r.CountryCode, countryCode, StringComparison.OrdinalIgnoreCase))
               ?? list.FirstOrDefault(r => r.CountryCode == TaxRule.Fallback);
    }
}

public sealed record TaxedAmount(decimal Amount, string? Name, decimal Rate);

/// <param name="Lines">Tax per line, in the order the lines were given.</param>
public sealed record TaxCalculation(
    IReadOnlyList<TaxedAmount> Lines, TaxedAmount Shipping, decimal Tax, decimal Total, string? SummaryName, decimal SummaryRate,
    IReadOnlyList<TaxLineDto> Breakdown, decimal ShippingTotal, decimal Discount, IReadOnlyList<decimal> LineDiscounts);

internal static class TaxLookup
{
    public static async Task<(List<TaxRule> Rules, Guid DefaultClassId)> LoadAsync(PiranhaDbContext db, CancellationToken ct)
    {
        var rules = await db.Set<TaxRule>().AsNoTracking().ToListAsync(ct);
        var def = await db.Set<TaxClass>().AsNoTracking().Where(c => c.IsDefault).OrderBy(c => c.Id).Select(c => c.Id).FirstOrDefaultAsync(ct);
        return (rules, def == Guid.Empty ? TaxClasses.StandardId : def);
    }

    /// <summary>The tax class set on each product's online listing (missing or null: the default class).</summary>
    public static async Task<Dictionary<Guid, Guid?>> ClassesAsync(PiranhaDbContext db, IReadOnlyCollection<Guid> productIds, CancellationToken ct)
        => (await db.Set<ProductListing>().AsNoTracking().Where(l => productIds.Contains(l.ProductId))
            .Select(l => new { l.ProductId, l.TaxClassId }).ToListAsync(ct)).ToDictionary(l => l.ProductId, l => l.TaxClassId);
}

public interface IShippingService
{
    Task<IReadOnlyList<ShippingMethodDto>> ListAsync(bool activeOnly, CancellationToken ct = default);
    Task<ShippingMethodDto> CreateAsync(ShippingMethodInput input, CancellationToken ct = default);

    /// <summary>The method's rate rows, oldest first by country then weight. Null when the method does not exist.</summary>
    Task<IReadOnlyList<ShippingRateDto>?> GetRatesAsync(Guid id, CancellationToken ct = default);

    /// <summary>Replaces all the method's rates (an empty list goes back to the flat price). Null when the method does not exist; a validation error when bands overlap.</summary>
    Task<IReadOnlyList<ShippingRateDto>?> SetRatesAsync(Guid id, ShippingRatesInput input, CancellationToken ct = default);
    Task<ShippingMethodDto?> UpdateAsync(Guid id, ShippingMethodInput input, CancellationToken ct = default);
    /// <summary>Orders keep their own copy of the method name and price, so deleting is always safe.</summary>
    Task<bool> DeleteAsync(Guid id, CancellationToken ct = default);
}

internal sealed class ShippingService(PiranhaDbContext db) : IShippingService
{
    public async Task<IReadOnlyList<ShippingMethodDto>> ListAsync(bool activeOnly, CancellationToken ct = default)
        => await db.Set<ShippingMethod>().AsNoTracking().Where(m => !activeOnly || m.Active)
            .OrderBy(m => m.Position).ThenBy(m => m.Name).ThenBy(m => m.Id)
            .Select(m => new ShippingMethodDto(m.Id, m.Name, m.Price, m.FreeOver, m.Active, m.Position, db.Set<ShippingRate>().Count(r => r.ShippingMethodId == m.Id))).ToListAsync(ct);

    public async Task<ShippingMethodDto> CreateAsync(ShippingMethodInput input, CancellationToken ct = default)
    {
        ModelValidator.ValidateOrThrow(input);
        var m = new ShippingMethod { Name = input.Name.Trim() };
        Apply(m, input);
        db.Add(m);
        await db.SaveChangesAsync(ct);
        return ToDto(m);
    }

    public async Task<ShippingMethodDto?> UpdateAsync(Guid id, ShippingMethodInput input, CancellationToken ct = default)
    {
        ModelValidator.ValidateOrThrow(input);
        var m = await db.Set<ShippingMethod>().FirstOrDefaultAsync(x => x.Id == id, ct);
        if (m is null) return null;
        Apply(m, input);
        m.UpdatedUtc = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);
        return ToDto(m) with { RateCount = await db.Set<ShippingRate>().CountAsync(r => r.ShippingMethodId == id, ct) };
    }

    public async Task<IReadOnlyList<ShippingRateDto>?> GetRatesAsync(Guid id, CancellationToken ct = default)
    {
        if (!await db.Set<ShippingMethod>().AnyAsync(m => m.Id == id, ct)) return null;
        return await db.Set<ShippingRate>().AsNoTracking().Where(r => r.ShippingMethodId == id)
            .OrderBy(r => r.CountryCode).ThenBy(r => r.MinWeightGrams).ThenBy(r => r.Id)
            .Select(r => new ShippingRateDto(r.Id, r.CountryCode, r.MinWeightGrams, r.MaxWeightGrams, r.Price)).ToListAsync(ct);
    }

    public async Task<IReadOnlyList<ShippingRateDto>?> SetRatesAsync(Guid id, ShippingRatesInput input, CancellationToken ct = default)
    {
        if (input.Rates is null || input.Rates.Count > 200) throw new PiranhaValidationException(nameof(ShippingRatesInput.Rates), "Give at most 200 rates.");
        foreach (var r in input.Rates) ModelValidator.ValidateOrThrow(r); // the list is not walked by the validator, so each row is
        if (ShippingRates.Problem(input.Rates) is { } problem) throw new PiranhaValidationException(nameof(ShippingRatesInput.Rates), problem);

        var strategy = db.Database.CreateExecutionStrategy();
        var found = await strategy.ExecuteInTransactionAsync(
            async token =>
            {
                db.ChangeTracker.Clear();
                // Touch the method row first: two simultaneous replacements then run one after the other instead of mixing their rows.
                var touched = await db.Set<ShippingMethod>().Where(m => m.Id == id).ExecuteUpdateAsync(s => s.SetProperty(m => m.UpdatedUtc, (DateTimeOffset?)DateTimeOffset.UtcNow), token);
                if (touched == 0) return false;
                await db.Set<ShippingRate>().Where(r => r.ShippingMethodId == id).ExecuteDeleteAsync(token);
                foreach (var r in input.Rates)
                    db.Add(new ShippingRate
                    {
                        ShippingMethodId = id, CountryCode = r.CountryCode.ToUpperInvariant(), MinWeightGrams = r.MinWeightGrams,
                        MaxWeightGrams = r.MaxWeightGrams, Price = r.Price,
                    });
                await db.SaveChangesAsync(token);
                return true;
            },
            async token => await db.Set<ShippingRate>().AsNoTracking().CountAsync(r => r.ShippingMethodId == id, token) == input.Rates.Count,
            ct);
        return found ? await GetRatesAsync(id, ct) : null;
    }

    public async Task<bool> DeleteAsync(Guid id, CancellationToken ct = default)
        => await db.Set<ShippingMethod>().Where(m => m.Id == id).ExecuteDeleteAsync(ct) > 0;

    private static void Apply(ShippingMethod m, ShippingMethodInput i)
    {
        m.Name = i.Name.Trim(); m.Price = i.Price; m.FreeOver = i.FreeOver; m.Active = i.Active; m.Position = i.Position;
    }

    private static ShippingMethodDto ToDto(ShippingMethod m) => new(m.Id, m.Name, m.Price, m.FreeOver, m.Active, m.Position);
}

public interface ITaxService
{
    Task<IReadOnlyList<TaxClassDto>> ListClassesAsync(CancellationToken ct = default);
    Task<TaxClassDto> CreateClassAsync(TaxClassInput input, CancellationToken ct = default);
    Task<TaxClassDto?> UpdateClassAsync(Guid id, TaxClassInput input, CancellationToken ct = default);
    /// <summary>Conflict when it is the default class or any rule or product still uses it. False when not found.</summary>
    Task<bool> DeleteClassAsync(Guid id, CancellationToken ct = default);

    Task<IReadOnlyList<TaxRuleDto>> ListAsync(CancellationToken ct = default);
    /// <summary>Validation error when the class already has a rule for that country.</summary>
    Task<TaxRuleDto> CreateAsync(TaxRuleInput input, CancellationToken ct = default);
    Task<TaxRuleDto?> UpdateAsync(Guid id, TaxRuleInput input, CancellationToken ct = default);
    Task<bool> DeleteAsync(Guid id, CancellationToken ct = default);
}

internal sealed class TaxService(PiranhaDbContext db) : ITaxService
{
    public async Task<IReadOnlyList<TaxClassDto>> ListClassesAsync(CancellationToken ct = default)
        => await db.Set<TaxClass>().AsNoTracking().OrderByDescending(c => c.IsDefault).ThenBy(c => c.Name).ThenBy(c => c.Id)
            .Select(c => new TaxClassDto(c.Id, c.Name, c.IsDefault)).ToListAsync(ct);

    public async Task<TaxClassDto> CreateClassAsync(TaxClassInput input, CancellationToken ct = default)
    {
        ModelValidator.ValidateOrThrow(input);
        var c = new TaxClass { Name = input.Name.Trim(), IsDefault = false };
        db.Add(c);
        await SaveClassAsync(ct);
        if (input.IsDefault) await MakeDefaultAsync(c.Id, ct);
        return new TaxClassDto(c.Id, c.Name, input.IsDefault);
    }

    public async Task<TaxClassDto?> UpdateClassAsync(Guid id, TaxClassInput input, CancellationToken ct = default)
    {
        ModelValidator.ValidateOrThrow(input);
        var c = await db.Set<TaxClass>().FirstOrDefaultAsync(x => x.Id == id, ct);
        if (c is null) return null;
        if (c.IsDefault && !input.IsDefault)
            throw new PiranhaConflictException("A store always has a default tax class. Make another class the default instead.");
        c.Name = input.Name.Trim();
        c.UpdatedUtc = DateTimeOffset.UtcNow;
        await SaveClassAsync(ct);
        if (input.IsDefault && !c.IsDefault) await MakeDefaultAsync(id, ct);
        return new TaxClassDto(c.Id, c.Name, input.IsDefault || c.IsDefault);
    }

    public async Task<bool> DeleteClassAsync(Guid id, CancellationToken ct = default)
    {
        var c = await db.Set<TaxClass>().AsNoTracking().Where(x => x.Id == id).Select(x => new { x.IsDefault }).FirstOrDefaultAsync(ct);
        if (c is null) return false;
        if (c.IsDefault) throw new PiranhaConflictException("The default tax class cannot be deleted. Make another class the default first.");
        if (await db.Set<TaxRule>().AnyAsync(r => r.TaxClassId == id, ct) || await db.Set<ProductListing>().AnyAsync(l => l.TaxClassId == id, ct))
            throw new PiranhaConflictException("This tax class is still used by tax rules or products. Remove or move them first.");
        try { return await db.Set<TaxClass>().Where(x => x.Id == id).ExecuteDeleteAsync(ct) > 0; }
        catch (DbUpdateException) { throw new PiranhaConflictException("This tax class is still used by tax rules or products. Remove or move them first."); }
    }

    /// <summary>Atomic: afterwards exactly this class is the default.</summary>
    private async Task MakeDefaultAsync(Guid id, CancellationToken ct)
    {
        var strategy = db.Database.CreateExecutionStrategy();
        await strategy.ExecuteInTransactionAsync(
            async token =>
            {
                await db.Set<TaxClass>().Where(c => c.Id != id && c.IsDefault).ExecuteUpdateAsync(s => s.SetProperty(c => c.IsDefault, false), token);
                await db.Set<TaxClass>().Where(c => c.Id == id).ExecuteUpdateAsync(s => s.SetProperty(c => c.IsDefault, true), token);
                return true;
            },
            async token => await db.Set<TaxClass>().AsNoTracking().AnyAsync(c => c.Id == id && c.IsDefault, token),
            ct);
    }

    private async Task SaveClassAsync(CancellationToken ct)
    {
        try { await db.SaveChangesAsync(ct); }
        catch (DbUpdateException)
        {
            db.ChangeTracker.Clear();
            throw new PiranhaValidationException(nameof(TaxClassInput.Name), "A tax class with that name already exists.");
        }
    }

    public async Task<IReadOnlyList<TaxRuleDto>> ListAsync(CancellationToken ct = default)
    {
        var names = await db.Set<TaxClass>().AsNoTracking().ToDictionaryAsync(c => c.Id, c => c.Name, ct);
        var rules = await db.Set<TaxRule>().AsNoTracking().OrderBy(r => r.CountryCode).ThenBy(r => r.Name).ToListAsync(ct);
        return rules.Select(r => ToDto(r, names.GetValueOrDefault(r.TaxClassId)))
            .OrderBy(r => r.TaxClassName).ThenBy(r => r.CountryCode).ToList();
    }

    public async Task<TaxRuleDto> CreateAsync(TaxRuleInput input, CancellationToken ct = default)
    {
        ModelValidator.ValidateOrThrow(input);
        var classId = await ResolveClassAsync(input.TaxClassId, ct);
        var rule = new TaxRule { TaxClassId = classId, CountryCode = Normalize(input.CountryCode), Name = input.Name.Trim(), Rate = input.Rate };
        db.Add(rule);
        await SaveUniqueAsync(ct);
        return ToDto(rule, await ClassNameAsync(classId, ct));
    }

    public async Task<TaxRuleDto?> UpdateAsync(Guid id, TaxRuleInput input, CancellationToken ct = default)
    {
        ModelValidator.ValidateOrThrow(input);
        var rule = await db.Set<TaxRule>().FirstOrDefaultAsync(r => r.Id == id, ct);
        if (rule is null) return null;
        rule.TaxClassId = await ResolveClassAsync(input.TaxClassId, ct);
        rule.CountryCode = Normalize(input.CountryCode); rule.Name = input.Name.Trim(); rule.Rate = input.Rate;
        rule.UpdatedUtc = DateTimeOffset.UtcNow;
        await SaveUniqueAsync(ct);
        return ToDto(rule, await ClassNameAsync(rule.TaxClassId, ct));
    }

    public async Task<bool> DeleteAsync(Guid id, CancellationToken ct = default)
        => await db.Set<TaxRule>().Where(r => r.Id == id).ExecuteDeleteAsync(ct) > 0;

    /// <summary>A given class must exist; none means the default class.</summary>
    private async Task<Guid> ResolveClassAsync(Guid? requested, CancellationToken ct)
    {
        if (requested is { } id)
        {
            if (!await db.Set<TaxClass>().AnyAsync(c => c.Id == id, ct))
                throw new PiranhaValidationException(nameof(TaxRuleInput.TaxClassId), "That tax class does not exist.");
            return id;
        }
        return (await TaxLookup.LoadAsync(db, ct)).DefaultClassId;
    }

    private Task<string?> ClassNameAsync(Guid id, CancellationToken ct)
        => db.Set<TaxClass>().AsNoTracking().Where(c => c.Id == id).Select(c => (string?)c.Name).FirstOrDefaultAsync(ct);

    private async Task SaveUniqueAsync(CancellationToken ct)
    {
        try { await db.SaveChangesAsync(ct); }
        catch (DbUpdateException)
        {
            db.ChangeTracker.Clear();
            throw new PiranhaValidationException(nameof(TaxRuleInput.CountryCode), "That tax class already has a rule for that country.");
        }
    }

    private static string Normalize(string code) => code == TaxRule.Fallback ? code : code.Trim().ToUpperInvariant();
    private static TaxRuleDto ToDto(TaxRule r, string? className) => new(r.Id, r.CountryCode, r.Name, r.Rate, r.TaxClassId, className);
}
