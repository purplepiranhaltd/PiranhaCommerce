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
/// Tax charged on top of (prices are tax-exclusive) the goods plus shipping, by the country the order ships to.
/// <see cref="CountryCode"/> "*" is the fallback for countries without a rule of their own. No rule at all means no tax.
/// </summary>
public class TaxRule : AuditableEntity
{
    public const string Fallback = "*";

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

public sealed class TaxRuleInput
{
    [Required, RegularExpression(@"^(\*|[A-Za-z]{2})$", ErrorMessage = "Use a two-letter country code such as GB, or * for every other country.")]
    public string CountryCode { get; set; } = "";
    [Required, StringLength(50, MinimumLength = 1)] public string Name { get; set; } = "";
    [Range(0, 100)] public decimal Rate { get; set; }
}

public sealed record TaxRuleDto(Guid Id, string CountryCode, string Name, decimal Rate);

public sealed class QuoteInput
{
    public Guid? ShippingMethodId { get; set; }
    [Required, RegularExpression("^[A-Za-z]{2}$")] public string CountryCode { get; set; } = "";
}

public sealed record QuoteDto(string Currency, decimal Subtotal, decimal ShippingTotal, string? TaxName, decimal TaxRate, decimal TaxTotal, decimal Total);

/// <summary>The one place totals are worked out, used by the quote and by checkout so they can never disagree.</summary>
public static class OrderPricing
{
    public static decimal ShippingFor(ShippingMethod? method, decimal subtotal)
        => method is null ? 0m : method.FreeOver is { } free && subtotal >= free ? 0m : method.Price;

    public static (decimal Tax, decimal Total) Totals(decimal subtotal, decimal shipping, TaxRule? rule)
    {
        var tax = rule is null ? 0m : Math.Round((subtotal + shipping) * rule.Rate / 100m, 2, MidpointRounding.AwayFromZero);
        return (tax, subtotal + shipping + tax);
    }

    /// <summary>The rule for the country, else the fallback rule, else none.</summary>
    public static TaxRule? RuleFor(IEnumerable<TaxRule> rules, string countryCode)
    {
        var list = rules.ToList();
        return list.FirstOrDefault(r => string.Equals(r.CountryCode, countryCode, StringComparison.OrdinalIgnoreCase))
               ?? list.FirstOrDefault(r => r.CountryCode == TaxRule.Fallback);
    }
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
    Task<IReadOnlyList<TaxRuleDto>> ListAsync(CancellationToken ct = default);
    /// <summary>Validation error when the country already has a rule.</summary>
    Task<TaxRuleDto> CreateAsync(TaxRuleInput input, CancellationToken ct = default);
    Task<TaxRuleDto?> UpdateAsync(Guid id, TaxRuleInput input, CancellationToken ct = default);
    Task<bool> DeleteAsync(Guid id, CancellationToken ct = default);
}

internal sealed class TaxService(PiranhaDbContext db) : ITaxService
{
    public async Task<IReadOnlyList<TaxRuleDto>> ListAsync(CancellationToken ct = default)
        => (await db.Set<TaxRule>().AsNoTracking().OrderBy(r => r.CountryCode).ToListAsync(ct)).Select(ToDto).ToList();

    public async Task<TaxRuleDto> CreateAsync(TaxRuleInput input, CancellationToken ct = default)
    {
        ModelValidator.ValidateOrThrow(input);
        var rule = new TaxRule { CountryCode = Normalize(input.CountryCode), Name = input.Name.Trim(), Rate = input.Rate };
        db.Add(rule);
        await SaveUniqueAsync(ct);
        return ToDto(rule);
    }

    public async Task<TaxRuleDto?> UpdateAsync(Guid id, TaxRuleInput input, CancellationToken ct = default)
    {
        ModelValidator.ValidateOrThrow(input);
        var rule = await db.Set<TaxRule>().FirstOrDefaultAsync(r => r.Id == id, ct);
        if (rule is null) return null;
        rule.CountryCode = Normalize(input.CountryCode); rule.Name = input.Name.Trim(); rule.Rate = input.Rate;
        rule.UpdatedUtc = DateTimeOffset.UtcNow;
        await SaveUniqueAsync(ct);
        return ToDto(rule);
    }

    public async Task<bool> DeleteAsync(Guid id, CancellationToken ct = default)
        => await db.Set<TaxRule>().Where(r => r.Id == id).ExecuteDeleteAsync(ct) > 0;

    private async Task SaveUniqueAsync(CancellationToken ct)
    {
        try { await db.SaveChangesAsync(ct); }
        catch (DbUpdateException)
        {
            db.ChangeTracker.Clear();
            throw new PiranhaValidationException(nameof(TaxRuleInput.CountryCode), "That country already has a tax rule.");
        }
    }

    private static string Normalize(string code) => code == TaxRule.Fallback ? code : code.Trim().ToUpperInvariant();
    private static TaxRuleDto ToDto(TaxRule r) => new(r.Id, r.CountryCode, r.Name, r.Rate);
}
