using System.ComponentModel.DataAnnotations;
using PiranhaCommerce.Core.Audit;
using Microsoft.EntityFrameworkCore;
using PiranhaCommerce.Core.Domain;
using PiranhaCommerce.Core.Validation;
using PiranhaCommerce.Data;

namespace PiranhaCommerce.Ecommerce;

public enum PromotionKind { Percent, Amount }

/// <summary>
/// A promo code: a discount on the goods in a basket, taken off BEFORE shipping and tax are worked out. It never reduces shipping or
/// tax directly (tax falls only because the goods it is charged on cost less).
/// </summary>
[Audited]
public class Promotion : AuditableEntity
{
    /// <summary>Upper-case. Fixed once created: orders and usage counts refer to it.</summary>
    public required string Code { get; set; }
    public string? Name { get; set; }
    public PromotionKind Kind { get; set; }

    /// <summary>Percent (0-100) or a money amount, by <see cref="Kind"/>.</summary>
    public decimal Value { get; set; }

    /// <summary>The goods must total at least this (before the discount) for the code to apply.</summary>
    public decimal? MinSubtotal { get; set; }
    public DateTimeOffset? StartsUtc { get; set; }
    public DateTimeOffset? EndsUtc { get; set; }

    /// <summary>Most orders that may use the code. Empty means no limit.</summary>
    public int? MaxRedemptions { get; set; }
    public int Redeemed { get; set; }
    public bool Active { get; set; } = true;

    /// <summary>When set, only signed-in customers in this group can use the code. A plain value in the model with a database link (restrict).</summary>
    public Guid? CustomerGroupId { get; set; }
}

public sealed class PromotionInput
{
    [Required, RegularExpression("^[A-Za-z0-9_-]{3,40}$", ErrorMessage = "Use 3 to 40 letters, digits, - or _.")]
    public string Code { get; set; } = "";

    [StringLength(100)] public string? Name { get; set; }
    public PromotionKind Kind { get; set; }

    [Range(0.01, 1_000_000)] public decimal Value { get; set; }
    [Range(0, 1_000_000_000)] public decimal? MinSubtotal { get; set; }
    public DateTimeOffset? StartsUtc { get; set; }
    public DateTimeOffset? EndsUtc { get; set; }
    [Range(1, int.MaxValue)] public int? MaxRedemptions { get; set; }
    public bool Active { get; set; } = true;

    /// <summary>Limit the code to the members of this customer group. Empty means anyone.</summary>
    public Guid? CustomerGroupId { get; set; }
}

public sealed record PromotionDto(
    Guid Id, string Code, string? Name, PromotionKind Kind, decimal Value, decimal? MinSubtotal, DateTimeOffset? StartsUtc,
    DateTimeOffset? EndsUtc, int? MaxRedemptions, int Redeemed, bool Active, Guid? CustomerGroupId = null, string? CustomerGroupName = null);

public sealed class CodeInput
{
    [Required, StringLength(64)] public string Code { get; set; } = "";
}

/// <summary>The rules for whether a code works and what it takes off. Used by the basket, the quote and checkout.</summary>
public static class PromotionRules
{
    public const string NotValid = "That code is not valid.";

    public static string Normalize(string code) => code.Trim().ToUpperInvariant();

    /// <summary>Why the code cannot be used right now, or null when it can. <paramref name="goods"/> is the basket's goods total before the discount.</summary>
    public static string? Problem(Promotion? p, decimal goods, DateTimeOffset now, bool eligible = true)
    {
        if (p is null || !p.Active) return NotValid;
        if (!eligible) return NotValid; // a code for a group looks like any other bad code to someone outside it
        if (p.StartsUtc is { } start && start > now) return NotValid;
        if (p.EndsUtc is { } end && end <= now) return NotValid;
        if (p.MaxRedemptions is { } max && p.Redeemed >= max) return "That code has been used up.";
        if (p.MinSubtotal is { } min && goods < min) return $"Spend at least {min:0.##} to use that code.";
        return null;
    }

    /// <summary>What the code takes off the goods: a percentage (rounded to the penny) or a fixed amount, never more than the goods cost.</summary>
    public static decimal Discount(Promotion p, decimal goods)
    {
        var raw = p.Kind == PromotionKind.Percent
            ? Math.Round(goods * Math.Min(p.Value, 100m) / 100m, 2, MidpointRounding.AwayFromZero)
            : p.Value;
        return Math.Max(0m, Math.Min(raw, goods));
    }

    /// <summary>
    /// Shares the discount across the lines in proportion to what each costs, to the penny, so the shares add up to the discount
    /// exactly and no line goes below zero. Used so tax is charged on what each line actually costs after the discount.
    /// </summary>
    public static decimal[] Allocate(IReadOnlyList<decimal> lineTotals, decimal discount)
    {
        var shares = new decimal[lineTotals.Count];
        var goods = lineTotals.Sum();
        discount = Math.Min(discount, goods);
        if (discount <= 0 || goods <= 0) return shares;

        for (var i = 0; i < shares.Length; i++)
            shares[i] = Math.Round(discount * lineTotals[i] / goods, 2, MidpointRounding.AwayFromZero);

        var remainder = discount - shares.Sum();
        foreach (var i in Enumerable.Range(0, shares.Length).OrderByDescending(i => lineTotals[i]).ThenBy(i => i))
        {
            if (remainder == 0) break;
            var take = Math.Clamp(remainder, -shares[i], lineTotals[i] - shares[i]);
            shares[i] += take;
            remainder -= take;
        }
        return shares;
    }
}

public interface IPromotionService
{
    Task<IReadOnlyList<PromotionDto>> ListAsync(CancellationToken ct = default);
    Task<PromotionDto?> GetAsync(Guid id, CancellationToken ct = default);
    /// <summary>Validation error when the code is taken (whatever the letter case), the percentage is over 100, or the dates are backwards.</summary>
    Task<PromotionDto> CreateAsync(PromotionInput input, CancellationToken ct = default);
    /// <summary>The code itself cannot be changed. Null when not found.</summary>
    Task<PromotionDto?> UpdateAsync(Guid id, PromotionInput input, CancellationToken ct = default);
    /// <summary>Orders keep their own copy of the code and discount, so deleting is always safe.</summary>
    Task<bool> DeleteAsync(Guid id, CancellationToken ct = default);
}

/// <summary>Whether a customer may use a code that is limited to a customer group.</summary>
internal static class PromotionEligibility
{
    public static async Task<bool> Async(PiranhaCommerceDbContext db, Promotion? p, Guid? customerId, CancellationToken ct)
    {
        if (p?.CustomerGroupId is not { } groupId) return true;
        return customerId is { } c && await db.Set<PiranhaCommerce.Customers.CustomerGroupMember>().AsNoTracking().AnyAsync(m => m.GroupId == groupId && m.CustomerId == c, ct);
    }
}

internal sealed class PromotionService(PiranhaCommerceDbContext db) : IPromotionService
{
    public async Task<IReadOnlyList<PromotionDto>> ListAsync(CancellationToken ct = default)
        => await ToDtosAsync(await db.Set<Promotion>().AsNoTracking().OrderBy(p => p.Code).ToListAsync(ct), ct);

    public async Task<PromotionDto?> GetAsync(Guid id, CancellationToken ct = default)
    {
        var p = await db.Set<Promotion>().AsNoTracking().FirstOrDefaultAsync(x => x.Id == id, ct);
        return p is null ? null : (await ToDtosAsync([p], ct))[0];
    }

    public async Task<PromotionDto> CreateAsync(PromotionInput input, CancellationToken ct = default)
    {
        Validate(input);
        await CheckGroupAsync(input, ct);
        var p = new Promotion { Code = PromotionRules.Normalize(input.Code) };
        Apply(p, input);
        db.Add(p);
        try { await db.SaveChangesAsync(ct); }
        catch (DbUpdateException)
        {
            db.ChangeTracker.Clear();
            throw new PiranhaCommerceValidationException(nameof(PromotionInput.Code), "That code already exists.");
        }
        return (await GetAsync(p.Id, ct))!;
    }

    public async Task<PromotionDto?> UpdateAsync(Guid id, PromotionInput input, CancellationToken ct = default)
    {
        Validate(input);
        await CheckGroupAsync(input, ct);
        var p = await db.Set<Promotion>().FirstOrDefaultAsync(x => x.Id == id, ct);
        if (p is null) return null;
        if (PromotionRules.Normalize(input.Code) != p.Code)
            throw new PiranhaCommerceValidationException(nameof(PromotionInput.Code), "A code cannot be changed. Create a new promotion instead.");
        Apply(p, input);
        p.UpdatedUtc = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);
        return (await GetAsync(p.Id, ct))!;
    }

    private async Task CheckGroupAsync(PromotionInput input, CancellationToken ct)
    {
        if (input.CustomerGroupId is { } g && !await db.Set<PiranhaCommerce.Customers.CustomerGroup>().AnyAsync(x => x.Id == g, ct))
            throw new PiranhaCommerceValidationException(nameof(PromotionInput.CustomerGroupId), "That customer group does not exist.");
    }

    public async Task<bool> DeleteAsync(Guid id, CancellationToken ct = default)
        => await db.DeleteTrackedAsync<Promotion>(p => p.Id == id, ct) > 0;

    private static void Validate(PromotionInput input)
    {
        ModelValidator.ValidateOrThrow(input);
        if (input.Kind == PromotionKind.Percent && input.Value > 100)
            throw new PiranhaCommerceValidationException(nameof(PromotionInput.Value), "A percentage cannot be more than 100.");
        if (input.StartsUtc is { } s && input.EndsUtc is { } e && e <= s)
            throw new PiranhaCommerceValidationException(nameof(PromotionInput.EndsUtc), "The end must be after the start.");
    }

    private static void Apply(Promotion p, PromotionInput i)
    {
        p.Name = string.IsNullOrWhiteSpace(i.Name) ? null : i.Name.Trim();
        p.Kind = i.Kind; p.Value = i.Value; p.MinSubtotal = i.MinSubtotal;
        p.StartsUtc = i.StartsUtc; p.EndsUtc = i.EndsUtc; p.MaxRedemptions = i.MaxRedemptions; p.Active = i.Active; p.CustomerGroupId = i.CustomerGroupId;
    }

    private async Task<IReadOnlyList<PromotionDto>> ToDtosAsync(IReadOnlyList<Promotion> rows, CancellationToken ct)
    {
        var groupIds = rows.Where(p => p.CustomerGroupId != null).Select(p => p.CustomerGroupId!.Value).Distinct().ToList();
        var names = groupIds.Count == 0 ? [] : await db.Set<PiranhaCommerce.Customers.CustomerGroup>().AsNoTracking().Where(g => groupIds.Contains(g.Id)).ToDictionaryAsync(g => g.Id, g => g.Name, ct);
        return rows.Select(p => new PromotionDto(p.Id, p.Code, p.Name, p.Kind, p.Value, p.MinSubtotal, p.StartsUtc, p.EndsUtc, p.MaxRedemptions, p.Redeemed, p.Active,
            p.CustomerGroupId, p.CustomerGroupId is { } g ? names.GetValueOrDefault(g) : null)).ToList();
    }
}
