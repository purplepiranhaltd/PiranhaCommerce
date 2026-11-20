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
            .Select(b => new { b.Id, b.Status, b.CustomerId, b.OrderId }).FirstOrDefaultAsync(ct);
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
            dtos.Sum(d => d.LineTotal), dtos);
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

        var rule = OrderPricing.RuleFor(await db.Set<TaxRule>().AsNoTracking().ToListAsync(ct), input.CountryCode.ToUpperInvariant());
        var shipping = OrderPricing.ShippingFor(method, basket.Subtotal);
        var (tax, total) = OrderPricing.Totals(basket.Subtotal, shipping, rule);
        return new QuoteDto(basket.Currency, basket.Subtotal, shipping, rule?.Name, rule?.Rate ?? 0m, tax, total);
    }

    private Task<int> SetQuantityAsync(Guid basketId, Guid productId, int quantity, CancellationToken ct)
        => db.Set<BasketLine>().Where(l => l.BasketId == basketId && l.ProductId == productId)
            .ExecuteUpdateAsync(s => s.SetProperty(l => l.Quantity, quantity), ct);
}
