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

public enum DiscountKind
{
    /// <summary>A percentage off the qualifying goods.</summary>
    PercentOff = 0,
    /// <summary>A fixed amount off the qualifying goods, never more than they cost.</summary>
    AmountOff = 1,
    /// <summary>Buy <see cref="DiscountRule.BuyQuantity"/>, get <see cref="DiscountRule.GetQuantity"/> of the qualifying goods at <see cref="DiscountRule.Value"/> percent off (100 is free). The cheapest units are the ones discounted.</summary>
    BuyXGetY = 2,
    /// <summary>Delivery is free.</summary>
    FreeShipping = 3,
}

public enum DiscountStacking
{
    /// <summary>Applies together with the other combinable discounts, one after another in priority order, each on what is left.</summary>
    Combinable = 0,
    /// <summary>Applies alone: if it is worth more than all the combinable discounts together it wins and they are dropped; otherwise it is.</summary>
    Exclusive = 1,
}

/// <summary>
/// A discount the shop sets up once. With no <see cref="Code"/> it applies by itself to any basket that qualifies; with a code the shopper has to enter it.
/// What it applies to is the goods named (products, categories with their sub-categories, or everything). It works on the price the buyer already has
/// (sale, group price, quantity break), takes money off before shipping and tax are worked out, and an order keeps its own copy of what was applied.
/// </summary>
[Audited]
public class DiscountRule : AuditableEntity
{
    public required string Name { get; set; }
    /// <summary>Upper-case; empty for an automatic discount. Fixed once set... changeable, but unique among codes and promo codes.</summary>
    public string? Code { get; set; }
    public DiscountKind Kind { get; set; }
    /// <summary>A percentage (0.01 to 100) for <see cref="DiscountKind.PercentOff"/> and <see cref="DiscountKind.BuyXGetY"/>; an amount for <see cref="DiscountKind.AmountOff"/>; unused for free shipping.</summary>
    public decimal Value { get; set; }
    public int? BuyQuantity { get; set; }
    public int? GetQuantity { get; set; }
    /// <summary>Covers every product. Otherwise the named products and categories.</summary>
    public bool AllProducts { get; set; }
    /// <summary>The qualifying goods must cost at least this (before any discount).</summary>
    public decimal? MinSubtotal { get; set; }
    public DateTimeOffset? StartsUtc { get; set; }
    public DateTimeOffset? EndsUtc { get; set; }
    /// <summary>Lower numbers are applied first.</summary>
    public int Priority { get; set; }
    public DiscountStacking Stacking { get; set; }
    public int? MaxRedemptions { get; set; }
    public int Redeemed { get; set; }
    /// <summary>Most orders one signed-in customer may use it on. A guest cannot use a discount that has a limit per customer, because there is no one to count against.</summary>
    public int? MaxPerCustomer { get; set; }
    /// <summary>When set, only signed-in members of this group get it.</summary>
    public Guid? CustomerGroupId { get; set; }
    public bool Active { get; set; } = true;
}

[Audited]
public class DiscountRuleProduct
{
    public Guid RuleId { get; set; }
    public Guid ProductId { get; set; }
}

[Audited]
public class DiscountRuleCategory
{
    public Guid RuleId { get; set; }
    public Guid CategoryId { get; set; }
}

/// <summary>What an order was given, copied when it was placed. Kept when the rule is later changed or deleted.</summary>
public class OrderDiscount : Entity
{
    public Guid OrderId { get; set; }
    public Guid? RuleId { get; set; }
    public required string Name { get; set; }
    public string? Code { get; set; }
    public decimal Amount { get; set; }
    public bool FreeShipping { get; set; }
}

public sealed record AppliedDiscountDto(string Name, string? Code, decimal Amount, bool FreeShipping);

public sealed class DiscountRuleInput : IValidatableObject
{
    [Required, StringLength(100, MinimumLength = 1)] public string Name { get; set; } = "";
    /// <summary>Empty for an automatic discount.</summary>
    [RegularExpression("^[A-Za-z0-9_-]{3,40}$", ErrorMessage = "Use 3 to 40 letters, digits, - or _.")] public string? Code { get; set; }
    public DiscountKind Kind { get; set; }
    [Range(typeof(decimal), "0", "1000000")] public decimal Value { get; set; }
    [Range(1, 100)] public int? BuyQuantity { get; set; }
    [Range(1, 100)] public int? GetQuantity { get; set; }
    public bool AllProducts { get; set; }
    [MaxLength(500)] public List<Guid> ProductIds { get; set; } = [];
    [MaxLength(100)] public List<Guid> CategoryIds { get; set; } = [];
    [Range(typeof(decimal), "0", "1000000000")] public decimal? MinSubtotal { get; set; }
    public DateTimeOffset? StartsUtc { get; set; }
    public DateTimeOffset? EndsUtc { get; set; }
    [Range(0, 10_000)] public int Priority { get; set; }
    public DiscountStacking Stacking { get; set; }
    [Range(1, int.MaxValue)] public int? MaxRedemptions { get; set; }
    [Range(1, 1_000_000)] public int? MaxPerCustomer { get; set; }
    public Guid? CustomerGroupId { get; set; }
    public bool Active { get; set; } = true;

    public IEnumerable<ValidationResult> Validate(ValidationContext context)
    {
        switch (Kind)
        {
            case DiscountKind.PercentOff:
                if (Value is <= 0 or > 100 || decimal.Round(Value, 2) != Value) yield return new("Give a percentage above 0 and up to 100, with at most 2 decimal places.", [nameof(Value)]);
                break;
            case DiscountKind.AmountOff:
                if (Value <= 0 || decimal.Round(Value, 2) != Value) yield return new("Give an amount above 0, with at most 2 decimal places.", [nameof(Value)]);
                break;
            case DiscountKind.BuyXGetY:
                if (Value is <= 0 or > 100 || decimal.Round(Value, 2) != Value) yield return new("Give the percentage off the free or cheaper items: above 0 and up to 100 (100 is free).", [nameof(Value)]);
                if (BuyQuantity is null || GetQuantity is null) yield return new("Say how many to buy and how many to get.", [nameof(BuyQuantity)]);
                break;
            case DiscountKind.FreeShipping:
                break;
            default:
                yield return new("Choose what the discount does.", [nameof(Kind)]);
                break;
        }
        if (Kind != DiscountKind.BuyXGetY && (BuyQuantity is not null || GetQuantity is not null)) yield return new("How many to buy and get only applies to buy-X-get-Y.", [nameof(BuyQuantity)]);
        if (!AllProducts && ProductIds.Count == 0 && CategoryIds.Count == 0 && Kind != DiscountKind.FreeShipping)
            yield return new("Choose what it applies to: everything, or some products or categories.", [nameof(AllProducts)]);
        if (AllProducts && (ProductIds.Count > 0 || CategoryIds.Count > 0)) yield return new("A discount on everything cannot also name products or categories.", [nameof(AllProducts)]);
        if (StartsUtc is { } s && EndsUtc is { } e && e <= s) yield return new("The end must be after the start.", [nameof(EndsUtc)]);
        if (MinSubtotal is { } m && decimal.Round(m, 2) != m) yield return new("The minimum can have at most 2 decimal places.", [nameof(MinSubtotal)]);
    }
}

public sealed record DiscountTargetDto(Guid Id, string Name, string? Sku = null);

public sealed record DiscountRuleDto(
    Guid Id, string Name, string? Code, DiscountKind Kind, decimal Value, int? BuyQuantity, int? GetQuantity, bool AllProducts,
    IReadOnlyList<DiscountTargetDto> Products, IReadOnlyList<DiscountTargetDto> Categories, decimal? MinSubtotal, DateTimeOffset? StartsUtc, DateTimeOffset? EndsUtc,
    int Priority, DiscountStacking Stacking, int? MaxRedemptions, int Redeemed, int? MaxPerCustomer, Guid? CustomerGroupId, string? CustomerGroupName, bool Active);

public interface IDiscountRuleService
{
    /// <summary>Automatic ones and codes together, by priority then name.</summary>
    Task<IReadOnlyList<DiscountRuleDto>> ListAsync(CancellationToken ct = default);
    Task<DiscountRuleDto?> GetAsync(Guid id, CancellationToken ct = default);
    /// <summary>Validation error for a bad rule, a code already used (as a discount or a promo code), or a product, category or group that does not exist.</summary>
    [RequiresPermission(Permissions.PricesManage)]
    Task<DiscountRuleDto> CreateAsync(DiscountRuleInput input, CancellationToken ct = default);
    /// <summary>Replaces everything (the targets too). Null when not found. The uses so far are kept.</summary>
    [RequiresPermission(Permissions.PricesManage)]
    Task<DiscountRuleDto?> UpdateAsync(Guid id, DiscountRuleInput input, CancellationToken ct = default);
    /// <summary>Orders keep their own copy of what they were given, so deleting is always safe.</summary>
    [RequiresPermission(Permissions.PricesManage)]
    Task<bool> DeleteAsync(Guid id, CancellationToken ct = default);
}

internal sealed class DiscountRuleService(PiranhaCommerceDbContext db) : IDiscountRuleService
{
    public async Task<IReadOnlyList<DiscountRuleDto>> ListAsync(CancellationToken ct = default)
    {
        var rules = await db.Set<DiscountRule>().AsNoTracking().OrderBy(r => r.Priority).ThenBy(r => r.Name).ThenBy(r => r.Id).ToListAsync(ct);
        return await ToDtosAsync(rules, ct);
    }

    public async Task<DiscountRuleDto?> GetAsync(Guid id, CancellationToken ct = default)
    {
        var rule = await db.Set<DiscountRule>().AsNoTracking().FirstOrDefaultAsync(r => r.Id == id, ct);
        return rule is null ? null : (await ToDtosAsync([rule], ct))[0];
    }

    public async Task<DiscountRuleDto> CreateAsync(DiscountRuleInput input, CancellationToken ct = default)
    {
        await CheckAsync(input, null, ct);
        var rule = new DiscountRule { Name = input.Name.Trim() };
        Apply(rule, input);
        await SaveAsync(rule, input, isNew: true, ct);
        return (await GetAsync(rule.Id, ct))!;
    }

    public async Task<DiscountRuleDto?> UpdateAsync(Guid id, DiscountRuleInput input, CancellationToken ct = default)
    {
        if (!await db.Set<DiscountRule>().AnyAsync(r => r.Id == id, ct)) return null;
        await CheckAsync(input, id, ct);
        var strategy = db.Database.CreateExecutionStrategy();
        var found = await strategy.ExecuteInTransactionAsync(
            async token =>
            {
                db.ChangeTracker.Clear();
                var rule = await db.Set<DiscountRule>().FirstOrDefaultAsync(r => r.Id == id, token);
                if (rule is null) return false;
                rule.Name = input.Name.Trim();
                Apply(rule, input);
                await db.DeleteTrackedAsync<DiscountRuleProduct>(t => t.RuleId == id, token);
                await db.DeleteTrackedAsync<DiscountRuleCategory>(t => t.RuleId == id, token);
                AddTargets(id, input);
                await db.SaveChangesAsync(token);
                return true;
            },
            async token => true,
            ct);
        return found ? await GetAsync(id, ct) : null;
    }

    public async Task<bool> DeleteAsync(Guid id, CancellationToken ct = default) => await db.DeleteTrackedAsync<DiscountRule>(r => r.Id == id, ct) > 0;

    private static void Apply(DiscountRule r, DiscountRuleInput i)
    {
        r.Name = i.Name.Trim();
        r.Code = string.IsNullOrWhiteSpace(i.Code) ? null : PromotionRules.Normalize(i.Code);
        r.Kind = i.Kind;
        r.Value = i.Kind == DiscountKind.FreeShipping ? 0m : i.Value;
        r.BuyQuantity = i.Kind == DiscountKind.BuyXGetY ? i.BuyQuantity : null;
        r.GetQuantity = i.Kind == DiscountKind.BuyXGetY ? i.GetQuantity : null;
        r.AllProducts = i.AllProducts;
        r.MinSubtotal = i.MinSubtotal;
        r.StartsUtc = i.StartsUtc;
        r.EndsUtc = i.EndsUtc;
        r.Priority = i.Priority;
        r.Stacking = i.Stacking;
        r.MaxRedemptions = i.MaxRedemptions;
        r.MaxPerCustomer = i.MaxPerCustomer;
        r.CustomerGroupId = i.CustomerGroupId;
        r.Active = i.Active;
    }

    private void AddTargets(Guid id, DiscountRuleInput input)
    {
        foreach (var p in input.ProductIds.Distinct()) db.Add(new DiscountRuleProduct { RuleId = id, ProductId = p });
        foreach (var c in input.CategoryIds.Distinct()) db.Add(new DiscountRuleCategory { RuleId = id, CategoryId = c });
    }

    private async Task SaveAsync(DiscountRule rule, DiscountRuleInput input, bool isNew, CancellationToken ct)
    {
        if (isNew) db.Add(rule);
        AddTargets(rule.Id, input);
        try { await db.SaveChangesAsync(ct); }
        catch (DbUpdateException)
        {
            db.ChangeTracker.Clear();
            throw new PiranhaCommerceValidationException(nameof(DiscountRuleInput.Code), "That code is already in use.");
        }
    }

    private async Task CheckAsync(DiscountRuleInput input, Guid? existingId, CancellationToken ct)
    {
        ModelValidator.ValidateOrThrow(input);
        if (!string.IsNullOrWhiteSpace(input.Code))
        {
            var code = PromotionRules.Normalize(input.Code);
            if (await db.Set<Promotion>().AnyAsync(p => p.Code == code, ct) || await db.Set<DiscountRule>().AnyAsync(r => r.Code == code && r.Id != existingId, ct))
                throw new PiranhaCommerceValidationException(nameof(DiscountRuleInput.Code), "That code is already in use.");
        }
        var products = input.ProductIds.Distinct().ToList();
        if (products.Count > 0 && await db.Set<Product>().CountAsync(p => products.Contains(p.Id), ct) != products.Count)
            throw new PiranhaCommerceValidationException(nameof(DiscountRuleInput.ProductIds), "One of the products does not exist.");
        var categories = input.CategoryIds.Distinct().ToList();
        if (categories.Count > 0 && await db.Set<Category>().CountAsync(c => categories.Contains(c.Id), ct) != categories.Count)
            throw new PiranhaCommerceValidationException(nameof(DiscountRuleInput.CategoryIds), "One of the categories does not exist.");
        if (input.CustomerGroupId is { } g && !await db.Set<CustomerGroup>().AnyAsync(x => x.Id == g, ct))
            throw new PiranhaCommerceValidationException(nameof(DiscountRuleInput.CustomerGroupId), "That customer group does not exist.");
    }

    private async Task<IReadOnlyList<DiscountRuleDto>> ToDtosAsync(IReadOnlyList<DiscountRule> rules, CancellationToken ct)
    {
        var ids = rules.Select(r => r.Id).ToList();
        var productLinks = await db.Set<DiscountRuleProduct>().AsNoTracking().Where(t => ids.Contains(t.RuleId)).ToListAsync(ct);
        var categoryLinks = await db.Set<DiscountRuleCategory>().AsNoTracking().Where(t => ids.Contains(t.RuleId)).ToListAsync(ct);
        var productIds = productLinks.Select(t => t.ProductId).Distinct().ToList();
        var products = await db.Set<Product>().AsNoTracking().Where(p => productIds.Contains(p.Id)).Select(p => new { p.Id, p.Name, p.Sku }).ToDictionaryAsync(p => p.Id, ct);
        var categoryIds = categoryLinks.Select(t => t.CategoryId).Distinct().ToList();
        var categories = await db.Set<Category>().AsNoTracking().Where(c => categoryIds.Contains(c.Id)).Select(c => new { c.Id, c.Name }).ToDictionaryAsync(c => c.Id, ct);
        var groupIds = rules.Where(r => r.CustomerGroupId != null).Select(r => r.CustomerGroupId!.Value).Distinct().ToList();
        var groups = await db.Set<CustomerGroup>().AsNoTracking().Where(g => groupIds.Contains(g.Id)).ToDictionaryAsync(g => g.Id, g => g.Name, ct);
        return rules.Select(r => new DiscountRuleDto(r.Id, r.Name, r.Code, r.Kind, r.Value, r.BuyQuantity, r.GetQuantity, r.AllProducts,
            productLinks.Where(t => t.RuleId == r.Id).Select(t => products.GetValueOrDefault(t.ProductId)).Where(p => p is not null).Select(p => new DiscountTargetDto(p!.Id, p.Name, p.Sku)).OrderBy(p => p.Name).ToList(),
            categoryLinks.Where(t => t.RuleId == r.Id).Select(t => categories.GetValueOrDefault(t.CategoryId)).Where(c => c is not null).Select(c => new DiscountTargetDto(c!.Id, c.Name)).OrderBy(c => c.Name).ToList(),
            r.MinSubtotal, r.StartsUtc, r.EndsUtc, r.Priority, r.Stacking, r.MaxRedemptions, r.Redeemed, r.MaxPerCustomer, r.CustomerGroupId,
            r.CustomerGroupId is { } g ? groups.GetValueOrDefault(g) : null, r.Active)).ToList();
    }
}

internal sealed class DiscountRuleEndpoints : IApiEndpointContributor
{
    public void MapEndpoints(IEndpointRouteBuilder api)
    {
        var g = api.MapGroup("/discounts").WithTags("Discounts").RequirePermission(Permissions.CatalogView);
        g.MapGet("/", async (IDiscountRuleService svc, CancellationToken ct) => Results.Ok(await svc.ListAsync(ct)));
        g.MapGet("/{id:guid}", async (Guid id, IDiscountRuleService svc, CancellationToken ct) =>
            await svc.GetAsync(id, ct) is { } r ? Results.Ok(r) : Results.NotFound());
        g.MapPost("/", async (DiscountRuleInput input, IDiscountRuleService svc, CancellationToken ct) =>
        {
            var created = await svc.CreateAsync(input, ct);
            return Results.Created($"/api/v1/discounts/{created.Id}", created);
        }).RequirePermission(Permissions.PricesManage);
        g.MapPut("/{id:guid}", async (Guid id, DiscountRuleInput input, IDiscountRuleService svc, CancellationToken ct) =>
            await svc.UpdateAsync(id, input, ct) is { } r ? Results.Ok(r) : Results.NotFound()).RequirePermission(Permissions.PricesManage);
        g.MapDelete("/{id:guid}", async (Guid id, IDiscountRuleService svc, CancellationToken ct) =>
            await svc.DeleteAsync(id, ct) ? Results.NoContent() : Results.NotFound()).RequirePermission(Permissions.PricesManage);
    }
}
