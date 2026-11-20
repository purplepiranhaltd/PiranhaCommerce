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

public sealed record ShippingMethodDto(Guid Id, string Name, decimal Price, decimal? FreeOver, bool Active, int Position);

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
public sealed record QuoteDto(
    string Currency, decimal Subtotal, decimal ShippingTotal, string? TaxName, decimal TaxRate, decimal TaxTotal, decimal Total,
    IReadOnlyList<TaxLineDto> Taxes);

/// <summary>The one place totals are worked out, used by the quote and by checkout so they can never disagree.</summary>
public static class OrderPricing
{
    public static decimal ShippingFor(ShippingMethod? method, decimal subtotal)
        => method is null ? 0m : method.FreeOver is { } free && subtotal >= free ? 0m : method.Price;

    /// <summary>
    /// Tax for each line by ITS class, and for shipping by the default class, each rounded to the penny on its own (the way an
    /// invoice adds up), then summed. Used by the quote and by checkout so they can never disagree.
    /// </summary>
    /// <param name="lines">Each line's total and its product's tax class (null means the default class).</param>
    public static TaxCalculation Calculate(
        IReadOnlyList<(decimal LineTotal, Guid? TaxClassId)> lines, decimal shipping, IReadOnlyList<TaxRule> rules, Guid defaultClassId, string countryCode)
    {
        static decimal Round(decimal v) => Math.Round(v, 2, MidpointRounding.AwayFromZero);
        TaxedAmount Apply(decimal net, Guid classId)
        {
            var rule = RuleFor(rules, classId, countryCode);
            return rule is null ? new TaxedAmount(0m, null, 0m) : new TaxedAmount(Round(net * rule.Rate / 100m), rule.Name, rule.Rate);
        }

        var perLine = lines.Select(l => Apply(l.LineTotal, l.TaxClassId ?? defaultClassId)).ToList();
        var forShipping = Apply(shipping, defaultClassId);
        var subtotal = lines.Sum(l => l.LineTotal);
        var tax = perLine.Sum(l => l.Amount) + forShipping.Amount;

        var breakdown = perLine.Append(forShipping).Where(t => t.Name is not null)
            .GroupBy(t => (Name: t.Name!, t.Rate))
            .Select(g => new TaxLineDto(g.Key.Name, g.Key.Rate, g.Sum(t => t.Amount)))
            .OrderByDescending(t => t.Rate).ThenBy(t => t.Name).ToList();
        var (name, rate) = breakdown.Count switch { 0 => ((string?)null, 0m), 1 => (breakdown[0].Name, breakdown[0].Rate), _ => ("Tax", 0m) };
        return new TaxCalculation(perLine, forShipping, tax, subtotal + shipping + tax, name, rate, breakdown);
    }

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
    IReadOnlyList<TaxLineDto> Breakdown);

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
    Task<ShippingMethodDto?> UpdateAsync(Guid id, ShippingMethodInput input, CancellationToken ct = default);
    /// <summary>Orders keep their own copy of the method name and price, so deleting is always safe.</summary>
    Task<bool> DeleteAsync(Guid id, CancellationToken ct = default);
}

internal sealed class ShippingService(PiranhaDbContext db) : IShippingService
{
    public async Task<IReadOnlyList<ShippingMethodDto>> ListAsync(bool activeOnly, CancellationToken ct = default)
        => await db.Set<ShippingMethod>().AsNoTracking().Where(m => !activeOnly || m.Active)
            .OrderBy(m => m.Position).ThenBy(m => m.Name).ThenBy(m => m.Id)
            .Select(m => new ShippingMethodDto(m.Id, m.Name, m.Price, m.FreeOver, m.Active, m.Position)).ToListAsync(ct);

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
        return ToDto(m);
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
