using System.ComponentModel.DataAnnotations;
using PiranhaCommerce.Core.Audit;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using PiranhaCommerce.Api;
using PiranhaCommerce.Catalog;
using PiranhaCommerce.Core.Domain;
using PiranhaCommerce.Core.Validation;
using PiranhaCommerce.Data;

namespace PiranhaCommerce.Ecommerce;

/// <summary>
/// A sale: between two dates, a percentage off some or all products, or a fixed sale price on named ones. Everyone gets the sale price, signed in or not.
/// </summary>
[Audited]
public class Sale : AuditableEntity
{
    public required string Name { get; set; }
    public DateTimeOffset StartsUtc { get; set; }
    public DateTimeOffset EndsUtc { get; set; }
    /// <summary>A switch to pull a sale early (or hold one back) without deleting it.</summary>
    public bool Enabled { get; set; } = true;
    /// <summary>Off the regular price of every product the sale covers that has no fixed sale price. 0 means only fixed sale prices apply.</summary>
    public decimal Percent { get; set; }
    /// <summary>Covers every product.</summary>
    public bool AllProducts { get; set; }
}

/// <summary>A product named in a sale, with a fixed sale price or none (then the sale's percentage applies to it).</summary>
[Audited]
public class SaleProduct
{
    public Guid SaleId { get; set; }
    public Guid ProductId { get; set; }
    public decimal? Price { get; set; }
}

/// <summary>A category in a sale: covers its products and those of its sub-categories.</summary>
[Audited]
public class SaleCategory
{
    public Guid SaleId { get; set; }
    public Guid CategoryId { get; set; }
}

public enum SaleStatus { Off, Scheduled, Running, Ended }

public sealed class SaleInput
{
    [Required, StringLength(200)] public string Name { get; set; } = "";
    public DateTimeOffset StartsUtc { get; set; }
    /// <summary>The sale is over from this moment. Must be after the start.</summary>
    public DateTimeOffset EndsUtc { get; set; }
    public bool Enabled { get; set; } = true;
    /// <summary>0 to 100, up to 2 decimal places.</summary>
    [Range(typeof(decimal), "0", "100")] public decimal Percent { get; set; }
    public bool AllProducts { get; set; }
}

public sealed class SaleProductInput
{
    /// <summary>A fixed sale price for this product. Leave out to use the sale's percentage.</summary>
    [Range(typeof(decimal), "0", "1000000000")] public decimal? Price { get; set; }
}

public sealed record SaleProductDto(Guid ProductId, string Name, string? Sku, decimal RegularPrice, decimal? Price);
public sealed record SaleCategoryDto(Guid CategoryId, string Name);
public sealed record SaleDto(
    Guid Id, string Name, DateTimeOffset StartsUtc, DateTimeOffset EndsUtc, bool Enabled, decimal Percent, bool AllProducts, SaleStatus Status,
    IReadOnlyList<SaleProductDto> Products, IReadOnlyList<SaleCategoryDto> Categories);

public interface ISaleService
{
    /// <summary>Newest start first. <paramref name="status"/> null for all.</summary>
    Task<IReadOnlyList<SaleDto>> ListAsync(SaleStatus? status, CancellationToken ct = default);
    Task<SaleDto?> GetAsync(Guid id, CancellationToken ct = default);
    Task<SaleDto> CreateAsync(SaleInput input, CancellationToken ct = default);
    Task<SaleDto?> UpdateAsync(Guid id, SaleInput input, CancellationToken ct = default);
    Task<bool> DeleteAsync(Guid id, CancellationToken ct = default);

    /// <summary>Puts a product in the sale (or changes its fixed price). Null when the sale or product does not exist.</summary>
    Task<SaleDto?> SetProductAsync(Guid saleId, Guid productId, SaleProductInput input, CancellationToken ct = default);
    Task<SaleDto?> RemoveProductAsync(Guid saleId, Guid productId, CancellationToken ct = default);
    /// <summary>Puts a category (and everything under it) in the sale. Null when the sale or category does not exist.</summary>
    Task<SaleDto?> AddCategoryAsync(Guid saleId, Guid categoryId, CancellationToken ct = default);
    Task<SaleDto?> RemoveCategoryAsync(Guid saleId, Guid categoryId, CancellationToken ct = default);
}

/// <summary>
/// What running sales do to a price. A product a sale covers costs its fixed sale price if it has one there, otherwise the sale's percentage off the
/// regular price (rounded to 2 places); with several sales the lowest wins; never more than the regular price. Group prices are worked out separately
/// from the regular price and the customer pays the lower of the two (they do not add together). Baskets, quotes, checkout and the price lookups all
/// go through <see cref="GroupPricing.EffectiveAsync"/>, which uses this, so they agree.
/// </summary>
internal static class SalePricing
{
    internal static SaleStatus StatusOf(bool enabled, DateTimeOffset starts, DateTimeOffset ends, DateTimeOffset now) =>
        !enabled ? SaleStatus.Off : now < starts ? SaleStatus.Scheduled : now >= ends ? SaleStatus.Ended : SaleStatus.Running;

    /// <summary>For each product a running sale lowers, its sale price and when that sale ends. Products at their regular price are left out.</summary>
    public static async Task<Dictionary<Guid, (decimal Price, DateTimeOffset EndsUtc)>> ActiveAsync(
        PiranhaDbContext db, IReadOnlyCollection<(Guid ProductId, decimal Price)> items, CancellationToken ct)
    {
        var result = new Dictionary<Guid, (decimal, DateTimeOffset)>();
        if (items.Count == 0) return result;
        var now = DateTimeOffset.UtcNow;
        var sales = await db.Set<Sale>().AsNoTracking().Where(s => s.Enabled && s.StartsUtc <= now && s.EndsUtc > now)
            .Select(s => new { s.Id, s.Percent, s.AllProducts, s.EndsUtc }).ToListAsync(ct);
        if (sales.Count == 0) return result;

        var saleIds = sales.Select(s => s.Id).ToList();
        var productIds = items.Select(i => i.ProductId).ToList();
        var named = (await db.Set<SaleProduct>().AsNoTracking().Where(p => saleIds.Contains(p.SaleId) && productIds.Contains(p.ProductId)).ToListAsync(ct))
            .ToDictionary(p => (p.SaleId, p.ProductId), p => p.Price);
        var saleCategories = (await db.Set<SaleCategory>().AsNoTracking().Where(c => saleIds.Contains(c.SaleId)).ToListAsync(ct))
            .GroupBy(c => c.SaleId).ToDictionary(g => g.Key, g => g.Select(c => c.CategoryId).ToHashSet());

        var parents = new Dictionary<Guid, Guid?>();
        var inCategories = new Dictionary<Guid, List<Guid>>();
        if (saleCategories.Count > 0)
        {
            parents = await db.Set<Category>().AsNoTracking().ToDictionaryAsync(c => c.Id, c => c.ParentId, ct);
            inCategories = await ProductVariants.CategoriesAsync(db, productIds, ct);
        }

        foreach (var (productId, regular) in items)
        {
            decimal best = regular; DateTimeOffset? endsAt = null;
            foreach (var sale in sales)
            {
                decimal? candidate = null;
                if (named.TryGetValue((sale.Id, productId), out var fixedPrice))
                    candidate = fixedPrice ?? (sale.Percent > 0 ? GroupPricing.Percentage(regular, sale.Percent) : null);
                else if (sale.Percent > 0 && (sale.AllProducts || InCategory(inCategories.GetValueOrDefault(productId), saleCategories.GetValueOrDefault(sale.Id), parents)))
                    candidate = GroupPricing.Percentage(regular, sale.Percent);
                if (candidate is { } c && c < best) { best = c; endsAt = sale.EndsUtc; }
            }
            if (endsAt is { } e) result[productId] = (best, e);
        }
        return result;
    }

    /// <summary>True when one of the product's categories, or any category above it, is one the sale covers.</summary>
    private static bool InCategory(List<Guid>? productCategories, HashSet<Guid>? covered, Dictionary<Guid, Guid?> parents)
    {
        if (productCategories is null || covered is null) return false;
        foreach (var start in productCategories)
        {
            Guid? current = start;
            for (var depth = 0; current is { } c && depth < 64; depth++)
            {
                if (covered.Contains(c)) return true;
                current = parents.GetValueOrDefault(c);
            }
        }
        return false;
    }
}

internal sealed class SaleService(PiranhaDbContext db) : ISaleService
{
    public async Task<IReadOnlyList<SaleDto>> ListAsync(SaleStatus? status, CancellationToken ct = default)
    {
        var sales = await db.Set<Sale>().AsNoTracking().OrderByDescending(s => s.StartsUtc).ThenBy(s => s.Id).ToListAsync(ct);
        var now = DateTimeOffset.UtcNow;
        if (status is { } wanted) sales = sales.Where(s => SalePricing.StatusOf(s.Enabled, s.StartsUtc, s.EndsUtc, now) == wanted).ToList();
        var result = new List<SaleDto>();
        foreach (var s in sales) result.Add(await ToDtoAsync(s, now, ct));
        return result;
    }

    public async Task<SaleDto?> GetAsync(Guid id, CancellationToken ct = default) =>
        await db.Set<Sale>().AsNoTracking().FirstOrDefaultAsync(s => s.Id == id, ct) is { } s ? await ToDtoAsync(s, DateTimeOffset.UtcNow, ct) : null;

    public async Task<SaleDto> CreateAsync(SaleInput input, CancellationToken ct = default)
    {
        Check(input);
        var sale = new Sale { Name = input.Name.Trim(), StartsUtc = input.StartsUtc, EndsUtc = input.EndsUtc, Enabled = input.Enabled, Percent = input.Percent, AllProducts = input.AllProducts };
        db.Add(sale);
        await db.SaveChangesAsync(ct);
        return (await GetAsync(sale.Id, ct))!;
    }

    public async Task<SaleDto?> UpdateAsync(Guid id, SaleInput input, CancellationToken ct = default)
    {
        Check(input);
        var changed = await db.UpdateTrackedAsync<Sale>(s => s.Id == id, s =>
        {
            s.Name = input.Name.Trim(); s.StartsUtc = input.StartsUtc; s.EndsUtc = input.EndsUtc;
            s.Enabled = input.Enabled; s.Percent = input.Percent; s.AllProducts = input.AllProducts;
        }, ct);
        return changed == 0 ? null : await GetAsync(id, ct);
    }

    public async Task<bool> DeleteAsync(Guid id, CancellationToken ct = default) => await db.DeleteTrackedAsync<Sale>(s => s.Id == id, ct) > 0;

    public async Task<SaleDto?> SetProductAsync(Guid saleId, Guid productId, SaleProductInput input, CancellationToken ct = default)
    {
        ModelValidator.ValidateOrThrow(input);
        if (input.Price is { } p && decimal.Round(p, 4) != p) throw new PiranhaValidationException(nameof(SaleProductInput.Price), "A price can have at most 4 decimal places.");
        if (!await db.Set<Sale>().AnyAsync(s => s.Id == saleId, ct) || !await db.Set<Product>().AnyAsync(x => x.Id == productId, ct)) return null;

        var updated = await db.UpdateTrackedAsync<SaleProduct>(x => x.SaleId == saleId && x.ProductId == productId, x => x.Price = input.Price, ct);
        if (updated == 0)
        {
            try
            {
                db.Add(new SaleProduct { SaleId = saleId, ProductId = productId, Price = input.Price });
                await db.SaveChangesAsync(ct);
            }
            catch (DbUpdateException)
            {
                db.ChangeTracker.Clear();
                await db.UpdateTrackedAsync<SaleProduct>(x => x.SaleId == saleId && x.ProductId == productId, x => x.Price = input.Price, ct);
            }
        }
        return await GetAsync(saleId, ct);
    }

    public async Task<SaleDto?> RemoveProductAsync(Guid saleId, Guid productId, CancellationToken ct = default)
    {
        await db.DeleteTrackedAsync<SaleProduct>(x => x.SaleId == saleId && x.ProductId == productId, ct);
        return await GetAsync(saleId, ct);
    }

    public async Task<SaleDto?> AddCategoryAsync(Guid saleId, Guid categoryId, CancellationToken ct = default)
    {
        if (!await db.Set<Sale>().AnyAsync(s => s.Id == saleId, ct) || !await db.Set<Category>().AnyAsync(c => c.Id == categoryId, ct)) return null;
        if (!await db.Set<SaleCategory>().AnyAsync(c => c.SaleId == saleId && c.CategoryId == categoryId, ct))
        {
            try
            {
                db.Add(new SaleCategory { SaleId = saleId, CategoryId = categoryId });
                await db.SaveChangesAsync(ct);
            }
            catch (DbUpdateException) { db.ChangeTracker.Clear(); } // added at the same moment: it is there
        }
        return await GetAsync(saleId, ct);
    }

    public async Task<SaleDto?> RemoveCategoryAsync(Guid saleId, Guid categoryId, CancellationToken ct = default)
    {
        await db.DeleteTrackedAsync<SaleCategory>(c => c.SaleId == saleId && c.CategoryId == categoryId, ct);
        return await GetAsync(saleId, ct);
    }

    private static void Check(SaleInput input)
    {
        ModelValidator.ValidateOrThrow(input);
        if (string.IsNullOrWhiteSpace(input.Name)) throw new PiranhaValidationException(nameof(SaleInput.Name), "A sale needs a name.");
        if (decimal.Round(input.Percent, 2) != input.Percent) throw new PiranhaValidationException(nameof(SaleInput.Percent), "The percentage can have at most 2 decimal places.");
        if (input.EndsUtc <= input.StartsUtc) throw new PiranhaValidationException(nameof(SaleInput.EndsUtc), "The sale must end after it starts.");
    }

    private async Task<SaleDto> ToDtoAsync(Sale s, DateTimeOffset now, CancellationToken ct)
    {
        var products = await (from sp in db.Set<SaleProduct>().AsNoTracking()
                              join p in db.Set<Product>().AsNoTracking() on sp.ProductId equals p.Id
                              where sp.SaleId == s.Id
                              orderby p.Name
                              select new SaleProductDto(p.Id, p.Name, p.Sku, p.Price, sp.Price)).ToListAsync(ct);
        var categories = await (from sc in db.Set<SaleCategory>().AsNoTracking()
                                join c in db.Set<Category>().AsNoTracking() on sc.CategoryId equals c.Id
                                where sc.SaleId == s.Id
                                orderby c.Name
                                select new SaleCategoryDto(c.Id, c.Name)).ToListAsync(ct);
        return new SaleDto(s.Id, s.Name, s.StartsUtc, s.EndsUtc, s.Enabled, s.Percent, s.AllProducts, SalePricing.StatusOf(s.Enabled, s.StartsUtc, s.EndsUtc, now), products, categories);
    }
}

internal sealed class SaleEndpoints : IApiEndpointContributor
{
    public void MapEndpoints(IEndpointRouteBuilder api)
    {
        var sales = api.MapGroup("/sales").WithTags("Sales");
        sales.MapGet("/", async (SaleStatus? status, ISaleService svc, CancellationToken ct) => Results.Ok(await svc.ListAsync(status, ct)));
        sales.MapGet("/{id:guid}", async (Guid id, ISaleService svc, CancellationToken ct) => await svc.GetAsync(id, ct) is { } s ? Results.Ok(s) : Results.NotFound());
        sales.MapPost("/", async (SaleInput input, ISaleService svc, CancellationToken ct) =>
        {
            var created = await svc.CreateAsync(input, ct);
            return Results.Created($"/api/v1/sales/{created.Id}", created);
        }).RequireAuthorization(ApiPolicies.Write);
        sales.MapPut("/{id:guid}", async (Guid id, SaleInput input, ISaleService svc, CancellationToken ct) =>
            await svc.UpdateAsync(id, input, ct) is { } s ? Results.Ok(s) : Results.NotFound()).RequireAuthorization(ApiPolicies.Write);
        sales.MapDelete("/{id:guid}", async (Guid id, ISaleService svc, CancellationToken ct) =>
            await svc.DeleteAsync(id, ct) ? Results.NoContent() : Results.NotFound()).RequireAuthorization(ApiPolicies.Write);
        sales.MapPut("/{id:guid}/products/{productId:guid}", async (Guid id, Guid productId, SaleProductInput input, ISaleService svc, CancellationToken ct) =>
            await svc.SetProductAsync(id, productId, input, ct) is { } s ? Results.Ok(s) : Results.NotFound()).RequireAuthorization(ApiPolicies.Write);
        sales.MapDelete("/{id:guid}/products/{productId:guid}", async (Guid id, Guid productId, ISaleService svc, CancellationToken ct) =>
            await svc.RemoveProductAsync(id, productId, ct) is { } s ? Results.Ok(s) : Results.NotFound()).RequireAuthorization(ApiPolicies.Write);
        sales.MapPut("/{id:guid}/categories/{categoryId:guid}", async (Guid id, Guid categoryId, ISaleService svc, CancellationToken ct) =>
            await svc.AddCategoryAsync(id, categoryId, ct) is { } s ? Results.Ok(s) : Results.NotFound()).RequireAuthorization(ApiPolicies.Write);
        sales.MapDelete("/{id:guid}/categories/{categoryId:guid}", async (Guid id, Guid categoryId, ISaleService svc, CancellationToken ct) =>
            await svc.RemoveCategoryAsync(id, categoryId, ct) is { } s ? Results.Ok(s) : Results.NotFound()).RequireAuthorization(ApiPolicies.Write);
    }
}

/// <summary>Prices for anyone, signed in or not: the regular price and any sale price. (Signed-in shoppers use <c>/storefront/prices</c>, which also applies their groups.)</summary>
internal sealed class SaleStorefrontEndpoints : IStorefrontEndpointContributor
{
    public void MapEndpoints(IEndpointRouteBuilder storefront) =>
        storefront.MapGet("/guest/prices", async (Guid[]? productIds, string? countryCode, IGroupPricingService svc, CancellationToken ct) =>
                Results.Ok(await svc.PricesAsync(null, productIds ?? [], countryCode, ct)))
            .AllowAnonymous().WithTags("Storefront prices");
}
