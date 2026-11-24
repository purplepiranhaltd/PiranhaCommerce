using Microsoft.EntityFrameworkCore;
using PiranhaCommerce.Catalog;
using PiranhaCommerce.Customers;
using PiranhaCommerce.Data;

namespace PiranhaCommerce.Ecommerce;

/// <summary>One basket line as the discount engine sees it: the unit price the buyer already has (after sales, group prices and quantity breaks).</summary>
internal sealed record EngineLine(Guid ProductId, int Quantity, decimal UnitPrice, decimal LineTotal);

internal sealed record EngineRule(DiscountRule Rule, HashSet<Guid> Products, HashSet<Guid> Categories);

internal sealed record AppliedRule(Guid RuleId, string Name, string? Code, decimal Amount, bool FreeShipping);

/// <param name="LineDiscounts">Each line's discount, in the order the lines were given.</param>
/// <param name="CodeProblem">Why the code the buyer entered gave nothing (when it was one of these discounts' codes), else null.</param>
/// <param name="CodeIsDiscount">True when the code the buyer entered is one of these discounts' codes (so it is not a promo code).</param>
internal sealed record DiscountPlan(IReadOnlyList<decimal> LineDiscounts, bool FreeShipping, IReadOnlyList<AppliedRule> Applied, string? CodeProblem, bool CodeIsDiscount)
{
    public decimal Total => LineDiscounts.Sum();
    public static DiscountPlan None(int lines) => new(new decimal[lines], false, [], null, false);
}

/// <summary>
/// Works out which discounts a basket gets. The pure part (<see cref="Evaluate"/>) takes the rules that are allowed for this buyer and the lines and decides
/// the amounts; <see cref="PlanAsync"/> loads the rules, the buyer's groups and their earlier use. The basket, the quote and checkout all call this, so they agree.
/// <para>
/// Combinable discounts are applied in priority order (then name), each on what the earlier ones left. An exclusive discount is worked out on its own against the
/// full prices; if the best exclusive one is worth more than all the combinable ones together it is used alone, otherwise the combinable ones are.
/// </para>
/// </summary>
internal static class DiscountEngine
{
    private static decimal Round(decimal v) => Math.Round(v, 2, MidpointRounding.AwayFromZero);

    public static async Task<DiscountPlan> PlanAsync(PiranhaCommerceDbContext db, Guid? customerId, string? code, IReadOnlyList<EngineLine> lines, DateTimeOffset now, CancellationToken ct)
    {
        if (lines.Count == 0) return DiscountPlan.None(0);
        var normalized = string.IsNullOrWhiteSpace(code) ? null : PromotionRules.Normalize(code);

        // Only rules that are on can apply (an automatic one, or the one whose code was entered); dates are checked here too.
        var candidates = await db.Set<DiscountRule>().AsNoTracking()
            .Where(r => r.Active && (r.Code == null || r.Code == normalized)).ToListAsync(ct);
        var entered = normalized is null ? null : await db.Set<DiscountRule>().AsNoTracking().FirstOrDefaultAsync(r => r.Code == normalized, ct);
        if (candidates.Count == 0 && entered is null) return DiscountPlan.None(lines.Count);

        var ids = candidates.Select(r => r.Id).ToList();
        var productLinks = await db.Set<DiscountRuleProduct>().AsNoTracking().Where(t => ids.Contains(t.RuleId)).ToListAsync(ct);
        var categoryLinks = await db.Set<DiscountRuleCategory>().AsNoTracking().Where(t => ids.Contains(t.RuleId)).ToListAsync(ct);

        // A rule's category covers its sub-categories, so each product carries its categories and all their ancestors.
        var productIds = lines.Select(l => l.ProductId).Distinct().ToList();
        var coverage = new Dictionary<Guid, HashSet<Guid>>();
        if (categoryLinks.Count > 0)
        {
            var parents = await db.Set<Category>().AsNoTracking().ToDictionaryAsync(c => c.Id, c => c.ParentId, ct);
            var links = await db.Set<ProductCategory>().AsNoTracking().Where(pc => productIds.Contains(pc.ProductId)).ToListAsync(ct);
            foreach (var byProduct in links.GroupBy(x => x.ProductId))
            {
                var all = new HashSet<Guid>();
                foreach (var link in byProduct)
                {
                    Guid? c = link.CategoryId;
                    while (c is { } id && all.Add(id)) c = parents.GetValueOrDefault(id);
                }
                coverage[byProduct.Key] = all;
            }
        }

        var groups = customerId is { } cust ? (await db.Set<CustomerGroupMember>().AsNoTracking().Where(m => m.CustomerId == cust).Select(m => m.GroupId).ToListAsync(ct)).ToHashSet() : [];
        var limited = candidates.Where(r => r.MaxPerCustomer != null).Select(r => r.Id).ToList();
        var uses = new Dictionary<Guid, int>();
        if (limited.Count > 0 && customerId is { } who)
            uses = (await (from d in db.Set<OrderDiscount>().AsNoTracking()
                           join o in db.Set<Order>().AsNoTracking() on d.OrderId equals o.Id
                           where o.CustomerId == who && o.Stage != OrderStage.Cancelled && d.RuleId != null && limited.Contains(d.RuleId.Value)
                           select new { RuleId = d.RuleId!.Value, d.OrderId }).Distinct().ToListAsync(ct))
                .GroupBy(x => x.RuleId).ToDictionary(g => g.Key, g => g.Count());

        bool Allowed(DiscountRule r) =>
            r.Active && (r.StartsUtc is null || r.StartsUtc <= now) && (r.EndsUtc is null || r.EndsUtc > now)
            && (r.MaxRedemptions is null || r.Redeemed < r.MaxRedemptions)
            && (r.CustomerGroupId is null || groups.Contains(r.CustomerGroupId.Value))
            && (r.MaxPerCustomer is null || (customerId is not null && uses.GetValueOrDefault(r.Id) < r.MaxPerCustomer));

        EngineRule Load(DiscountRule r) => new(r,
            productLinks.Where(t => t.RuleId == r.Id).Select(t => t.ProductId).ToHashSet(),
            categoryLinks.Where(t => t.RuleId == r.Id).Select(t => t.CategoryId).ToHashSet());

        var eligible = candidates.Where(Allowed).Select(Load).ToList();
        var plan = Evaluate(eligible, lines, coverage);

        if (entered is null) return plan with { CodeIsDiscount = false };
        if (plan.Applied.Any(a => a.RuleId == entered.Id)) return plan with { CodeIsDiscount = true };

        // The code is one of ours but gave nothing: say why.
        string? problem;
        if (!Allowed(entered)) problem = entered.Active && entered.MaxRedemptions is { } max && entered.Redeemed >= max ? "That code has been used up." : PromotionRules.NotValid;
        else
        {
            var e = Load(entered);
            var qualifying = lines.Where(l => Qualifies(e, l, coverage)).ToList();
            problem = qualifying.Count == 0 ? "That code does not apply to anything in your basket."
                : entered.MinSubtotal is { } min && qualifying.Sum(l => l.LineTotal) < min ? $"Spend at least {min:0.##} on the goods it covers to use that code."
                : "A better offer is already applied, so that code adds nothing.";
        }
        return plan with { CodeProblem = problem, CodeIsDiscount = true };
    }

    private static bool Qualifies(EngineRule r, EngineLine l, IReadOnlyDictionary<Guid, HashSet<Guid>> coverage) =>
        r.Rule.AllProducts || (r.Rule.Kind == DiscountKind.FreeShipping && r.Products.Count == 0 && r.Categories.Count == 0) || r.Products.Contains(l.ProductId) || (coverage.TryGetValue(l.ProductId, out var cats) && cats.Overlaps(r.Categories));

    /// <summary>The discounts these (already allowed) rules give these lines.</summary>
    public static DiscountPlan Evaluate(IReadOnlyList<EngineRule> rules, IReadOnlyList<EngineLine> lines, IReadOnlyDictionary<Guid, HashSet<Guid>> coverage)
    {
        var pristine = lines.Select(l => l.LineTotal).ToArray();

        // Combinable ones, in order, each on what is left.
        var remaining = (decimal[])pristine.Clone();
        var combined = new decimal[lines.Count];
        var applied = new List<AppliedRule>();
        var free = false;
        foreach (var r in rules.Where(r => r.Rule.Stacking == DiscountStacking.Combinable).OrderBy(r => r.Rule.Priority).ThenBy(r => r.Rule.Name, StringComparer.OrdinalIgnoreCase).ThenBy(r => r.Rule.Id))
        {
            var result = Compute(r, lines, pristine, remaining, coverage);
            if (result is null) continue;
            for (var i = 0; i < lines.Count; i++) { remaining[i] -= result.Value.Shares[i]; combined[i] += result.Value.Shares[i]; }
            free |= result.Value.Free;
            applied.Add(new AppliedRule(r.Rule.Id, r.Rule.Name, r.Rule.Code, result.Value.Shares.Sum(), result.Value.Free));
        }

        // The best exclusive one, against the full prices.
        (AppliedRule Rule, decimal[] Shares)? best = null;
        foreach (var r in rules.Where(r => r.Rule.Stacking == DiscountStacking.Exclusive).OrderBy(r => r.Rule.Priority).ThenBy(r => r.Rule.Name, StringComparer.OrdinalIgnoreCase).ThenBy(r => r.Rule.Id))
        {
            var result = Compute(r, lines, pristine, pristine, coverage);
            if (result is null) continue;
            var total = result.Value.Shares.Sum();
            if (best is null || total > best.Value.Shares.Sum() || (total == best.Value.Shares.Sum() && result.Value.Free && !best.Value.Rule.FreeShipping))
                best = (new AppliedRule(r.Rule.Id, r.Rule.Name, r.Rule.Code, total, result.Value.Free), result.Value.Shares);
        }

        var combinedTotal = combined.Sum();
        if (best is { } b && (b.Shares.Sum(), b.Rule.FreeShipping ? 1 : 0).CompareTo((combinedTotal, free ? 1 : 0)) > 0)
            return new DiscountPlan(b.Shares, b.Rule.FreeShipping, [b.Rule], null, false);
        return new DiscountPlan(combined, free, applied, null, false);
    }

    /// <summary>What one rule takes off, given what each line costs now; null when it gives nothing.</summary>
    private static (decimal[] Shares, bool Free)? Compute(EngineRule r, IReadOnlyList<EngineLine> lines, decimal[] pristine, decimal[] remaining, IReadOnlyDictionary<Guid, HashSet<Guid>> coverage)
    {
        var rule = r.Rule;
        var q = Enumerable.Range(0, lines.Count).Where(i => Qualifies(r, lines[i], coverage)).ToList();
        if (q.Count == 0) return null;
        if (rule.MinSubtotal is { } min && q.Sum(i => pristine[i]) < min) return null;

        var shares = new decimal[lines.Count];
        switch (rule.Kind)
        {
            case DiscountKind.FreeShipping:
                return (shares, true);

            case DiscountKind.PercentOff:
                foreach (var i in q) shares[i] = Round(remaining[i] * rule.Value / 100m);
                break;

            case DiscountKind.AmountOff:
            {
                var totals = q.Select(i => remaining[i]).ToList();
                var split = PromotionRules.Allocate(totals, Math.Min(rule.Value, totals.Sum()));
                for (var k = 0; k < q.Count; k++) shares[q[k]] = split[k];
                break;
            }

            case DiscountKind.BuyXGetY:
            {
                // Every group of (buy + get) units has `get` of the cheapest units discounted.
                var buy = rule.BuyQuantity ?? 1;
                var get = rule.GetQuantity ?? 1;
                var units = q.SelectMany(i => Enumerable.Repeat((Line: i, Price: lines[i].Quantity > 0 ? Round(remaining[i] / lines[i].Quantity) : 0m), lines[i].Quantity))
                    .OrderBy(u => u.Price).ThenBy(u => u.Line).ToList();
                var discounted = units.Count / (buy + get) * get;
                foreach (var u in units.Take(discounted)) shares[u.Line] += Round(u.Price * rule.Value / 100m);
                foreach (var i in q) shares[i] = Math.Min(shares[i], remaining[i]);
                break;
            }
        }
        return shares.Sum() > 0 ? (shares, false) : null;
    }
}
