using PiranhaCommerce.Core.Events;
using PiranhaCommerce.Core.Audit;
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

internal sealed partial class OrderService(
    PiranhaCommerceDbContext db, IStockService stock, ICustomerService customers, IOptions<EcommerceOptions> options,
    IEmailSender email, OrderMailer mailer, ILogger<OrderService> logger, IAuditTrail? trail = null, IEventPublisher? events = null) : IOrderService
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
        => await WithPaymentsAsync(await db.Set<Order>().AsNoTracking().Include(o => o.Lines).Include(o => o.Discounts).AsSplitQuery()
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
        => await WithPaymentsAsync(await db.Set<Order>().AsNoTracking().Include(o => o.Lines).Include(o => o.Discounts).AsSplitQuery().FirstOrDefaultAsync(o => o.Id == id, ct), ct);

    public async Task<OrderDto?> GetByNumberAsync(long number, CancellationToken ct = default)
        => await WithPaymentsAsync(await db.Set<Order>().AsNoTracking().Include(o => o.Lines).Include(o => o.Discounts).AsSplitQuery().FirstOrDefaultAsync(o => o.Number == number, ct), ct);

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
        var shipments = (await ShipmentQueries.ForOrderAsync(db, order.Id, ct)).Select(s => s.Summary()).ToList();
        return ToDto(order, PaymentTotals.StatusOf(order.Total, paid, refunded), paid, refunded, shipments);
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
            throw new PiranhaCommerceValidationException(nameof(CheckoutInput.CustomerId), "That customer does not exist.");

        var locationId = options.Value.StockLocationId ?? StockLocations.DefaultId;
        var productIds = await db.Set<BasketLine>().AsNoTracking().Where(l => l.BasketId == basketId).Select(l => l.ProductId).ToListAsync(ct);
        if (productIds.Count == 0) throw new PiranhaCommerceConflictException("The basket is empty.");
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
                    if (lines.Count == 0) throw new PiranhaCommerceConflictException("The basket is empty.");
                    // The buyer's group prices, decided here in the transaction exactly as the basket and quote show them.
                    var buyer = input.CustomerId ?? await db.Set<Basket>().AsNoTracking().Where(b => b.Id == basketId).Select(b => b.CustomerId).FirstAsync(token);
                    var lineIds = lines.Select(l => l.Id).ToList();
                    var limits = await db.Set<ProductListing>().AsNoTracking().Where(l => lineIds.Contains(l.ProductId)).Select(l => new { l.ProductId, l.MinOrderQuantity, l.MaxOrderQuantity }).ToDictionaryAsync(l => l.ProductId, token);
                    foreach (var l in lines)
                        if (limits.TryGetValue(l.Id, out var lim) && OrderQuantityRules.Problem(l.Name, l.Quantity, lim.MinOrderQuantity, lim.MaxOrderQuantity) is { } outOfRange)
                            throw new PiranhaCommerceConflictException(outOfRange + " Change the quantity in the basket to continue.");
                    var pay = await GroupPricing.EffectiveAsync(db, buyer, lines.Select(l => (l.Id, l.Price)).ToList(), token, lines.ToDictionary(l => l.Id, l => l.Quantity));
                    lines = lines.Select(l => new { l.Id, l.Name, l.Sku, Price = pay[l.Id], l.Quantity }).ToList();
                    var productIdsInBasket = lines.Select(x => x.Id).ToList();
                    var published = (await db.Set<ProductListing>().AsNoTracking().Where(l => l.Published && productIdsInBasket.Contains(l.ProductId))
                        .Select(l => l.ProductId).ToListAsync(token)).ToHashSet();
                    var unavailable = lines.FirstOrDefault(l => !published.Contains(l.Id));
                    if (unavailable is not null)
                        throw new PiranhaCommerceConflictException($"\"{unavailable.Name}\" is no longer available. Remove it from the basket to continue.");

                    // 2b. Shipping and tax, from the current rules, by the same calculator the quote uses.
                    var subtotal = lines.Sum(l => l.Price * l.Quantity);
                    var country = input.ShippingAddress!.CountryCode.Trim().ToUpperInvariant();
                    var method = await ChooseShippingAsync(input.ShippingMethodId, token);
                    decimal? shippingPrice = null; // by weight and destination when the method has rates
                    if (method is not null)
                    {
                        var weight = await ShippingPricing.WeightAsync(db, lines.Select(l => (l.Id, l.Quantity)).ToList(), token);
                        shippingPrice = await ShippingPricing.PriceAsync(db, method, country, input.ShippingAddress.PostalCode, weight, token)
                            ?? throw new PiranhaCommerceValidationException(nameof(CheckoutInput.ShippingMethodId), "That shipping method is not available for this destination and basket.");
                    }

                    // 2a. The promo code, if the basket holds one: checked again NOW, and one use spent with a single conditional
                    // update, so a limited code cannot be over-used by simultaneous orders. A code that fails stops the order (409)
                    // rather than quietly charging full price.
                    Promotion? promo = null;
                    var discount = 0m;
                    var plan = DiscountPlan.None(lines.Count);
                    var held = await db.Set<Basket>().AsNoTracking().Where(b => b.Id == basketId).Select(b => new { b.PromotionCode, b.VoucherId, b.CustomerId }).FirstAsync(token);
                    var promoCode = held.PromotionCode;
                    plan = await DiscountEngine.PlanAsync(db, input.CustomerId ?? held.CustomerId, promoCode,
                        lines.Select(l => new EngineLine(l.Id, l.Quantity, l.Price, l.Price * l.Quantity)).ToList(), DateTimeOffset.UtcNow, token);
                    if (plan.CodeProblem is { } codeProblem)
                        throw new PiranhaCommerceConflictException($"The discount code on this basket cannot be used: {codeProblem} Remove it to continue.");
                    foreach (var applied in plan.Applied)
                    {
                        // One use spent per discount that applied, with a conditional update so a limited discount cannot be over-used by simultaneous orders.
                        var spent = await db.Set<DiscountRule>()
                            .Where(r => r.Id == applied.RuleId && r.Active && (r.MaxRedemptions == null || r.Redeemed < r.MaxRedemptions))
                            .ExecuteUpdateAsync(s => s.SetProperty(r => r.Redeemed, r => r.Redeemed + 1), token);
                        if (spent == 0) throw new PiranhaCommerceConflictException($"The discount \"{applied.Name}\" has been used up. Try again.");
                    }
                    if (promoCode is not null && !plan.CodeIsDiscount)
                    {
                        promo = await db.Set<Promotion>().AsNoTracking().FirstOrDefaultAsync(p => p.Code == promoCode, token);
                        if (PromotionRules.Problem(promo, subtotal, DateTimeOffset.UtcNow, await PromotionEligibility.Async(db, promo, input.CustomerId ?? held.CustomerId, token)) is { } problem)
                            throw new PiranhaCommerceConflictException($"The promotion code on this basket cannot be used: {problem} Remove it to continue.");
                        var used = await db.Set<Promotion>()
                            .Where(p => p.Id == promo!.Id && p.Active && (p.MaxRedemptions == null || p.Redeemed < p.MaxRedemptions))
                            .ExecuteUpdateAsync(s => s.SetProperty(p => p.Redeemed, p => p.Redeemed + 1), token);
                        if (used == 0) throw new PiranhaCommerceConflictException("That promotion code has been used up. Remove it to continue.");
                        discount = PromotionRules.Discount(promo!, subtotal - plan.Total);
                    }

                    var (rules, defaultClass) = await TaxLookup.LoadAsync(db, token);
                    var classes = await TaxLookup.ClassesAsync(db, productIdsInBasket, token);
                    var calc = OrderPricing.Calculate(
                        lines.Select(l => (l.Price * l.Quantity, classes.GetValueOrDefault(l.Id))).ToList(), discount, method, rules, defaultClass, country, shippingPrice, plan.LineDiscounts, plan.FreeShipping);

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
                            DiscountAmount = calc.LineDiscounts[i],
                            TaxName = calc.Lines[i].Name, TaxRate = calc.Lines[i].Rate, TaxAmount = calc.Lines[i].Amount,
                        }).ToList(),
                    };
                    order.Subtotal = subtotal;
                    order.ShippingMethodName = method?.Name;
                    order.ShippingTotal = calc.ShippingTotal;
                    order.PromotionId = promo?.Id;
                    order.PromotionCode = promo?.Code;
                    order.DiscountTotal = calc.Discount;
                    foreach (var applied in plan.Applied)
                        order.Discounts.Add(new OrderDiscount { OrderId = orderId, RuleId = applied.RuleId, Name = applied.Name, Code = applied.Code, Amount = applied.Amount, FreeShipping = applied.FreeShipping });
                    order.TaxName = calc.SummaryName;
                    order.TaxRate = calc.SummaryRate;
                    order.TaxTotal = calc.Tax;
                    order.ShippingTax = calc.Shipping.Amount;
                    order.ShippingTaxName = calc.Shipping.Name;
                    order.ShippingTaxRate = calc.Shipping.Rate;
                    order.Total = calc.Total;
                    db.Add(order);
                    events?.Publish(EcommerceEvents.OrderPlaced, new
                    {
                        orderId, number, reference = OrderNumbers.Format(number), stage = order.Stage, currency, customerId = order.CustomerId, lineCount = lines.Count,
                        subtotal = order.Subtotal, shippingTotal = order.ShippingTotal, discountTotal = order.DiscountTotal, taxTotal = order.TaxTotal, total = order.Total,
                    });

                    // 4. Take the stock, in the same transaction. Short stock throws and rolls everything back, claim included.
                    var reference = OrderNumbers.Format(number);
                    await stock.ApplyInTransactionAsync(
                        lines.Select(l => new StockChange(l.Id, -l.Quantity, "Order placed", reference, $"\"{l.Name}\"")).ToList(),
                        locationId, allowNegative: false, actor, token);

                    // 5. Pay with the gift voucher, if the basket holds one. It is a way of paying: it comes AFTER the total (and the
                    // tax) is known and changes neither. One conditional update takes the money, so a voucher spent by two orders at
                    // once pays only one of them in full and never goes below zero. The voucher's ledger and the order's payment
                    // record are written in this same transaction.
                    if (held.VoucherId is { } voucherId)
                    {
                        var voucher = await db.Set<GiftVoucher>().AsNoTracking().FirstOrDefaultAsync(v => v.Id == voucherId, token);
                        var at = DateTimeOffset.UtcNow;
                        if (VoucherRules.Problem(voucher, currency, at) is not null)
                            throw new PiranhaCommerceConflictException("The gift voucher on this basket cannot be used. Remove it to continue.");
                        var pays = Math.Min(voucher!.Balance, calc.Total);
                        if (pays > 0)
                        {
                            var spent = await db.Set<GiftVoucher>()
                                .Where(v => v.Id == voucherId && v.Active && v.Balance >= pays && v.Currency == currency && (v.ExpiresUtc == null || v.ExpiresUtc > at))
                                .ExecuteUpdateAsync(s => s.SetProperty(v => v.Balance, v => v.Balance - pays), token);
                            if (spent == 0) throw new PiranhaCommerceConflictException("The gift voucher balance changed. Check the basket and try again.");
                            db.Add(new VoucherEntry { VoucherId = voucherId, Amount = -pays, Kind = VoucherEntryKinds.Redeemed, OrderId = orderId, Reference = reference, Actor = actor, CreatedUtc = at });
                            events?.Publish(EcommerceEvents.PaymentReceived, new { orderId, number, reference = OrderNumbers.Format(number), kind = PaymentKind.Payment, provider = "voucher", amount = pays, currency });
                            db.Add(new Payment
                            {
                                OrderId = orderId, Kind = PaymentKind.Payment, Provider = "voucher", Amount = pays, Currency = currency,
                                Reference = $"VCH-{voucher.CodeLast4}-{reference}", // unique per use: the same voucher can pay many orders
                                Note = $"Gift voucher ending {voucher.CodeLast4}", Actor = actor, CreatedUtc = at,
                            });
                            order.VoucherId = voucherId;
                            order.VoucherPaid = pays;
                        }
                    }

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
            throw new PiranhaCommerceConflictException("This basket is being checked out. Try again in a moment.");
        }
        catch (DbUpdateException)
        {
            db.ChangeTracker.Clear();
            throw new PiranhaCommerceConflictException("The order could not be placed because something in the basket changed. Please try again.");
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

    public Task<OrderDto?> ChangeStageAsync(Guid id, OrderStage to, string? actor = null, CancellationToken ct = default) =>
        ChangeStageCoreAsync(id, to, actor, onlyIfUnpaid: false, ct);

    /// <param name="onlyIfUnpaid">Used by expiry: the order must still be at Placed and have no payment at all, checked after its row is held.</param>
    private async Task<OrderDto?> ChangeStageCoreAsync(Guid id, OrderStage to, string? actor, bool onlyIfUnpaid, CancellationToken ct)
    {
        var current = await db.Set<Order>().AsNoTracking().Where(o => o.Id == id).Select(o => new { o.Stage, o.Number, o.Currency, o.PromotionId, o.VoucherId, o.VoucherPaid }).FirstOrDefaultAsync(ct);
        if (current is null) return null;
        if (!OrderStages.Next(current.Stage).Contains(to))
            throw new PiranhaCommerceConflictException($"An order that is {current.Stage} cannot be moved to {to}.");
        if (onlyIfUnpaid && current.Stage != OrderStage.Placed) throw new PiranhaCommerceConflictException("The order has moved on.");

        if (to == OrderStage.Processing && options.Value.RequirePaymentBeforeProcessing)
        {
            var total = await db.Set<Order>().AsNoTracking().Where(o => o.Id == id).Select(o => o.Total).FirstAsync(ct);
            var (paid, refunded) = await PaymentSumsAsync(id, ct);
            if (PaymentTotals.StatusOf(total, paid, refunded) != PaymentStatus.Paid)
                throw new PiranhaCommerceConflictException("This order must be paid in full before it can be processed.");
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
                    if (moved == 0) throw new PiranhaCommerceConflictException("The order was changed by someone else. Reload it and try again.");
                    if (trail is not null) await trail.RecordUpdateAsync("Order", id.ToString(), current.Number.ToString(), [new AuditChange("Stage", from.ToString(), to.ToString())], token);
                    if (events is not null)
                    {
                        events.Publish(EcommerceEvents.OrderStageChanged, new { orderId = id, number = current.Number, reference = OrderNumbers.Format(current.Number), previousStage = from, stage = to });
                        await db.SaveChangesAsync(token);
                    }

                    // A payment is booked under this same row lock, so it is either seen here (and this rolls back) or is refused as a payment on a cancelled order.
                    if (onlyIfUnpaid && await db.Set<Payment>().AsNoTracking().AnyAsync(p => p.OrderId == id, token))
                        throw new PiranhaCommerceConflictException("The order has been paid.");

                    // Read AFTER the order row is held: a parcel being recorded at the same moment has either finished (and is seen)
                    // or is waiting behind this and will find the order no longer processing.
                    if (to == OrderStage.Cancelled && await db.Set<OrderLine>().AsNoTracking().AnyAsync(l => l.OrderId == id && l.QuantityShipped > 0, token))
                        throw new PiranhaCommerceConflictException("Part of this order has already been sent, so it cannot be cancelled. Record a return for what comes back.");

                    // Completing by hand says everything has gone: record what was not yet recorded as one shipment, so "completed"
                    // always means every line is shipped (and returns can then be recorded against it).
                    if (to == OrderStage.Completed)
                    {
                        var unsent = await db.Set<OrderLine>().AsNoTracking().Where(l => l.OrderId == id && l.QuantityShipped < l.Quantity)
                            .Select(l => new { l.Id, Remaining = l.Quantity - l.QuantityShipped }).ToListAsync(token);
                        if (unsent.Count > 0)
                        {
                            await db.Set<OrderLine>().Where(l => l.OrderId == id && l.QuantityShipped < l.Quantity)
                                .ExecuteUpdateAsync(s => s.SetProperty(l => l.QuantityShipped, l => l.Quantity), token);
                            var shipmentId = Guid.NewGuid();
                            events?.Publish(EcommerceEvents.ShipmentCreated, new { shipmentId, orderId = id, number = current.Number, reference = OrderNumbers.Format(current.Number), lineCount = unsent.Count });
                            db.Add(new Shipment
                            {
                                Id = shipmentId, OrderId = id, ShippedUtc = now, Actor = actor,
                                Lines = unsent.Select(u => new ShipmentLine { ShipmentId = shipmentId, OrderLineId = u.Id, Quantity = u.Remaining }).ToList(),
                            });
                            await db.SaveChangesAsync(token);
                        }
                    }

                    // A cancelled order gives its promo code use back (once: only the caller that moved the stage gets here).
                    if (to == OrderStage.Cancelled && current.PromotionId is { } promoId)
                        await db.Set<Promotion>().Where(p => p.Id == promoId && p.Redeemed > 0)
                            .ExecuteUpdateAsync(s => s.SetProperty(p => p.Redeemed, p => p.Redeemed - 1), token);

                    // The discount rules it used get their use back too.
                    if (to == OrderStage.Cancelled)
                    {
                        var used = await db.Set<OrderDiscount>().AsNoTracking().Where(d => d.OrderId == id && d.RuleId != null).Select(d => d.RuleId!.Value).ToListAsync(token);
                        if (used.Count > 0)
                            await db.Set<DiscountRule>().Where(r => used.Contains(r.Id) && r.Redeemed > 0)
                                .ExecuteUpdateAsync(s => s.SetProperty(r => r.Redeemed, r => r.Redeemed - 1), token);
                    }

                    // Money paid with a gift voucher goes back onto the voucher: only what the order still holds (a manual refund already
                    // given is not given twice), in the voucher's ledger and as a refund on the order, in this same transaction.
                    if (to == OrderStage.Cancelled && current.VoucherId is { } voucherId && current.VoucherPaid > 0)
                    {
                        var (paidSoFar, refundedSoFar) = await PaymentSumsAsync(id, token);
                        var alreadyReturned = await db.Set<Payment>().AsNoTracking().Where(p => p.OrderId == id && p.Kind == PaymentKind.Refund && p.Provider == "voucher").SumAsync(p => (decimal?)p.Amount, token) ?? 0m;
                        var credit = Math.Min(current.VoucherPaid - alreadyReturned, paidSoFar - refundedSoFar);
                        if (credit > 0)
                        {
                            var reference = OrderNumbers.Format(current.Number);
                            var last4 = await db.Set<GiftVoucher>().AsNoTracking().Where(v => v.Id == voucherId).Select(v => v.CodeLast4).FirstAsync(token);
                            var at = DateTimeOffset.UtcNow;
                            await db.Set<GiftVoucher>().Where(v => v.Id == voucherId)
                                .ExecuteUpdateAsync(s => s.SetProperty(v => v.Balance, v => v.Balance + credit), token);
                            db.Add(new VoucherEntry { VoucherId = voucherId, Amount = credit, Kind = VoucherEntryKinds.Returned, OrderId = id, Reference = reference, Actor = actor, CreatedUtc = at });
                            events?.Publish(EcommerceEvents.PaymentRefunded, new { orderId = id, number = current.Number, reference = OrderNumbers.Format(current.Number), kind = PaymentKind.Refund, provider = "voucher", amount = credit, currency = current.Currency });
                            db.Add(new Payment
                            {
                                OrderId = id, Kind = PaymentKind.Refund, Provider = "voucher", Amount = credit,
                                Currency = await db.Set<Order>().AsNoTracking().Where(o => o.Id == id).Select(o => o.Currency).FirstAsync(token),
                                Reference = $"VCH-{last4}-{reference}-RETURN", Note = $"Returned to gift voucher ending {last4}", Actor = actor, CreatedUtc = at,
                            });
                            await db.SaveChangesAsync(token);
                        }
                    }

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
            throw new PiranhaCommerceConflictException("The order could not be updated. Please try again.");
        }
        if (to == OrderStage.Cancelled) await mailer.CancelledAsync(id, ct); // only the caller that moved the stage gets here
        return await GetAsync(id, ct);
    }

    /// <summary>The chosen active method; required when the store has any active method, ignored (free) when it has none.</summary>
    private async Task<ShippingMethod?> ChooseShippingAsync(Guid? id, CancellationToken ct)
    {
        var active = await db.Set<ShippingMethod>().AsNoTracking().Where(m => m.Active).ToListAsync(ct);
        if (active.Count == 0) return null;
        if (id is null) throw new PiranhaCommerceValidationException(nameof(CheckoutInput.ShippingMethodId), "Choose a shipping method.");
        return active.FirstOrDefault(m => m.Id == id)
               ?? throw new PiranhaCommerceValidationException(nameof(CheckoutInput.ShippingMethodId), "That shipping method is not available.");
    }

    private static void ValidateAddress(AddressInput address, string field)
    {
        try { ModelValidator.ValidateOrThrow(address); }
        catch (PiranhaCommerceValidationException ex)
        {
            throw new PiranhaCommerceValidationException(ex.Errors.ToDictionary(e => $"{field}.{e.Key}", e => e.Value));
        }
    }

    private static OrderAddress ToOrderAddress(AddressInput a) => new()
    {
        Recipient = Clean(a.Recipient), Line1 = a.Line1.Trim(), Line2 = Clean(a.Line2), City = a.City.Trim(),
        Region = Clean(a.Region), PostalCode = Clean(a.PostalCode), CountryCode = a.CountryCode.Trim().ToUpperInvariant(),
    };

    private static string? Clean(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();

    private static OrderDto ToDto(Order o, PaymentStatus status, decimal paid, decimal refunded, IReadOnlyList<ShipmentSummaryDto> shipments) => new OrderDto(
        o.Id, o.Number, OrderNumbers.Format(o.Number), o.Stage, o.CustomerId, o.Email, o.Currency, o.Subtotal,
        o.ShippingMethodName, o.ShippingTotal, o.TaxName, o.TaxRate, o.TaxTotal, o.Total, status, paid, refunded, o.Notes,
        ToDto(o.ShippingAddress), ToDto(o.BillingAddress),
        o.Lines.OrderBy(l => l.Position).Select(l => new OrderLineDto(l.ProductId, l.Name, l.Sku, l.UnitPrice, l.Quantity, l.LineTotal, l.TaxName, l.TaxRate, l.TaxAmount, l.DiscountAmount, l.Id, l.QuantityShipped, l.QuantityReturned)).ToList(),
        // Once anything has left, cancelling is no longer offered: what was sent comes back as a return instead.
        OrderStages.Next(o.Stage).Where(s => s != OrderStage.Cancelled || o.Lines.All(l => l.QuantityShipped == 0)).ToList(),
        o.CreatedUtc, o.UpdatedUtc, o.ShippingTax, TaxBreakdown(o), o.DiscountTotal, o.PromotionCode, shipments,
        o.Discounts.OrderBy(d => d.Name).Select(d => new AppliedDiscountDto(d.Name, d.Code, d.Amount, d.FreeShipping)).ToList());

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
