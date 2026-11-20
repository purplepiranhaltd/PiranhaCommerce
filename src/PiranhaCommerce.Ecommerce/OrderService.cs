using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using PiranhaCommerce.Catalog;
using PiranhaCommerce.Core.Email;
using PiranhaCommerce.Core.Paging;
using PiranhaCommerce.Core.Validation;
using PiranhaCommerce.Customers;
using PiranhaCommerce.Data;

namespace PiranhaCommerce.Ecommerce;

public interface IOrderService
{
    /// <summary>Newest first. <paramref name="search"/> matches an order number (with or without the ORD- prefix) or part of the email.</summary>
    Task<PagedResult<OrderSummaryDto>> ListAsync(int? page, int? pageSize, OrderStage? stage, string? search, CancellationToken ct = default);

    Task<OrderDto?> GetAsync(Guid id, CancellationToken ct = default);

    /// <summary>One customer's own orders, newest first.</summary>
    Task<PagedResult<OrderSummaryDto>> ListForCustomerAsync(Guid customerId, int? page, int? pageSize, CancellationToken ct = default);

    /// <summary>Null when the order does not exist OR belongs to someone else (the two look the same on purpose).</summary>
    Task<OrderDto?> GetForCustomerAsync(Guid customerId, Guid id, CancellationToken ct = default);

    Task<OrderDto?> GetByNumberAsync(long number, CancellationToken ct = default);

    /// <summary>
    /// Turns a basket into an order, all or nothing: the order, its numbered copy of every price/name/address, and the stock taken
    /// from the shelf are committed in ONE transaction, so there is never an order without its stock movements or the reverse.
    /// Checking out the same basket again returns the same order. Null when the basket does not exist. Conflict when stock is
    /// short, or the basket is empty or holds something no longer published.
    /// </summary>
    Task<OrderDto?> CheckoutAsync(Guid basketId, CheckoutInput input, string? actor = null, CancellationToken ct = default);

    /// <summary>
    /// Moves an order to a later stage (see <see cref="OrderStages.Next"/>). Cancelling puts the stock back, once, in the same
    /// transaction. Null when the order does not exist; conflict when the move is not allowed from the current stage.
    /// </summary>
    Task<OrderDto?> ChangeStageAsync(Guid id, OrderStage to, string? actor = null, CancellationToken ct = default);
}

internal sealed class OrderService(
    PiranhaDbContext db, IStockService stock, ICustomerService customers, IOptions<EcommerceOptions> options,
    IEmailSender email, ILogger<OrderService> logger) : IOrderService
{
    public async Task<PagedResult<OrderSummaryDto>> ListAsync(int? page, int? pageSize, OrderStage? stage, string? search, CancellationToken ct = default)
    {
        var (p, size) = Paging.Normalize(page, pageSize);
        var query = db.Set<Order>().AsNoTracking().AsQueryable();
        if (stage is { } s) query = query.Where(o => o.Stage == s);
        if (!string.IsNullOrWhiteSpace(search))
        {
            var term = search.Trim();
            var digits = term.StartsWith("ORD-", StringComparison.OrdinalIgnoreCase) ? term[4..] : term.TrimStart('#');
            if (long.TryParse(digits, out var number))
                query = query.Where(o => o.Number == number || o.Email.Contains(term));
            else
                query = query.Where(o => o.Email.Contains(term));
        }

        return await PageAsync(query, p, size, ct);
    }

    public async Task<PagedResult<OrderSummaryDto>> ListForCustomerAsync(Guid customerId, int? page, int? pageSize, CancellationToken ct = default)
    {
        var (p, size) = Paging.Normalize(page, pageSize);
        return await PageAsync(db.Set<Order>().AsNoTracking().Where(o => o.CustomerId == customerId), p, size, ct);
    }

    public async Task<OrderDto?> GetForCustomerAsync(Guid customerId, Guid id, CancellationToken ct = default)
        => await WithPaymentsAsync(await db.Set<Order>().AsNoTracking().Include(o => o.Lines).AsSplitQuery()
            .FirstOrDefaultAsync(o => o.Id == id && o.CustomerId == customerId, ct), ct);

    private async Task<PagedResult<OrderSummaryDto>> PageAsync(IQueryable<Order> query, int p, int size, CancellationToken ct)
    {
        var total = await query.CountAsync(ct);
        var items = await query.OrderByDescending(o => o.Number).Skip((p - 1) * size).Take(size)
            .Select(o => new { o.Id, o.Number, o.Stage, o.Email, o.Currency, o.Total, o.CreatedUtc })
            .ToListAsync(ct);
        var ids = items.Select(o => o.Id).ToList();
        var sums = await db.Set<Payment>().AsNoTracking().Where(x => ids.Contains(x.OrderId))
            .GroupBy(x => new { x.OrderId, x.Kind }).Select(g => new { g.Key.OrderId, g.Key.Kind, Sum = g.Sum(x => x.Amount) }).ToListAsync(ct);
        PaymentStatus StatusFor(Guid id, decimal orderTotal) => PaymentTotals.StatusOf(orderTotal,
            sums.Where(x => x.OrderId == id && x.Kind == PaymentKind.Payment).Sum(x => x.Sum),
            sums.Where(x => x.OrderId == id && x.Kind == PaymentKind.Refund).Sum(x => x.Sum));
        return new PagedResult<OrderSummaryDto>(
            items.Select(o => new OrderSummaryDto(o.Id, o.Number, OrderNumbers.Format(o.Number), o.Stage, StatusFor(o.Id, o.Total), o.Email, o.Currency, o.Total, o.CreatedUtc)).ToList(),
            p, size, total);
    }

    public async Task<OrderDto?> GetAsync(Guid id, CancellationToken ct = default)
        => await WithPaymentsAsync(await db.Set<Order>().AsNoTracking().Include(o => o.Lines).AsSplitQuery().FirstOrDefaultAsync(o => o.Id == id, ct), ct);

    public async Task<OrderDto?> GetByNumberAsync(long number, CancellationToken ct = default)
        => await WithPaymentsAsync(await db.Set<Order>().AsNoTracking().Include(o => o.Lines).AsSplitQuery().FirstOrDefaultAsync(o => o.Number == number, ct), ct);

    private async Task<(decimal Paid, decimal Refunded)> PaymentSumsAsync(Guid orderId, CancellationToken ct)
    {
        var totals = await db.Set<Payment>().AsNoTracking().Where(p => p.OrderId == orderId)
            .GroupBy(p => p.Kind).Select(g => new { Kind = g.Key, Sum = g.Sum(p => p.Amount) }).ToListAsync(ct);
        return (totals.Where(t => t.Kind == PaymentKind.Payment).Sum(t => t.Sum), totals.Where(t => t.Kind == PaymentKind.Refund).Sum(t => t.Sum));
    }

    private async Task<OrderDto?> WithPaymentsAsync(Order? order, CancellationToken ct)
    {
        if (order is null) return null;
        var (paid, refunded) = await PaymentSumsAsync(order.Id, ct);
        return ToDto(order, PaymentTotals.StatusOf(order.Total, paid, refunded), paid, refunded);
    }

    public async Task<OrderDto?> CheckoutAsync(Guid basketId, CheckoutInput input, string? actor = null, CancellationToken ct = default)
    {
        ModelValidator.ValidateOrThrow(input);
        ValidateAddress(input.ShippingAddress!, nameof(CheckoutInput.ShippingAddress));
        if (input.BillingAddress is not null) ValidateAddress(input.BillingAddress, nameof(CheckoutInput.BillingAddress));

        var basket = await db.Set<Basket>().AsNoTracking().Where(b => b.Id == basketId).Select(b => new { b.Status, b.OrderId }).FirstOrDefaultAsync(ct);
        if (basket is null) return null;
        if (basket.Status == BasketStatus.CheckedOut) return await GetAsync(basket.OrderId!.Value, ct); // already done: same answer again

        if (input.CustomerId is { } customerId && await customers.GetAsync(customerId, ct) is null)
            throw new PiranhaValidationException(nameof(CheckoutInput.CustomerId), "That customer does not exist.");

        var locationId = options.Value.StockLocationId ?? StockLocations.DefaultId;
        var productIds = await db.Set<BasketLine>().AsNoTracking().Where(l => l.BasketId == basketId).Select(l => l.ProductId).ToListAsync(ct);
        if (productIds.Count == 0) throw new PiranhaConflictException("The basket is empty.");
        await stock.EnsureLevelsAsync(productIds, locationId, ct); // before the transaction: a failed insert would abort a PostgreSQL one

        var orderId = Guid.NewGuid(); // fixed outside the retried delegate so a retry after an ambiguous commit can recognise its own success
        var currency = options.Value.Currency.ToUpperInvariant();
        var strategy = db.Database.CreateExecutionStrategy();
        try
        {
            await strategy.ExecuteInTransactionAsync(
                async token =>
                {
                    db.ChangeTracker.Clear(); // every attempt starts clean

                    // 1. Claim the basket. Exactly one concurrent checkout wins; the others see 0 rows and are answered below.
                    var claimed = await db.Set<Basket>().Where(b => b.Id == basketId && b.Status == BasketStatus.Open)
                        .ExecuteUpdateAsync(s => s.SetProperty(b => b.Status, BasketStatus.CheckedOut).SetProperty(b => b.OrderId, orderId), token);
                    if (claimed == 0) throw new BasketAlreadyClaimedException();

                    // 2. Read prices, names and publication NOW, inside the transaction: nothing stored on the basket is trusted.
                    var lines = await db.Set<BasketLine>().AsNoTracking().Where(l => l.BasketId == basketId)
                        .Join(db.Set<Product>(), l => l.ProductId, p => p.Id, (l, p) => new { p.Id, p.Name, p.Sku, p.Price, l.Quantity })
                        .OrderBy(x => x.Name).ThenBy(x => x.Id)
                        .ToListAsync(token);
                    if (lines.Count == 0) throw new PiranhaConflictException("The basket is empty.");
                    var productIdsInBasket = lines.Select(x => x.Id).ToList();
                    var published = (await db.Set<ProductListing>().AsNoTracking().Where(l => l.Published && productIdsInBasket.Contains(l.ProductId))
                        .Select(l => l.ProductId).ToListAsync(token)).ToHashSet();
                    var unavailable = lines.FirstOrDefault(l => !published.Contains(l.Id));
                    if (unavailable is not null)
                        throw new PiranhaConflictException($"\"{unavailable.Name}\" is no longer available. Remove it from the basket to continue.");

                    // 2b. Shipping and tax, from the current rules, by the same calculator the quote uses.
                    var subtotal = lines.Sum(l => l.Price * l.Quantity);
                    var method = await ChooseShippingAsync(input.ShippingMethodId, token);
                    var shipping = OrderPricing.ShippingFor(method, subtotal);
                    var country = input.ShippingAddress!.CountryCode.Trim().ToUpperInvariant();
                    var (rules, defaultClass) = await TaxLookup.LoadAsync(db, token);
                    var classes = await TaxLookup.ClassesAsync(db, productIdsInBasket, token);
                    var calc = OrderPricing.Calculate(
                        lines.Select(l => (l.Price * l.Quantity, classes.GetValueOrDefault(l.Id))).ToList(), shipping, rules, defaultClass, country);

                    // 3. Next order number. The UPDATE takes the counter row's lock, which is held to commit, so numbers are never shared.
                    await db.Set<OrderCounter>().Where(c => c.Id == 1)
                        .ExecuteUpdateAsync(s => s.SetProperty(c => c.LastNumber, c => c.LastNumber + 1), token);
                    var number = await db.Set<OrderCounter>().AsNoTracking().Where(c => c.Id == 1).Select(c => c.LastNumber).FirstAsync(token);

                    var order = new Order
                    {
                        Id = orderId,
                        Number = number,
                        Stage = OrderStage.Placed,
                        CustomerId = input.CustomerId,
                        Email = input.Email.Trim(),
                        Currency = currency,
                        Notes = string.IsNullOrWhiteSpace(input.Notes) ? null : input.Notes.Trim(),
                        ShippingAddress = ToOrderAddress(input.ShippingAddress!),
                        BillingAddress = ToOrderAddress(input.BillingAddress ?? input.ShippingAddress!),
                        Lines = lines.Select((l, i) => new OrderLine
                        {
                            Position = i + 1, ProductId = l.Id, Name = l.Name, Sku = l.Sku,
                            UnitPrice = l.Price, Quantity = l.Quantity, LineTotal = l.Price * l.Quantity,
                            TaxName = calc.Lines[i].Name, TaxRate = calc.Lines[i].Rate, TaxAmount = calc.Lines[i].Amount,
                        }).ToList(),
                    };
                    order.Subtotal = subtotal;
                    order.ShippingMethodName = method?.Name;
                    order.ShippingTotal = shipping;
                    order.TaxName = calc.SummaryName;
                    order.TaxRate = calc.SummaryRate;
                    order.TaxTotal = calc.Tax;
                    order.ShippingTax = calc.Shipping.Amount;
                    order.ShippingTaxName = calc.Shipping.Name;
                    order.ShippingTaxRate = calc.Shipping.Rate;
                    order.Total = calc.Total;
                    db.Add(order);

                    // 4. Take the stock, in the same transaction. Short stock throws and rolls everything back, claim included.
                    var reference = OrderNumbers.Format(number);
                    await stock.ApplyInTransactionAsync(
                        lines.Select(l => new StockChange(l.Id, -l.Quantity, "Order placed", reference, $"\"{l.Name}\"")).ToList(),
                        locationId, allowNegative: false, actor, token);

                    await db.SaveChangesAsync(token);
                    return true;
                },
                async token => await db.Set<Order>().AsNoTracking().AnyAsync(o => o.Id == orderId, token),
                ct);
        }
        catch (BasketAlreadyClaimedException)
        {
            // Someone else checked this basket out between our check and our claim. Their order is the answer.
            db.ChangeTracker.Clear();
            var winner = await db.Set<Basket>().AsNoTracking().Where(b => b.Id == basketId).Select(b => b.OrderId).FirstOrDefaultAsync(ct);
            if (winner is { } id) return await GetAsync(id, ct);
            throw new PiranhaConflictException("This basket is being checked out. Try again in a moment.");
        }
        catch (DbUpdateException)
        {
            db.ChangeTracker.Clear();
            throw new PiranhaConflictException("The order could not be placed because something in the basket changed. Please try again.");
        }

        var placed = await GetAsync(orderId, ct);
        if (placed is not null && options.Value.SendOrderConfirmations)
        {
            // After the commit, and never able to fail the order: the order exists whether or not the email goes.
            try { await email.SendAsync(OrderEmails.Confirmation(placed), ct); }
            catch (Exception ex) { logger.LogWarning(ex, "Order confirmation for {Reference} could not be sent.", placed.Reference); }
        }
        return placed;
    }

    public async Task<OrderDto?> ChangeStageAsync(Guid id, OrderStage to, string? actor = null, CancellationToken ct = default)
    {
        var current = await db.Set<Order>().AsNoTracking().Where(o => o.Id == id).Select(o => new { o.Stage, o.Number }).FirstOrDefaultAsync(ct);
        if (current is null) return null;
        if (!OrderStages.Next(current.Stage).Contains(to))
            throw new PiranhaConflictException($"An order that is {current.Stage} cannot be moved to {to}.");

        if (to == OrderStage.Processing && options.Value.RequirePaymentBeforeProcessing)
        {
            var total = await db.Set<Order>().AsNoTracking().Where(o => o.Id == id).Select(o => o.Total).FirstAsync(ct);
            var (paid, refunded) = await PaymentSumsAsync(id, ct);
            if (PaymentTotals.StatusOf(total, paid, refunded) != PaymentStatus.Paid)
                throw new PiranhaConflictException("This order must be paid in full before it can be processed.");
        }

        var from = current.Stage;
        var locationId = options.Value.StockLocationId ?? StockLocations.DefaultId;
        var returning = to == OrderStage.Cancelled
            ? await db.Set<OrderLine>().AsNoTracking().Where(l => l.OrderId == id).OrderBy(l => l.Position)
                .Select(l => new StockChange(l.ProductId, l.Quantity, "Order cancelled", OrderNumbers.Format(current.Number), "\"" + l.Name + "\"")).ToListAsync(ct)
            : [];
        if (returning.Count > 0) await stock.EnsureLevelsAsync(returning.Select(r => r.ProductId), locationId, ct);

        var strategy = db.Database.CreateExecutionStrategy();
        try
        {
            await strategy.ExecuteInTransactionAsync(
                async token =>
                {
                    db.ChangeTracker.Clear();
                    var now = DateTimeOffset.UtcNow;

                    // Only one caller can move the order out of `from`; a second, simultaneous cancel changes 0 rows and must not restock.
                    var moved = await db.Set<Order>().Where(o => o.Id == id && o.Stage == from)
                        .ExecuteUpdateAsync(s => s.SetProperty(o => o.Stage, to).SetProperty(o => o.UpdatedUtc, (DateTimeOffset?)now), token);
                    if (moved == 0) throw new PiranhaConflictException("The order was changed by someone else. Reload it and try again.");

                    if (returning.Count > 0)
                    {
                        await stock.ApplyInTransactionAsync(returning, locationId, allowNegative: true, actor, token);
                        await db.SaveChangesAsync(token);
                    }
                    return true;
                },
                async token => await db.Set<Order>().AsNoTracking().AnyAsync(o => o.Id == id && o.Stage == to, token),
                ct);
        }
        catch (DbUpdateException)
        {
            db.ChangeTracker.Clear();
            throw new PiranhaConflictException("The order could not be updated. Please try again.");
        }
        return await GetAsync(id, ct);
    }

    /// <summary>The chosen active method; required when the store has any active method, ignored (free) when it has none.</summary>
    private async Task<ShippingMethod?> ChooseShippingAsync(Guid? id, CancellationToken ct)
    {
        var active = await db.Set<ShippingMethod>().AsNoTracking().Where(m => m.Active).ToListAsync(ct);
        if (active.Count == 0) return null;
        if (id is null) throw new PiranhaValidationException(nameof(CheckoutInput.ShippingMethodId), "Choose a shipping method.");
        return active.FirstOrDefault(m => m.Id == id)
               ?? throw new PiranhaValidationException(nameof(CheckoutInput.ShippingMethodId), "That shipping method is not available.");
    }

    private static void ValidateAddress(AddressInput address, string field)
    {
        try { ModelValidator.ValidateOrThrow(address); }
        catch (PiranhaValidationException ex)
        {
            throw new PiranhaValidationException(ex.Errors.ToDictionary(e => $"{field}.{e.Key}", e => e.Value));
        }
    }

    private static OrderAddress ToOrderAddress(AddressInput a) => new()
    {
        Recipient = Clean(a.Recipient), Line1 = a.Line1.Trim(), Line2 = Clean(a.Line2), City = a.City.Trim(),
        Region = Clean(a.Region), PostalCode = Clean(a.PostalCode), CountryCode = a.CountryCode.Trim().ToUpperInvariant(),
    };

    private static string? Clean(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();

    private static OrderDto ToDto(Order o, PaymentStatus status, decimal paid, decimal refunded) => new OrderDto(
        o.Id, o.Number, OrderNumbers.Format(o.Number), o.Stage, o.CustomerId, o.Email, o.Currency, o.Subtotal,
        o.ShippingMethodName, o.ShippingTotal, o.TaxName, o.TaxRate, o.TaxTotal, o.Total, status, paid, refunded, o.Notes,
        ToDto(o.ShippingAddress), ToDto(o.BillingAddress),
        o.Lines.OrderBy(l => l.Position).Select(l => new OrderLineDto(l.ProductId, l.Name, l.Sku, l.UnitPrice, l.Quantity, l.LineTotal, l.TaxName, l.TaxRate, l.TaxAmount)).ToList(),
        OrderStages.Next(o.Stage), o.CreatedUtc, o.UpdatedUtc, o.ShippingTax, TaxBreakdown(o));

    /// <summary>The tax summary for display: one row per named rate, from the order's own copies. Orders placed before per-line tax have one flat row.</summary>
    private static IReadOnlyList<TaxLineDto> TaxBreakdown(Order o)
    {
        var rows = o.Lines.Where(l => l.TaxName is not null).Select(l => (Name: l.TaxName!, l.TaxRate, Amount: l.TaxAmount))
            .Concat(o.ShippingTaxName is null ? [] : [(Name: o.ShippingTaxName, TaxRate: o.ShippingTaxRate, Amount: o.ShippingTax)])
            .GroupBy(r => (r.Name, r.TaxRate))
            .Select(g => new TaxLineDto(g.Key.Name, g.Key.TaxRate, g.Sum(r => r.Amount)))
            .OrderByDescending(t => t.Rate).ThenBy(t => t.Name).ToList();
        if (rows.Count == 0 && o.TaxTotal > 0 && o.TaxName is not null) rows.Add(new TaxLineDto(o.TaxName, o.TaxRate, o.TaxTotal));
        return rows;
    }

    private static OrderAddressDto? ToDto(OrderAddress? a) =>
        a is null ? null : new OrderAddressDto(a.Recipient, a.Line1, a.Line2, a.City, a.Region, a.PostalCode, a.CountryCode);

    private sealed class BasketAlreadyClaimedException : Exception;
}
