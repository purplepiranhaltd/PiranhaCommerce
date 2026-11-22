using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using PiranhaCommerce.Api;
using PiranhaCommerce.Catalog;
using PiranhaCommerce.Core.Validation;
using PiranhaCommerce.Customers;
using PiranhaCommerce.Data;

namespace PiranhaCommerce.Ecommerce;

/// <summary>A fixed price for one product for the members of one customer group. It beats the group's percentage for that product.</summary>
public class GroupPrice
{
    public Guid GroupId { get; set; }
    public Guid ProductId { get; set; }
    public decimal Price { get; set; }
}

/// <summary>A percentage off, for the members of one group, on every product in a category (and its sub-categories). Overrides the group's own percentage there.</summary>
public class GroupCategoryDiscount
{
    public Guid GroupId { get; set; }
    public Guid CategoryId { get; set; }
    public decimal Percent { get; set; }
}

public sealed class GroupCategoryDiscountInput
{
    /// <summary>0 to 100, up to 2 decimal places. 0 means no discount in this category even if the group has a percentage overall.</summary>
    [Range(typeof(decimal), "0", "100")] public decimal Percent { get; set; }
}

public sealed record GroupCategoryDiscountDto(Guid CategoryId, string CategoryName, Guid? ParentId, decimal Percent);

public sealed class GroupPriceInput
{
    [Range(typeof(decimal), "0", "1000000000")] public decimal Price { get; set; }
}

/// <summary>One row of a product's group prices: the group, its percentage off, a fixed price if set, and what its members actually pay.</summary>
/// <param name="DiscountPercent">The group's own percentage.</param>
/// <param name="CategoryPercent">When a category discount set for the group covers this product, its percentage, which replaces <paramref name="DiscountPercent"/> for this product.</param>
public sealed record ProductGroupPriceDto(Guid GroupId, string GroupName, decimal DiscountPercent, decimal? FixedPrice, decimal GroupPrice, decimal? CategoryPercent = null);

/// <summary>What one shopper pays for a product: <see cref="Price"/> is the regular price unless one of their groups gives them less.</summary>
/// <param name="SaleEndsUtc">When the price is a sale price, when that sale ends.</param>
/// <param name="TaxCountry">The destination the tax-inclusive figures are for (null when none was given and the shop has no default country).</param>
/// <param name="TaxRate">The rate that applies to this product there (0 when it is not taxed). The tax-inclusive prices are the price plus that rate, rounded to the penny. A basket or quote adds tax per line, so a total can differ from unit price times quantity by a penny: the quote is what is charged.</param>
public sealed record ShopperPriceDto(
    Guid ProductId, decimal RegularPrice, decimal Price, bool Discounted, DateTimeOffset? SaleEndsUtc = null,
    string? TaxCountry = null, decimal? TaxRate = null, decimal? PriceIncludingTax = null, decimal? RegularPriceIncludingTax = null);

public interface IGroupPricingService
{
    /// <summary>Every customer group with what it pays for this product. Null when the product does not exist.</summary>
    Task<IReadOnlyList<ProductGroupPriceDto>?> ListForProductAsync(Guid productId, CancellationToken ct = default);

    /// <summary>Sets (or replaces) the fixed price for a group. Null when the product or group does not exist.</summary>
    Task<ProductGroupPriceDto?> SetAsync(Guid productId, Guid groupId, GroupPriceInput input, CancellationToken ct = default);

    /// <summary>Removes the fixed price (the group's percentage applies again). False when there was none.</summary>
    Task<bool> RemoveAsync(Guid productId, Guid groupId, CancellationToken ct = default);

    /// <summary>The category discounts set for a group, by category name. Null when the group does not exist.</summary>
    Task<IReadOnlyList<GroupCategoryDiscountDto>?> ListCategoryDiscountsAsync(Guid groupId, CancellationToken ct = default);

    /// <summary>Sets (or replaces) the group's percentage for a category and everything under it. Null when the group or category does not exist.</summary>
    Task<GroupCategoryDiscountDto?> SetCategoryDiscountAsync(Guid groupId, Guid categoryId, GroupCategoryDiscountInput input, CancellationToken ct = default);

    /// <summary>Removes it (the group's own percentage applies there again). False when there was none.</summary>
    Task<bool> RemoveCategoryDiscountAsync(Guid groupId, Guid categoryId, CancellationToken ct = default);

    /// <summary>The prices this customer pays (a null customer pays the regular price). Products that do not exist are left out. At most 200 at a time.</summary>
    /// <param name="countryCode">The destination to show tax-inclusive prices for; the shop's default country when empty. A validation error when it is not two letters.</param>
    Task<IReadOnlyList<ShopperPriceDto>> PricesAsync(Guid? customerId, IReadOnlyCollection<Guid> productIds, string? countryCode = null, CancellationToken ct = default);
}

/// <summary>
/// The one place that decides what a customer pays. Each group they belong to gives a price, from the most specific thing set: the fixed price for
/// that product; otherwise the group's percentage for a category the product is in (a category covers its sub-categories, the nearest one set wins,
/// and if the product is in several categories the largest percentage wins); otherwise the group's own percentage. The percentage comes off the
/// regular price and is rounded to 2 places. They pay the lowest of the groups' prices and the regular price, so a group can never make something
/// dearer. Baskets, quotes and checkout all use this, so they always agree.
/// </summary>
internal static class GroupPricing
{
    public static decimal Percentage(decimal price, decimal percent) => decimal.Round(price * (100m - percent) / 100m, 2, MidpointRounding.AwayFromZero);

    /// <summary>
    /// The percentage a group's category discounts give a product in these categories: for each category the nearest one (itself, then its parent...)
    /// that has a discount, and the largest across the categories. Null when none of them has one.
    /// </summary>
    public static decimal? CategoryPercent(IEnumerable<Guid> productCategories, IReadOnlyDictionary<Guid, decimal> groupDiscounts, IReadOnlyDictionary<Guid, Guid?> parents)
    {
        decimal? best = null;
        foreach (var start in productCategories)
        {
            Guid? current = start;
            for (var depth = 0; current is { } c && depth < 64; depth++)
            {
                if (groupDiscounts.TryGetValue(c, out var pct)) { if (best is null || pct > best) best = pct; break; }
                current = parents.GetValueOrDefault(c);
            }
        }
        return best;
    }

    /// <summary>What the customer pays: the lowest of the regular price, any running sale's price, and what their groups give. A null customer gets the sale price at most.</summary>
    public static async Task<Dictionary<Guid, decimal>> EffectiveAsync(PiranhaDbContext db, Guid? customerId, IReadOnlyCollection<(Guid ProductId, decimal Price)> items, CancellationToken ct)
    {
        var result = await ForGroupsAsync(db, customerId, items, ct);
        foreach (var (id, sale) in await SalePricing.ActiveAsync(db, items, ct))
            if (sale.Price < result[id]) result[id] = sale.Price;
        return result;
    }

    private static async Task<Dictionary<Guid, decimal>> ForGroupsAsync(PiranhaDbContext db, Guid? customerId, IReadOnlyCollection<(Guid ProductId, decimal Price)> items, CancellationToken ct)
    {
        var result = new Dictionary<Guid, decimal>();
        foreach (var (id, price) in items) result[id] = price;
        if (customerId is not { } customer || result.Count == 0) return result;

        var groups = await (from m in db.Set<CustomerGroupMember>().AsNoTracking()
                            join g in db.Set<CustomerGroup>().AsNoTracking() on m.GroupId equals g.Id
                            where m.CustomerId == customer
                            select new { g.Id, g.DiscountPercent }).ToListAsync(ct);
        if (groups.Count == 0) return result;

        var groupIds = groups.Select(g => g.Id).ToList();
        var productIds = result.Keys.ToList();
        var fixedPrices = (await db.Set<GroupPrice>().AsNoTracking().Where(p => groupIds.Contains(p.GroupId) && productIds.Contains(p.ProductId)).ToListAsync(ct))
            .ToDictionary(p => (p.GroupId, p.ProductId), p => p.Price);

        // Category discounts are only looked up when this customer's groups have any.
        var discounts = (await db.Set<GroupCategoryDiscount>().AsNoTracking().Where(d => groupIds.Contains(d.GroupId)).ToListAsync(ct))
            .GroupBy(d => d.GroupId).ToDictionary(g => g.Key, g => g.ToDictionary(d => d.CategoryId, d => d.Percent));
        var parents = new Dictionary<Guid, Guid?>();
        var inCategories = new Dictionary<Guid, List<Guid>>();
        if (discounts.Count > 0)
        {
            parents = await db.Set<Category>().AsNoTracking().ToDictionaryAsync(c => c.Id, c => c.ParentId, ct);
            foreach (var pc in await db.Set<ProductCategory>().AsNoTracking().Where(x => productIds.Contains(x.ProductId)).ToListAsync(ct))
                (inCategories.TryGetValue(pc.ProductId, out var list) ? list : inCategories[pc.ProductId] = []).Add(pc.CategoryId);
        }

        foreach (var productId in productIds)
        {
            var regular = result[productId];
            var best = regular;
            foreach (var g in groups)
            {
                decimal candidate;
                if (fixedPrices.TryGetValue((g.Id, productId), out var fixedPrice)) candidate = fixedPrice;
                else
                {
                    var pct = discounts.TryGetValue(g.Id, out var gd) ? CategoryPercent(inCategories.GetValueOrDefault(productId) ?? [], gd, parents) ?? g.DiscountPercent : g.DiscountPercent;
                    candidate = pct > 0 ? Percentage(regular, pct) : regular;
                }
                if (candidate < best) best = candidate;
            }
            result[productId] = best;
        }
        return result;
    }
}

internal sealed class GroupPricingService(PiranhaDbContext db, Microsoft.Extensions.Options.IOptions<EcommerceOptions> options) : IGroupPricingService
{
    private async Task<IReadOnlyList<ProductGroupPriceDto>> RowsAsync(Guid productId, decimal regular, CancellationToken ct)
    {
        var groups = await db.Set<CustomerGroup>().AsNoTracking().OrderBy(g => g.NormalizedName).Select(g => new { g.Id, g.Name, g.DiscountPercent }).ToListAsync(ct);
        var fixedPrices = await db.Set<GroupPrice>().AsNoTracking().Where(p => p.ProductId == productId).ToDictionaryAsync(p => p.GroupId, p => p.Price, ct);
        var discounts = (await db.Set<GroupCategoryDiscount>().AsNoTracking().ToListAsync(ct)).GroupBy(d => d.GroupId).ToDictionary(g => g.Key, g => g.ToDictionary(d => d.CategoryId, d => d.Percent));
        var parents = discounts.Count > 0 ? await db.Set<Category>().AsNoTracking().ToDictionaryAsync(c => c.Id, c => c.ParentId, ct) : [];
        var mine = discounts.Count > 0 ? await db.Set<ProductCategory>().AsNoTracking().Where(x => x.ProductId == productId).Select(x => x.CategoryId).ToListAsync(ct) : [];
        return groups.Select(g =>
        {
            decimal? fixedPrice = fixedPrices.TryGetValue(g.Id, out var f) ? f : null;
            decimal? categoryPercent = discounts.TryGetValue(g.Id, out var gd) ? GroupPricing.CategoryPercent(mine, gd, parents) : null;
            var pct = categoryPercent ?? g.DiscountPercent;
            var price = fixedPrice ?? (pct > 0 ? GroupPricing.Percentage(regular, pct) : regular);
            return new ProductGroupPriceDto(g.Id, g.Name, g.DiscountPercent, fixedPrice, price, categoryPercent);
        }).ToList();
    }

    public async Task<IReadOnlyList<ProductGroupPriceDto>?> ListForProductAsync(Guid productId, CancellationToken ct = default)
    {
        var regular = await db.Set<Product>().AsNoTracking().Where(p => p.Id == productId).Select(p => (decimal?)p.Price).FirstOrDefaultAsync(ct);
        return regular is null ? null : await RowsAsync(productId, regular.Value, ct);
    }

    public async Task<ProductGroupPriceDto?> SetAsync(Guid productId, Guid groupId, GroupPriceInput input, CancellationToken ct = default)
    {
        ModelValidator.ValidateOrThrow(input);
        if (decimal.Round(input.Price, 4) != input.Price) throw new PiranhaValidationException(nameof(GroupPriceInput.Price), "A price can have at most 4 decimal places.");
        var regular = await db.Set<Product>().AsNoTracking().Where(p => p.Id == productId).Select(p => (decimal?)p.Price).FirstOrDefaultAsync(ct);
        if (regular is null || !await db.Set<CustomerGroup>().AnyAsync(g => g.Id == groupId, ct)) return null;

        // Update first, then insert if there was nothing to update; a concurrent insert of the same pair shows as a key clash and is retried as an update.
        var updated = await db.Set<GroupPrice>().Where(p => p.GroupId == groupId && p.ProductId == productId)
            .ExecuteUpdateAsync(s => s.SetProperty(p => p.Price, input.Price), ct);
        if (updated == 0)
        {
            try
            {
                db.Add(new GroupPrice { GroupId = groupId, ProductId = productId, Price = input.Price });
                await db.SaveChangesAsync(ct);
            }
            catch (DbUpdateException)
            {
                db.ChangeTracker.Clear();
                await db.Set<GroupPrice>().Where(p => p.GroupId == groupId && p.ProductId == productId).ExecuteUpdateAsync(s => s.SetProperty(p => p.Price, input.Price), ct);
            }
        }
        return (await RowsAsync(productId, regular.Value, ct)).First(r => r.GroupId == groupId);
    }

    public async Task<IReadOnlyList<GroupCategoryDiscountDto>?> ListCategoryDiscountsAsync(Guid groupId, CancellationToken ct = default)
    {
        if (!await db.Set<CustomerGroup>().AnyAsync(g => g.Id == groupId, ct)) return null;
        return await (from d in db.Set<GroupCategoryDiscount>().AsNoTracking()
                      join c in db.Set<Category>().AsNoTracking() on d.CategoryId equals c.Id
                      where d.GroupId == groupId
                      orderby c.Name
                      select new GroupCategoryDiscountDto(c.Id, c.Name, c.ParentId, d.Percent)).ToListAsync(ct);
    }

    public async Task<GroupCategoryDiscountDto?> SetCategoryDiscountAsync(Guid groupId, Guid categoryId, GroupCategoryDiscountInput input, CancellationToken ct = default)
    {
        ModelValidator.ValidateOrThrow(input);
        if (decimal.Round(input.Percent, 2) != input.Percent) throw new PiranhaValidationException(nameof(GroupCategoryDiscountInput.Percent), "The percentage can have at most 2 decimal places.");
        var category = await db.Set<Category>().AsNoTracking().Where(c => c.Id == categoryId).Select(c => new { c.Name, c.ParentId }).FirstOrDefaultAsync(ct);
        if (category is null || !await db.Set<CustomerGroup>().AnyAsync(g => g.Id == groupId, ct)) return null;

        var updated = await db.Set<GroupCategoryDiscount>().Where(d => d.GroupId == groupId && d.CategoryId == categoryId)
            .ExecuteUpdateAsync(s => s.SetProperty(d => d.Percent, input.Percent), ct);
        if (updated == 0)
        {
            try
            {
                db.Add(new GroupCategoryDiscount { GroupId = groupId, CategoryId = categoryId, Percent = input.Percent });
                await db.SaveChangesAsync(ct);
            }
            catch (DbUpdateException)
            {
                db.ChangeTracker.Clear();
                await db.Set<GroupCategoryDiscount>().Where(d => d.GroupId == groupId && d.CategoryId == categoryId).ExecuteUpdateAsync(s => s.SetProperty(d => d.Percent, input.Percent), ct);
            }
        }
        return new GroupCategoryDiscountDto(categoryId, category.Name, category.ParentId, input.Percent);
    }

    public async Task<bool> RemoveCategoryDiscountAsync(Guid groupId, Guid categoryId, CancellationToken ct = default) =>
        await db.Set<GroupCategoryDiscount>().Where(d => d.GroupId == groupId && d.CategoryId == categoryId).ExecuteDeleteAsync(ct) > 0;

    public async Task<bool> RemoveAsync(Guid productId, Guid groupId, CancellationToken ct = default) =>
        await db.Set<GroupPrice>().Where(p => p.GroupId == groupId && p.ProductId == productId).ExecuteDeleteAsync(ct) > 0;

    public async Task<IReadOnlyList<ShopperPriceDto>> PricesAsync(Guid? customerId, IReadOnlyCollection<Guid> productIds, string? countryCode = null, CancellationToken ct = default)
    {
        if (!string.IsNullOrWhiteSpace(countryCode) && !System.Text.RegularExpressions.Regex.IsMatch(countryCode, "^[A-Za-z]{2}$"))
            throw new PiranhaValidationException("countryCode", "Use a two-letter country code such as GB.");
        var country = (string.IsNullOrWhiteSpace(countryCode) ? options.Value.DefaultCountryCode : countryCode)?.ToUpperInvariant();
        var ids = productIds.Distinct().Take(200).ToList();
        if (ids.Count == 0) return [];
        var regular = await db.Set<Product>().AsNoTracking().Where(p => ids.Contains(p.Id)).Select(p => new { p.Id, p.Price }).ToListAsync(ct);
        var effective = await GroupPricing.EffectiveAsync(db, customerId, regular.Select(r => (r.Id, r.Price)).ToList(), ct);
        var sales = await SalePricing.ActiveAsync(db, regular.Select(r => (r.Id, r.Price)).ToList(), ct);
        var rates = new Dictionary<Guid, decimal>();
        if (country is not null)
        {
            var (rules, defaultClass) = await TaxLookup.LoadAsync(db, ct);
            var classes = await TaxLookup.ClassesAsync(db, regular.Select(r => r.Id).ToList(), ct);
            foreach (var r in regular) rates[r.Id] = OrderPricing.RuleFor(rules, classes.GetValueOrDefault(r.Id) ?? defaultClass, country)?.Rate ?? 0m;
        }
        return regular.Select(r => new ShopperPriceDto(r.Id, r.Price, effective[r.Id], effective[r.Id] < r.Price,
            sales.TryGetValue(r.Id, out var sale) && sale.Price <= effective[r.Id] ? sale.EndsUtc : null,
            country, country is null ? null : rates[r.Id],
            country is null ? null : OrderPricing.IncludingTax(effective[r.Id], rates[r.Id]),
            country is null ? null : OrderPricing.IncludingTax(r.Price, rates[r.Id]))).OrderBy(r => r.ProductId).ToList();
    }
}

internal sealed class GroupPricingEndpoints : IApiEndpointContributor
{
    public void MapEndpoints(IEndpointRouteBuilder api)
    {
        var group = api.MapGroup("/products/{productId:guid}/group-prices").WithTags("Group prices");

        group.MapGet("/", async (Guid productId, IGroupPricingService svc, CancellationToken ct) =>
            await svc.ListForProductAsync(productId, ct) is { } rows ? Results.Ok(rows) : Results.NotFound());

        group.MapPut("/{groupId:guid}", async (Guid productId, Guid groupId, GroupPriceInput input, IGroupPricingService svc, CancellationToken ct) =>
                await svc.SetAsync(productId, groupId, input, ct) is { } row ? Results.Ok(row) : Results.NotFound())
            .RequireAuthorization(ApiPolicies.Write);

        group.MapDelete("/{groupId:guid}", async (Guid productId, Guid groupId, IGroupPricingService svc, CancellationToken ct) =>
                await svc.RemoveAsync(productId, groupId, ct) ? Results.NoContent() : Results.NotFound())
            .RequireAuthorization(ApiPolicies.Write);

        // A group's percentage off a whole category (and its sub-categories).
        var cats = api.MapGroup("/customer-groups/{groupId:guid}/category-discounts").WithTags("Group prices");
        cats.MapGet("/", async (Guid groupId, IGroupPricingService svc, CancellationToken ct) =>
            await svc.ListCategoryDiscountsAsync(groupId, ct) is { } rows ? Results.Ok(rows) : Results.NotFound());
        cats.MapPut("/{categoryId:guid}", async (Guid groupId, Guid categoryId, GroupCategoryDiscountInput input, IGroupPricingService svc, CancellationToken ct) =>
                await svc.SetCategoryDiscountAsync(groupId, categoryId, input, ct) is { } row ? Results.Ok(row) : Results.NotFound())
            .RequireAuthorization(ApiPolicies.Write);
        cats.MapDelete("/{categoryId:guid}", async (Guid groupId, Guid categoryId, IGroupPricingService svc, CancellationToken ct) =>
                await svc.RemoveCategoryDiscountAsync(groupId, categoryId, ct) ? Results.NoContent() : Results.NotFound())
            .RequireAuthorization(ApiPolicies.Write);

        // For a shop front that shows prices on a shopper's behalf: what this customer pays for these products.
        api.MapGet("/customers/{customerId:guid}/prices", async (Guid customerId, Guid[]? productIds, string? countryCode, IGroupPricingService svc, CancellationToken ct) =>
                Results.Ok(await svc.PricesAsync(customerId, productIds ?? [], countryCode, ct)))
            .WithTags("Group prices");
    }
}

/// <summary>The signed-in shopper's own prices. The customer is always the one in the token.</summary>
internal sealed class GroupPricingStorefrontEndpoints : IStorefrontEndpointContributor
{
    public void MapEndpoints(IEndpointRouteBuilder storefront)
    {
        storefront.MapGet("/prices", async (Guid[]? productIds, string? countryCode, System.Security.Claims.ClaimsPrincipal user, IGroupPricingService svc, CancellationToken ct) =>
                Results.Ok(await svc.PricesAsync(user.CustomerId(), productIds ?? [], countryCode, ct)))
            .WithTags("Storefront prices").AddEndpointFilter<ActiveCustomerFilter>();
    }
}
