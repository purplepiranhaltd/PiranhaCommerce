using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using PiranhaCommerce.Catalog;
using PiranhaCommerce.Core.Validation;
using PiranhaCommerce.Customers;
using PiranhaCommerce.Data;

namespace PiranhaCommerce.Ecommerce;

public interface IBasketService
{
    Task<BasketDto> CreateAsync(BasketCreateInput input, CancellationToken ct = default);

    Task<BasketDto?> GetAsync(Guid basketId, CancellationToken ct = default);

    /// <summary>
    /// Puts a promo code on an open basket (null or blank removes it). A code that does not exist, is off, outside its dates or used up
    /// is a validation error with one answer; the minimum spend is judged at quote and checkout, as the basket may still grow.
    /// Null when the basket does not exist; conflict once it has been checked out.
    /// </summary>
    Task<BasketDto?> SetPromotionAsync(Guid basketId, string? code, CancellationToken ct = default);

    /// <summary>True only when the basket exists and belongs to that customer.</summary>
    Task<bool> IsOwnedByAsync(Guid basketId, Guid customerId, CancellationToken ct = default);

    /// <summary>
    /// Gives an open basket that has no owner to a customer (for a shopper who started shopping before signing in). Knowing the
    /// basket id is the proof, as it is for every basket call. Already theirs: true. Owned by someone else, checked out or missing: false.
    /// </summary>
    Task<bool> ClaimAsync(Guid basketId, Guid customerId, CancellationToken ct = default);

    /// <summary>
    /// Sets the quantity of a product (0 removes it). Null when the basket does not exist. Only published products can be added;
    /// a basket that has been checked out is read-only (conflict).
    /// </summary>
    Task<BasketDto?> SetLineAsync(Guid basketId, Guid productId, BasketLineInput input, CancellationToken ct = default);

    /// <summary>What checkout would charge right now for a shipping method and destination country. Null when the basket does not exist.</summary>
    Task<QuoteDto?> QuoteAsync(Guid basketId, QuoteInput input, CancellationToken ct = default);
}

internal sealed class BasketService(PiranhaDbContext db, ICustomerService customers, IOptions<EcommerceOptions> options) : IBasketService
{
    public async Task<BasketDto> CreateAsync(BasketCreateInput input, CancellationToken ct = default)
    {
        if (input.CustomerId is { } customerId && await customers.GetAsync(customerId, ct) is null)
            throw new PiranhaValidationException(nameof(BasketCreateInput.CustomerId), "That customer does not exist.");

        var basket = new Basket { CustomerId = input.CustomerId };
        db.Add(basket);
        await db.SaveChangesAsync(ct);
        return await GetAsync(basket.Id, ct) ?? throw new InvalidOperationException("Basket vanished after creation.");
    }

    public async Task<bool> IsOwnedByAsync(Guid basketId, Guid customerId, CancellationToken ct = default)
        => await db.Set<Basket>().AsNoTracking().AnyAsync(b => b.Id == basketId && b.CustomerId == customerId, ct);

    public async Task<bool> ClaimAsync(Guid basketId, Guid customerId, CancellationToken ct = default)
    {
        // One atomic statement: of two customers claiming the same basket at once, exactly one wins.
        var claimed = await db.Set<Basket>().Where(b => b.Id == basketId && b.CustomerId == null && b.Status == BasketStatus.Open)
            .ExecuteUpdateAsync(s => s.SetProperty(b => b.CustomerId, (Guid?)customerId), ct);
        return claimed > 0 || await IsOwnedByAsync(basketId, customerId, ct);
    }

    public async Task<BasketDto?> GetAsync(Guid basketId, CancellationToken ct = default)
    {
        var basket = await db.Set<Basket>().AsNoTracking().Where(b => b.Id == basketId)
            .Select(b => new { b.Id, b.Status, b.CustomerId, b.OrderId, b.PromotionCode }).FirstOrDefaultAsync(ct);
        if (basket is null) return null;

        var lines = await db.Set<BasketLine>().AsNoTracking().Where(l => l.BasketId == basketId)
            .Join(db.Set<Product>(), l => l.ProductId, p => p.Id, (l, p) => new { p.Id, p.Name, p.Sku, p.Price, l.Quantity, l.BasketId })
            .OrderBy(x => x.Name).ThenBy(x => x.Id)
            .ToListAsync(ct);
        var published = (await db.Set<ProductListing>().AsNoTracking()
            .Where(l => l.Published && db.Set<BasketLine>().Any(b => b.BasketId == basketId && b.ProductId == l.ProductId))
            .Select(l => l.ProductId).ToListAsync(ct)).ToHashSet();

        var dtos = lines.Select(x => new BasketLineDto(x.Id, x.Name, x.Sku, x.Price, x.Quantity, x.Price * x.Quantity, published.Contains(x.Id))).ToList();
        return new BasketDto(basket.Id, basket.Status, basket.CustomerId, basket.OrderId, options.Value.Currency.ToUpperInvariant(),
            dtos.Sum(d => d.LineTotal), dtos, basket.PromotionCode);
    }

    public async Task<BasketDto?> SetPromotionAsync(Guid basketId, string? code, CancellationToken ct = default)
    {
        string? normalized = null;
        if (!string.IsNullOrWhiteSpace(code))
        {
            normalized = PromotionRules.Normalize(code);
            var promo = await db.Set<Promotion>().AsNoTracking().FirstOrDefaultAsync(p => p.Code == normalized, ct);
            if (PromotionRules.Problem(promo, decimal.MaxValue, DateTimeOffset.UtcNow) is { } problem)
                throw new PiranhaValidationException(nameof(CodeInput.Code), problem);
        }

        // Only an open basket can change: once checked out it is read-only, and the update says so atomically.
        var changed = await db.Set<Basket>().Where(b => b.Id == basketId && b.Status == BasketStatus.Open)
            .ExecuteUpdateAsync(s => s.SetProperty(b => b.PromotionCode, normalized), ct);
        if (changed == 0)
        {
            if (!await db.Set<Basket>().AnyAsync(b => b.Id == basketId, ct)) return null;
            throw new PiranhaConflictException("This basket has been checked out and can no longer be changed.");
        }
        return await GetAsync(basketId, ct);
    }

    public async Task<BasketDto?> SetLineAsync(Guid basketId, Guid productId, BasketLineInput input, CancellationToken ct = default)
    {
        ModelValidator.ValidateOrThrow(input);

        var status = await db.Set<Basket>().AsNoTracking().Where(b => b.Id == basketId).Select(b => (BasketStatus?)b.Status).FirstOrDefaultAsync(ct);
        if (status is null) return null;
        if (status != BasketStatus.Open) throw new PiranhaConflictException("This basket has been checked out and can no longer be changed.");

        if (input.Quantity == 0)
        {
            await db.Set<BasketLine>().Where(l => l.BasketId == basketId && l.ProductId == productId).ExecuteDeleteAsync(ct);
            return await GetAsync(basketId, ct);
        }

        var sellable = await db.Set<ProductListing>().AsNoTracking().AnyAsync(l => l.ProductId == productId && l.Published, ct)
                       && await db.Set<Product>().AnyAsync(p => p.Id == productId, ct);
        if (!sellable)
            throw new PiranhaValidationException("productId", "That product is not available to buy.");

        var quantity = input.Quantity;
        var updated = await SetQuantityAsync(basketId, productId, quantity, ct);
        if (updated == 0)
        {
            try
            {
                db.Add(new BasketLine { BasketId = basketId, ProductId = productId, Quantity = quantity });
                await db.SaveChangesAsync(ct);
            }
            catch (DbUpdateException)
            {
                db.ChangeTracker.Clear();
                // Lost a race with another request adding the same product (the unique index refused it): set the quantity instead.
                if (await SetQuantityAsync(basketId, productId, quantity, ct) == 0)
                {
                    if (!await db.Set<Basket>().AnyAsync(b => b.Id == basketId, CancellationToken.None)) return null;
                    throw;
                }
            }
        }
        return await GetAsync(basketId, ct);
    }

    public async Task<QuoteDto?> QuoteAsync(Guid basketId, QuoteInput input, CancellationToken ct = default)
    {
        ModelValidator.ValidateOrThrow(input);
        var basket = await GetAsync(basketId, ct);
        if (basket is null) return null;

        var methods = await db.Set<ShippingMethod>().AsNoTracking().Where(m => m.Active).ToListAsync(ct);
        ShippingMethod? method = null;
        if (methods.Count > 0)
            method = methods.FirstOrDefault(m => m.Id == input.ShippingMethodId)
                     ?? throw new PiranhaValidationException(nameof(QuoteInput.ShippingMethodId), "Choose an available shipping method.");

        // A code that cannot be used right now is reported and left out of the price; checkout would refuse it.
        var discount = 0m;
        string? problem = null;
        if (basket.PromotionCode is { } code)
        {
            var promo = await db.Set<Promotion>().AsNoTracking().FirstOrDefaultAsync(p => p.Code == code, ct);
            problem = PromotionRules.Problem(promo, basket.Subtotal, DateTimeOffset.UtcNow);
            if (problem is null) discount = PromotionRules.Discount(promo!, basket.Subtotal);
        }

        var (rules, defaultClass) = await TaxLookup.LoadAsync(db, ct);
        var classes = await TaxLookup.ClassesAsync(db, basket.Lines.Select(l => l.ProductId).ToList(), ct);
        var calc = OrderPricing.Calculate(
            basket.Lines.Select(l => (l.LineTotal, classes.GetValueOrDefault(l.ProductId))).ToList(),
            discount, method, rules, defaultClass, input.CountryCode.ToUpperInvariant());
        return new QuoteDto(basket.Currency, basket.Subtotal, calc.ShippingTotal, calc.SummaryName, calc.SummaryRate, calc.Tax, calc.Total,
            calc.Breakdown, calc.Discount, basket.PromotionCode, problem);
    }

    private Task<int> SetQuantityAsync(Guid basketId, Guid productId, int quantity, CancellationToken ct)
        => db.Set<BasketLine>().Where(l => l.BasketId == basketId && l.ProductId == productId)
            .ExecuteUpdateAsync(s => s.SetProperty(l => l.Quantity, quantity), ct);
}
