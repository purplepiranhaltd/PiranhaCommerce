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

    /// <summary>The shipping methods this basket can use for a destination country, with what each would cost now (after any free-over rule). Null when the basket does not exist.</summary>
    Task<IReadOnlyList<ShippingOptionDto>?> ShippingOptionsAsync(Guid basketId, string countryCode, string? postalCode = null, CancellationToken ct = default);

    /// <summary>
    /// Puts a promo code on an open basket (null or blank removes it). A code that does not exist, is off, outside its dates or used up
    /// is a validation error with one answer; the minimum spend is judged at quote and checkout, as the basket may still grow.
    /// Null when the basket does not exist; conflict once it has been checked out.
    /// </summary>
    Task<BasketDto?> SetPromotionAsync(Guid basketId, string? code, CancellationToken ct = default);

    /// <summary>
    /// Puts a gift voucher on an open basket, to pay with at checkout (null or blank removes it). Every failure (no such code, off,
    /// expired, empty, wrong currency) is the same validation error. Null when the basket does not exist; conflict once checked out.
    /// </summary>
    Task<BasketDto?> SetVoucherAsync(Guid basketId, string? code, CancellationToken ct = default);

    /// <summary>
    /// The customer's open basket they used most recently, or null when they have none. This is how a shopper finds their basket again on a
    /// new device or after signing out, since a basket is otherwise known only by its id.
    /// </summary>
    Task<BasketDto?> OpenForCustomerAsync(Guid customerId, CancellationToken ct = default);

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

internal sealed partial class BasketService(PiranhaDbContext db, ICustomerService customers, IOptions<EcommerceOptions> options) : IBasketService
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

    public async Task<BasketDto?> OpenForCustomerAsync(Guid customerId, CancellationToken ct = default)
    {
        var id = await db.Set<Basket>().AsNoTracking().Where(b => b.CustomerId == customerId && b.Status == BasketStatus.Open)
            .OrderByDescending(b => b.UpdatedUtc ?? b.CreatedUtc).Select(b => (Guid?)b.Id).FirstOrDefaultAsync(ct);
        return id is null ? null : await GetAsync(id.Value, ct);
    }

    public async Task<bool> IsOwnedByAsync(Guid basketId, Guid customerId, CancellationToken ct = default)
        => await db.Set<Basket>().AsNoTracking().AnyAsync(b => b.Id == basketId && b.CustomerId == customerId, ct);

    public async Task<bool> ClaimAsync(Guid basketId, Guid customerId, CancellationToken ct = default)
    {
        // One atomic statement: of two customers claiming the same basket at once, exactly one wins.
        var claimed = await db.Set<Basket>().Where(b => b.Id == basketId && b.CustomerId == null && b.Status == BasketStatus.Open)
            .ExecuteUpdateAsync(s => s.SetProperty(b => b.CustomerId, (Guid?)customerId).SetProperty(b => b.UpdatedUtc, (DateTimeOffset?)DateTimeOffset.UtcNow), ct);
        return claimed > 0 || await IsOwnedByAsync(basketId, customerId, ct);
    }

    public async Task<BasketDto?> GetAsync(Guid basketId, CancellationToken ct = default)
    {
        var basket = await db.Set<Basket>().AsNoTracking().Where(b => b.Id == basketId)
            .Select(b => new { b.Id, b.Status, b.CustomerId, b.OrderId, b.PromotionCode, b.VoucherId }).FirstOrDefaultAsync(ct);
        if (basket is null) return null;

        var lines = await db.Set<BasketLine>().AsNoTracking().Where(l => l.BasketId == basketId)
            .Join(db.Set<Product>(), l => l.ProductId, p => p.Id, (l, p) => new { p.Id, p.Name, p.Sku, p.Price, l.Quantity, l.BasketId })
            .OrderBy(x => x.Name).ThenBy(x => x.Id)
            .ToListAsync(ct);
        var published = (await db.Set<ProductListing>().AsNoTracking()
            .Where(l => l.Published && db.Set<BasketLine>().Any(b => b.BasketId == basketId && b.ProductId == l.ProductId))
            .Select(l => l.ProductId).ToListAsync(ct)).ToHashSet();

        // What this customer pays: the regular price, or less when one of their groups gives a better one.
        var pay = await GroupPricing.EffectiveAsync(db, basket.CustomerId, lines.Select(x => (x.Id, x.Price)).ToList(), ct, lines.ToDictionary(x => x.Id, x => x.Quantity));
        var dtos = lines.Select(x => new BasketLineDto(x.Id, x.Name, x.Sku, pay[x.Id], x.Quantity, pay[x.Id] * x.Quantity, published.Contains(x.Id), pay[x.Id] < x.Price ? x.Price : null)).ToList();
        return new BasketDto(basket.Id, basket.Status, basket.CustomerId, basket.OrderId, options.Value.Currency.ToUpperInvariant(),
            dtos.Sum(d => d.LineTotal), dtos, basket.PromotionCode,
            basket.VoucherId is { } vid ? await db.Set<GiftVoucher>().AsNoTracking().Where(v => v.Id == vid).Select(v => v.CodeLast4).FirstOrDefaultAsync(ct) : null);
    }

    public async Task<BasketDto?> SetVoucherAsync(Guid basketId, string? code, CancellationToken ct = default)
    {
        Guid? voucherId = null;
        if (!string.IsNullOrWhiteSpace(code))
        {
            var hash = VoucherRules.Hash(code);
            var voucher = await db.Set<GiftVoucher>().AsNoTracking().FirstOrDefaultAsync(v => v.CodeHash == hash, ct);
            if (VoucherRules.Problem(voucher, options.Value.Currency, DateTimeOffset.UtcNow) is { } problem)
                throw new PiranhaValidationException(nameof(CodeInput.Code), problem);
            voucherId = voucher!.Id;
        }

        var changed = await db.Set<Basket>().Where(b => b.Id == basketId && b.Status == BasketStatus.Open)
            .ExecuteUpdateAsync(s => s.SetProperty(b => b.VoucherId, voucherId).SetProperty(b => b.UpdatedUtc, (DateTimeOffset?)DateTimeOffset.UtcNow), ct);
        if (changed == 0)
        {
            if (!await db.Set<Basket>().AnyAsync(b => b.Id == basketId, ct)) return null;
            throw new PiranhaConflictException("This basket has been checked out and can no longer be changed.");
        }
        return await GetAsync(basketId, ct);
    }

    public async Task<BasketDto?> SetPromotionAsync(Guid basketId, string? code, CancellationToken ct = default)
    {
        string? normalized = null;
        if (!string.IsNullOrWhiteSpace(code))
        {
            normalized = PromotionRules.Normalize(code);
            var rule = await db.Set<DiscountRule>().AsNoTracking().FirstOrDefaultAsync(r => r.Code == normalized, ct);
            if (rule is not null)
            {
                // Whether it gives anything depends on the basket, so the quote says; here it only has to be a code that is switched on.
                if (!rule.Active) throw new PiranhaValidationException(nameof(CodeInput.Code), PromotionRules.NotValid);
            }
            else
            {
                var promo = await db.Set<Promotion>().AsNoTracking().FirstOrDefaultAsync(p => p.Code == normalized, ct);
                var owner = await db.Set<Basket>().AsNoTracking().Where(b => b.Id == basketId).Select(b => b.CustomerId).FirstOrDefaultAsync(ct);
                if (PromotionRules.Problem(promo, decimal.MaxValue, DateTimeOffset.UtcNow, await PromotionEligibility.Async(db, promo, owner, ct)) is { } problem)
                    throw new PiranhaValidationException(nameof(CodeInput.Code), problem);
            }
        }

        // Only an open basket can change: once checked out it is read-only, and the update says so atomically.
        var changed = await db.Set<Basket>().Where(b => b.Id == basketId && b.Status == BasketStatus.Open)
            .ExecuteUpdateAsync(s => s.SetProperty(b => b.PromotionCode, normalized).SetProperty(b => b.UpdatedUtc, (DateTimeOffset?)DateTimeOffset.UtcNow), ct);
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
            await TouchAsync(basketId, ct);
            return await GetAsync(basketId, ct);
        }

        var sellable = await db.Set<ProductListing>().AsNoTracking().AnyAsync(l => l.ProductId == productId && l.Published, ct)
                       && await db.Set<Product>().AnyAsync(p => p.Id == productId, ct);
        if (!sellable)
            throw new PiranhaValidationException("productId", "That product is not available to buy.");
        if (await ProductVariants.HasOptionsAsync(db, productId, ct))
            throw new PiranhaValidationException("productId", "This product comes in options (such as size or colour): add one of its variants instead.");

        var quantity = input.Quantity;
        var limits = await db.Set<ProductListing>().AsNoTracking().Where(l => l.ProductId == productId).Select(l => new { l.MinOrderQuantity, l.MaxOrderQuantity }).FirstOrDefaultAsync(ct);
        if (limits is not null && OrderQuantityRules.Problem(await db.Set<Product>().Where(p => p.Id == productId).Select(p => p.Name).FirstAsync(ct), quantity, limits.MinOrderQuantity, limits.MaxOrderQuantity) is { } tooFewOrMany)
            throw new PiranhaValidationException(nameof(BasketLineInput.Quantity), tooFewOrMany);
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
        await TouchAsync(basketId, ct);
        return await GetAsync(basketId, ct);
    }

    /// <summary>Notes that the shopper has just been here, so an active basket is never taken for an abandoned one.</summary>
    private Task TouchAsync(Guid basketId, CancellationToken ct) =>
        db.Set<Basket>().Where(b => b.Id == basketId && b.Status == BasketStatus.Open)
            .ExecuteUpdateAsync(s => s.SetProperty(b => b.UpdatedUtc, (DateTimeOffset?)DateTimeOffset.UtcNow), ct);

    public async Task<IReadOnlyList<ShippingOptionDto>?> ShippingOptionsAsync(Guid basketId, string countryCode, string? postalCode = null, CancellationToken ct = default)
    {
        if (!System.Text.RegularExpressions.Regex.IsMatch(countryCode ?? "", "^[A-Za-z]{2}$"))
            throw new PiranhaValidationException("CountryCode", "Use a two-letter country code such as GB.");
        if (await GetAsync(basketId, ct) is null) return null;

        var options = new List<ShippingOptionDto>();
        var methods = await db.Set<ShippingMethod>().AsNoTracking().Where(m => m.Active).OrderBy(m => m.Position).ThenBy(m => m.Name).ThenBy(m => m.Id).ToListAsync(ct);
        foreach (var m in methods)
        {
            try
            {
                var quote = await QuoteAsync(basketId, new QuoteInput { ShippingMethodId = m.Id, CountryCode = countryCode!, PostalCode = postalCode }, ct);
                if (quote is null) return null;
                options.Add(new ShippingOptionDto(m.Id, m.Name, quote.ShippingTotal));
            }
            catch (PiranhaValidationException) { } // this method does not serve that destination and weight: left out
        }
        return options;
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

        // By weight and destination, when the method has rates: the same lookup checkout uses.
        decimal? shippingPrice = null;
        if (method is not null)
        {
            var weight = await ShippingPricing.WeightAsync(db, basket.Lines.Select(l => (l.ProductId, l.Quantity)).ToList(), ct);
            shippingPrice = await ShippingPricing.PriceAsync(db, method, input.CountryCode.ToUpperInvariant(), input.PostalCode, weight, ct)
                ?? throw new PiranhaValidationException(nameof(QuoteInput.ShippingMethodId), "That shipping method is not available for this destination and basket.");
        }

        // A code that cannot be used right now is reported and left out of the price; checkout would refuse it.
        // Discount rules (automatic ones, and the one whose code was entered) come first; a promo code then takes what it takes from what is left.
        var plan = await DiscountEngine.PlanAsync(db, basket.CustomerId,  basket.PromotionCode,
            basket.Lines.Select(l => new EngineLine(l.ProductId, l.Quantity, l.UnitPrice, l.LineTotal)).ToList(), DateTimeOffset.UtcNow, ct);
        var discount = 0m;
        string? problem = plan.CodeProblem;
        if (basket.PromotionCode is { } code && !plan.CodeIsDiscount)
        {
            var promo = await db.Set<Promotion>().AsNoTracking().FirstOrDefaultAsync(p => p.Code == code, ct);
            problem = PromotionRules.Problem(promo, basket.Subtotal, DateTimeOffset.UtcNow, await PromotionEligibility.Async(db, promo, basket.CustomerId, ct));
            if (problem is null) discount = PromotionRules.Discount(promo!, basket.Subtotal - plan.Total);
        }

        var (rules, defaultClass) = await TaxLookup.LoadAsync(db, ct);
        var classes = await TaxLookup.ClassesAsync(db, basket.Lines.Select(l => l.ProductId).ToList(), ct);
        var calc = OrderPricing.Calculate(
            basket.Lines.Select(l => (l.LineTotal, classes.GetValueOrDefault(l.ProductId))).ToList(),
            discount, method, rules, defaultClass, input.CountryCode.ToUpperInvariant(), shippingPrice, plan.LineDiscounts, plan.FreeShipping);

        // A gift voucher pays part of the total; it never changes the price or the tax.
        var applied = 0m;
        string? voucherProblem = null;
        var voucherId = await db.Set<Basket>().AsNoTracking().Where(b => b.Id == basketId).Select(b => b.VoucherId).FirstOrDefaultAsync(ct);
        if (voucherId is { } vid)
        {
            var voucher = await db.Set<GiftVoucher>().AsNoTracking().FirstOrDefaultAsync(v => v.Id == vid, ct);
            voucherProblem = VoucherRules.Problem(voucher, basket.Currency, DateTimeOffset.UtcNow);
            if (voucherProblem is null) applied = Math.Min(voucher!.Balance, calc.Total);
        }

        return new QuoteDto(basket.Currency, basket.Subtotal, calc.ShippingTotal, calc.SummaryName, calc.SummaryRate, calc.Tax, calc.Total,
            calc.Breakdown, calc.Discount, basket.PromotionCode, problem, applied, calc.Total - applied, basket.VoucherLast4, voucherProblem,
            basket.Lines.Select((l, i) => new QuoteLineDto(l.ProductId, l.Name, l.Quantity, l.UnitPrice, l.LineTotal, calc.LineDiscounts[i], calc.Lines[i].Name, calc.Lines[i].Rate,
                calc.Lines[i].Amount, l.LineTotal - calc.LineDiscounts[i] + calc.Lines[i].Amount)).ToList(), calc.Shipping.Amount,
            plan.Applied.Select(a => new AppliedDiscountDto(a.Name, a.Code, a.Amount, a.FreeShipping)).ToList());
    }

    private Task<int> SetQuantityAsync(Guid basketId, Guid productId, int quantity, CancellationToken ct)
        => db.Set<BasketLine>().Where(l => l.BasketId == basketId && l.ProductId == productId)
            .ExecuteUpdateAsync(s => s.SetProperty(l => l.Quantity, quantity), ct);
}
