using PiranhaCommerce.Core.Events;
using PiranhaCommerce.Core.Audit;
using System.ComponentModel.DataAnnotations;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using PiranhaCommerce.Catalog;
using PiranhaCommerce.Core.Domain;
using PiranhaCommerce.Core.Validation;
using PiranhaCommerce.Data;

namespace PiranhaCommerce.Ecommerce;

/// <summary>
/// One parcel sent to the buyer: which lines (and how many of each) left, when, and how to track it. Shipments are a record, never
/// edited. They do not touch stock: the stock was taken from the shelf when the order was placed.
/// </summary>
public class Shipment : Entity
{
    public Guid OrderId { get; set; }
    public DateTimeOffset ShippedUtc { get; set; }
    public string? Carrier { get; set; }
    public string? TrackingNumber { get; set; }
    /// <summary>An http(s) address the buyer can follow. Checked on the way in, because it is shown to the buyer as a link.</summary>
    public string? TrackingUrl { get; set; }
    public string? Actor { get; set; }
    public List<ShipmentLine> Lines { get; set; } = [];
}

public class ShipmentLine : Entity
{
    public Guid ShipmentId { get; set; }
    public Guid OrderLineId { get; set; }
    public int Quantity { get; set; }
}

/// <summary>
/// Goods that came back from the buyer, recorded when they arrive. Putting them back on the shelf is a choice per line (a damaged
/// item is written off, not restocked). Money is separate: <see cref="SuggestedRefund"/> is only a suggestion; the refund is its own
/// payment record, made by staff or sent through the payment provider.
/// </summary>
public class OrderReturn : Entity
{
    public Guid OrderId { get; set; }
    public DateTimeOffset CreatedUtc { get; set; }
    public required string Reason { get; set; }
    public string? Note { get; set; }
    public string? Actor { get; set; }
    /// <summary>Where restocked goods went back. Recorded so that locations can matter later.</summary>
    public Guid LocationId { get; set; }
    public decimal SuggestedRefund { get; set; }
    /// <summary>True when this return also covers the delivery charge (and its tax). Only one return per order may.</summary>
    public bool ShippingIncluded { get; set; }
    public List<OrderReturnLine> Lines { get; set; } = [];
}

public class OrderReturnLine : Entity
{
    public Guid OrderReturnId { get; set; }
    public Guid OrderLineId { get; set; }
    public int Quantity { get; set; }
    public bool Restocked { get; set; }
}

// ---- Inputs and DTOs -------------------------------------------------------------------------------------------------------

public sealed class FulfilmentLineInput
{
    public Guid OrderLineId { get; set; }

    [Range(1, OrderLineQuantity.Max)]
    public int Quantity { get; set; }
}

public static class OrderLineQuantity
{
    public const int Max = 100_000;
}

public sealed class ShipmentInput : IValidatableObject
{
    /// <summary>The lines in this parcel. Leave out (or empty) to send everything not yet sent.</summary>
    public List<FulfilmentLineInput>? Lines { get; set; }

    /// <summary>One of the shop's carriers (<c>/shipping-carriers</c>): its name is used as the carrier, and the tracking link is built from its template and the tracking number unless <see cref="TrackingUrl"/> is given.</summary>
    public Guid? CarrierId { get; set; }

    [StringLength(100)] public string? Carrier { get; set; }
    [StringLength(100)] public string? TrackingNumber { get; set; }
    [StringLength(500)] public string? TrackingUrl { get; set; }

    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        if (!string.IsNullOrWhiteSpace(TrackingUrl)
            && !(Uri.TryCreate(TrackingUrl.Trim(), UriKind.Absolute, out var uri) && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps)))
            yield return new ValidationResult("The tracking link must be a web address starting with http:// or https://.", [nameof(TrackingUrl)]);
        if (Lines is { Count: > 200 })
            yield return new ValidationResult("Too many lines.", [nameof(Lines)]);
        else if (Lines is not null && Lines.Any(l => l is null || l.Quantity < 1 || l.Quantity > OrderLineQuantity.Max))
            yield return new ValidationResult($"Each quantity must be between 1 and {OrderLineQuantity.Max}.", [nameof(Lines)]); // nested lines are not checked by their own attributes, and a negative one would un-send goods
    }
}

public sealed class ReturnLineInput
{
    public Guid OrderLineId { get; set; }

    [Range(1, OrderLineQuantity.Max)]
    public int Quantity { get; set; }

    /// <summary>Put the goods back on the shelf (default). Untick for damaged or unsellable goods.</summary>
    public bool Restock { get; set; } = true;
}

public sealed class ReturnInput : IValidatableObject
{
    [Required, StringLength(200, MinimumLength = 1)]
    public string Reason { get; set; } = "";

    [StringLength(1000)] public string? Note { get; set; }

    public List<ReturnLineInput> Lines { get; set; } = [];

    /// <summary>The approved buyer request these goods answer, if any. It is marked Received. Must belong to this order and be Approved.</summary>
    public Guid? ReturnRequestId { get; set; }

    /// <summary>Include the delivery charge in the suggested refund. Allowed once per order.</summary>
    public bool IncludeShipping { get; set; }

    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        if (Lines is null || Lines.Count == 0) yield return new ValidationResult("Choose at least one item that came back.", [nameof(Lines)]);
        else if (Lines.Count > 200) yield return new ValidationResult("Too many lines.", [nameof(Lines)]);
        else if (Lines.Any(l => l is null || l.Quantity < 1 || l.Quantity > OrderLineQuantity.Max))
            yield return new ValidationResult($"Each quantity must be between 1 and {OrderLineQuantity.Max}.", [nameof(Lines)]); // a negative one would un-return goods and take stock off the shelf
    }
}

public sealed record ShipmentLineDto(Guid OrderLineId, string Name, string? Sku, int Quantity);

/// <summary>What a buyer may see of a shipment: no member of staff.</summary>
public sealed record ShipmentSummaryDto(Guid Id, DateTimeOffset ShippedUtc, string? Carrier, string? TrackingNumber, string? TrackingUrl, IReadOnlyList<ShipmentLineDto> Lines);

public sealed record ShipmentDto(
    Guid Id, Guid OrderId, DateTimeOffset ShippedUtc, string? Carrier, string? TrackingNumber, string? TrackingUrl, string? Actor, IReadOnlyList<ShipmentLineDto> Lines);

public sealed record ReturnLineDto(Guid OrderLineId, string Name, string? Sku, int Quantity, bool Restocked);

public sealed record ReturnDto(
    Guid Id, Guid OrderId, DateTimeOffset CreatedUtc, string Reason, string? Note, string? Actor, decimal SuggestedRefund, bool ShippingIncluded,
    IReadOnlyList<ReturnLineDto> Lines);

// ---- Refund suggestion -----------------------------------------------------------------------------------------------------

public static class RefundSuggestion
{
    /// <summary>
    /// What the buyer paid for the whole line: its price, less its share of any discount, plus its tax. Returned quantities are
    /// worked out from the CUMULATIVE share, so returning one of three in three separate returns refunds the line exactly, with no
    /// penny lost or gained to rounding.
    /// </summary>
    public static decimal ForLine(decimal lineTotal, decimal discount, decimal tax, int ordered, int alreadyReturned, int returningNow)
    {
        if (ordered <= 0 || returningNow <= 0) return 0m;
        var net = lineTotal - discount + tax;
        decimal Share(int n) => decimal.Round(net * n / ordered, 2, MidpointRounding.AwayFromZero);
        return Share(alreadyReturned + returningNow) - Share(alreadyReturned);
    }
}

// ---- Service ---------------------------------------------------------------------------------------------------------------

public interface IFulfilmentService
{
    /// <summary>Oldest first. Null when the order does not exist.</summary>
    Task<IReadOnlyList<ShipmentDto>?> ListShipmentsAsync(Guid orderId, CancellationToken ct = default);

    /// <summary>
    /// Records a parcel leaving. Only an order being processed can ship. When the last line has gone the order becomes completed, in
    /// the same transaction. Null when the order does not exist; conflict when it cannot be shipped or would send more than was bought.
    /// </summary>
    Task<ShipmentDto?> ShipAsync(Guid orderId, ShipmentInput input, string? actor = null, CancellationToken ct = default);

    /// <summary>Oldest first. Null when the order does not exist.</summary>
    Task<IReadOnlyList<ReturnDto>?> ListReturnsAsync(Guid orderId, CancellationToken ct = default);

    /// <summary>
    /// Records goods that came back, putting the chosen lines back on the shelf, in one transaction. Only what was shipped (and not
    /// already returned) can come back. Does not move any money: it suggests a refund, which staff then make.
    /// </summary>
    Task<ReturnDto?> RecordReturnAsync(Guid orderId, ReturnInput input, string? actor = null, CancellationToken ct = default);
}

internal static class ShipmentQueries
{
    public static async Task<List<ShipmentDto>> ForOrderAsync(PiranhaCommerceDbContext db, Guid orderId, CancellationToken ct)
    {
        var shipments = await db.Set<Shipment>().AsNoTracking().Where(s => s.OrderId == orderId).OrderBy(s => s.ShippedUtc).ThenBy(s => s.Id).ToListAsync(ct);
        if (shipments.Count == 0) return [];
        var ids = shipments.Select(s => s.Id).ToList();
        var lines = await db.Set<ShipmentLine>().AsNoTracking().Where(l => ids.Contains(l.ShipmentId))
            .Join(db.Set<OrderLine>(), sl => sl.OrderLineId, ol => ol.Id, (sl, ol) => new { sl.ShipmentId, sl.OrderLineId, ol.Name, ol.Sku, sl.Quantity, ol.Position })
            .OrderBy(x => x.Position).ToListAsync(ct);
        return shipments.Select(s => new ShipmentDto(s.Id, s.OrderId, s.ShippedUtc, s.Carrier, s.TrackingNumber, s.TrackingUrl, s.Actor,
            lines.Where(l => l.ShipmentId == s.Id).Select(l => new ShipmentLineDto(l.OrderLineId, l.Name, l.Sku, l.Quantity)).ToList())).ToList();
    }

    public static ShipmentSummaryDto Summary(this ShipmentDto s) => new(s.Id, s.ShippedUtc, s.Carrier, s.TrackingNumber, s.TrackingUrl, s.Lines);
}

internal sealed class FulfilmentService(PiranhaCommerceDbContext db, IStockService stock, IOptions<EcommerceOptions> options, OrderMailer mailer, IAuditTrail? trail = null, IEventPublisher? events = null) : IFulfilmentService
{
    public async Task<IReadOnlyList<ShipmentDto>?> ListShipmentsAsync(Guid orderId, CancellationToken ct = default)
    {
        if (!await db.Set<Order>().AnyAsync(o => o.Id == orderId, ct)) return null;
        return await ShipmentQueries.ForOrderAsync(db, orderId, ct);
    }

    public async Task<ShipmentDto?> ShipAsync(Guid orderId, ShipmentInput input, string? actor = null, CancellationToken ct = default)
    {
        ModelValidator.ValidateOrThrow(input);

        var stage = await db.Set<Order>().AsNoTracking().Where(o => o.Id == orderId).Select(o => (OrderStage?)o.Stage).FirstOrDefaultAsync(ct);
        if (stage is null) return null;
        if (stage != OrderStage.Processing)
            throw new PiranhaCommerceConflictException(stage == OrderStage.Placed
                ? "Move the order to Processing before sending it."
                : $"An order that is {stage} cannot be shipped.");

        // A chosen carrier gives the name, and the tracking link when the tracking number is known; the parcel keeps these as copies.
        if (input.CarrierId is { } carrierId)
        {
            var carrier = await db.Set<ShippingCarrier>().AsNoTracking().FirstOrDefaultAsync(c => c.Id == carrierId && c.Active, ct)
                ?? throw new PiranhaCommerceValidationException(nameof(ShipmentInput.CarrierId), "Choose one of the shop's carriers that is switched on.");
            input.Carrier = carrier.Name;
            if (string.IsNullOrWhiteSpace(input.TrackingUrl) && ShippingCarriers.TrackingUrl(carrier.TrackingUrlTemplate, input.TrackingNumber) is { Length: <= 500 } link) input.TrackingUrl = link;
        }

        var shipmentId = Guid.NewGuid(); // fixed outside the retried delegate so a retry can recognise its own success
        var shipment = new ShipmentBuilder(shipmentId, orderId, input, actor);
        var strategy = db.Database.CreateExecutionStrategy();
        try
        {
            await strategy.ExecuteInTransactionAsync(
                async token =>
                {
                    db.ChangeTracker.Clear();
                    var now = DateTimeOffset.UtcNow;

                    // The order row first, and only while it is still being processed. A simultaneous cancel takes the same lock
                    // first (or after), so a parcel can never leave from an order that has just been cancelled, and the reverse.
                    var held = await db.Set<Order>().Where(o => o.Id == orderId && o.Stage == OrderStage.Processing)
                        .ExecuteUpdateAsync(s => s.SetProperty(o => o.UpdatedUtc, (DateTimeOffset?)now), token);
                    if (held == 0) throw new PiranhaCommerceConflictException("The order was changed by someone else. Reload it and try again.");

                    var lines = await db.Set<OrderLine>().AsNoTracking().Where(l => l.OrderId == orderId).OrderBy(l => l.Position)
                        .Select(l => new { l.Id, l.Name, l.Quantity, l.QuantityShipped }).ToListAsync(token);
                    var wanted = shipment.Resolve(lines.Select(l => (l.Id, l.Quantity - l.QuantityShipped)).ToList());

                    foreach (var (lineId, quantity) in wanted)
                    {
                        // The guard: only while it keeps shipped within what was bought, so two people cannot both send the last one.
                        var done = await db.Set<OrderLine>().Where(l => l.Id == lineId && l.OrderId == orderId && l.QuantityShipped + quantity <= l.Quantity)
                            .ExecuteUpdateAsync(s => s.SetProperty(l => l.QuantityShipped, l => l.QuantityShipped + quantity), token);
                        if (done == 0) throw new PiranhaCommerceConflictException("That is more than is left to send. Reload the order and check what has already gone.");
                    }

                    var parcel = shipment.ToEntity(wanted, now);
                    db.Add(parcel);
                    var orderNumber = await db.Set<Order>().AsNoTracking().Where(o => o.Id == orderId).Select(o => o.Number).FirstAsync(token);
                    events?.Publish(EcommerceEvents.ShipmentCreated, new { shipmentId = parcel.Id, orderId, number = orderNumber, reference = OrderNumbers.Format(orderNumber), lineCount = wanted.Count, carrier = parcel.Carrier, trackingNumber = parcel.TrackingNumber, trackingUrl = parcel.TrackingUrl });
                    await db.SaveChangesAsync(token);

                    // The last item has gone: the order is complete.
                    if (!await db.Set<OrderLine>().AsNoTracking().AnyAsync(l => l.OrderId == orderId && l.QuantityShipped < l.Quantity, token))
                    {
                        var completed = await db.Set<Order>().Where(o => o.Id == orderId && o.Stage == OrderStage.Processing)
                            .ExecuteUpdateAsync(s => s.SetProperty(o => o.Stage, OrderStage.Completed).SetProperty(o => o.UpdatedUtc, (DateTimeOffset?)now), token);
                        if (completed > 0 && trail is not null)
                            await trail.RecordUpdateAsync("Order", orderId.ToString(), null, [new AuditChange("Stage", OrderStage.Processing.ToString(), OrderStage.Completed.ToString())], token);
                        if (completed > 0 && events is not null)
                        {
                            events.Publish(EcommerceEvents.OrderStageChanged, new { orderId, number = orderNumber, reference = OrderNumbers.Format(orderNumber), previousStage = OrderStage.Processing, stage = OrderStage.Completed });
                            await db.SaveChangesAsync(token);
                        }
                    }
                    return true;
                },
                async token => await db.Set<Shipment>().AsNoTracking().AnyAsync(s => s.Id == shipmentId, token),
                ct);
        }
        catch (DbUpdateException)
        {
            db.ChangeTracker.Clear();
            throw new PiranhaCommerceConflictException("The shipment could not be recorded. Please try again.");
        }

        var recorded = (await ShipmentQueries.ForOrderAsync(db, orderId, ct)).First(s => s.Id == shipmentId);
        await mailer.ShippedAsync(orderId, recorded, ct); // after the commit; cannot fail the shipment
        return recorded;
    }

    public async Task<IReadOnlyList<ReturnDto>?> ListReturnsAsync(Guid orderId, CancellationToken ct = default)
    {
        if (!await db.Set<Order>().AnyAsync(o => o.Id == orderId, ct)) return null;
        var returns = await db.Set<OrderReturn>().AsNoTracking().Where(r => r.OrderId == orderId).OrderBy(r => r.CreatedUtc).ThenBy(r => r.Id).ToListAsync(ct);
        if (returns.Count == 0) return [];
        var ids = returns.Select(r => r.Id).ToList();
        var lines = await db.Set<OrderReturnLine>().AsNoTracking().Where(l => ids.Contains(l.OrderReturnId))
            .Join(db.Set<OrderLine>(), rl => rl.OrderLineId, ol => ol.Id, (rl, ol) => new { rl.OrderReturnId, rl.OrderLineId, ol.Name, ol.Sku, rl.Quantity, rl.Restocked, ol.Position })
            .OrderBy(x => x.Position).ToListAsync(ct);
        return returns.Select(r => new ReturnDto(r.Id, r.OrderId, r.CreatedUtc, r.Reason, r.Note, r.Actor, r.SuggestedRefund, r.ShippingIncluded,
            lines.Where(l => l.OrderReturnId == r.Id).Select(l => new ReturnLineDto(l.OrderLineId, l.Name, l.Sku, l.Quantity, l.Restocked)).ToList())).ToList();
    }

    public async Task<ReturnDto?> RecordReturnAsync(Guid orderId, ReturnInput input, string? actor = null, CancellationToken ct = default)
    {
        ModelValidator.ValidateOrThrow(input);
        if (input.Lines.Select(l => l.OrderLineId).Distinct().Count() != input.Lines.Count)
            throw new PiranhaCommerceValidationException(nameof(ReturnInput.Lines), "List each item once.");

        var order = await db.Set<Order>().AsNoTracking().Where(o => o.Id == orderId).Select(o => new { o.Number, o.ShippingTotal, o.ShippingTax }).FirstOrDefaultAsync(ct);
        if (order is null) return null;

        var locationId = options.Value.StockLocationId ?? StockLocations.DefaultId;
        var ownLines = await db.Set<OrderLine>().AsNoTracking().Where(l => l.OrderId == orderId).Select(l => new { l.Id, l.ProductId }).ToListAsync(ct);
        var restockProducts = input.Lines.Where(l => l.Restock).Join(ownLines, l => l.OrderLineId, o => o.Id, (l, o) => o.ProductId).Distinct().ToList();
        if (restockProducts.Count > 0) await stock.EnsureLevelsAsync(restockProducts, locationId, ct); // before the transaction

        var returnId = Guid.NewGuid();
        var reference = OrderNumbers.Format(order.Number);
        var strategy = db.Database.CreateExecutionStrategy();
        try
        {
            await strategy.ExecuteInTransactionAsync(
                async token =>
                {
                    db.ChangeTracker.Clear();
                    var now = DateTimeOffset.UtcNow;

                    // The order row first (the same order as shipping and cancelling), so returns on one order are checked one after another.
                    await db.Set<Order>().Where(o => o.Id == orderId).ExecuteUpdateAsync(s => s.SetProperty(o => o.UpdatedUtc, (DateTimeOffset?)now), token);

                    var lines = await db.Set<OrderLine>().AsNoTracking().Where(l => l.OrderId == orderId).ToListAsync(token);
                    var suggested = 0m;
                    var stockChanges = new List<StockChange>();
                    var returnLines = new List<OrderReturnLine>();
                    foreach (var item in input.Lines)
                    {
                        var line = lines.FirstOrDefault(l => l.Id == item.OrderLineId)
                                   ?? throw new PiranhaCommerceValidationException(nameof(ReturnInput.Lines), "One of those items is not on this order.");
                        if (item.Quantity > line.QuantityShipped - line.QuantityReturned)
                            throw new PiranhaCommerceConflictException(line.QuantityShipped == 0
                                ? $"\"{line.Name}\" has not been sent yet, so it cannot come back."
                                : $"Only {line.QuantityShipped - line.QuantityReturned} of \"{line.Name}\" can still be returned.");

                        // The guard, as for shipping: shipped and returned never cross, however many people record at once.
                        var done = await db.Set<OrderLine>().Where(l => l.Id == line.Id && l.QuantityReturned + item.Quantity <= l.QuantityShipped)
                            .ExecuteUpdateAsync(s => s.SetProperty(l => l.QuantityReturned, l => l.QuantityReturned + item.Quantity), token);
                        if (done == 0) throw new PiranhaCommerceConflictException("That is more than can still be returned. Reload the order and check.");

                        suggested += RefundSuggestion.ForLine(line.LineTotal, line.DiscountAmount, line.TaxAmount, line.Quantity, line.QuantityReturned, item.Quantity);
                        returnLines.Add(new OrderReturnLine { OrderReturnId = returnId, OrderLineId = line.Id, Quantity = item.Quantity, Restocked = item.Restock });
                        if (item.Restock) stockChanges.Add(new StockChange(line.ProductId, item.Quantity, "Returned", reference, "\"" + line.Name + "\""));
                    }

                    if (input.ReturnRequestId is { } requestId)
                    {
                        // Only an approved request of this order, and only once: the conditional update decides between simultaneous recordings.
                        var marked = await db.Set<ReturnRequest>().Where(r => r.Id == requestId && r.OrderId == orderId && r.Status == ReturnRequestStatus.Approved)
                            .ExecuteUpdateAsync(s => s.SetProperty(r => r.Status, ReturnRequestStatus.Received).SetProperty(r => r.ReceivedReturnId, (Guid?)returnId), token);
                        if (marked == 0) throw new PiranhaCommerceConflictException("That return request is not an approved one for this order, or has already been received.");
                    }

                    if (input.IncludeShipping)
                    {
                        if (await db.Set<OrderReturn>().AsNoTracking().AnyAsync(r => r.OrderId == orderId && r.ShippingIncluded, token))
                            throw new PiranhaCommerceConflictException("The delivery charge is already part of an earlier return on this order.");
                        suggested += order.ShippingTotal + order.ShippingTax;
                    }

                    events?.Publish(EcommerceEvents.ReturnCreated, new { returnId, orderId, number = order.Number, reference, suggestedRefund = suggested, lineCount = returnLines.Count });
                    db.Add(new OrderReturn
                    {
                        Id = returnId, OrderId = orderId, CreatedUtc = now, Reason = input.Reason.Trim(),
                        Note = string.IsNullOrWhiteSpace(input.Note) ? null : input.Note.Trim(), Actor = actor, LocationId = locationId,
                        SuggestedRefund = suggested, ShippingIncluded = input.IncludeShipping, Lines = returnLines,
                    });
                    if (stockChanges.Count > 0)
                        await stock.ApplyInTransactionAsync(stockChanges, locationId, allowNegative: true, actor, token);
                    await db.SaveChangesAsync(token);
                    return true;
                },
                async token => await db.Set<OrderReturn>().AsNoTracking().AnyAsync(r => r.Id == returnId, token),
                ct);
        }
        catch (DbUpdateException)
        {
            db.ChangeTracker.Clear();
            throw new PiranhaCommerceConflictException("The return could not be recorded. Please try again.");
        }

        return (await ListReturnsAsync(orderId, ct))!.First(r => r.Id == returnId);
    }

    /// <summary>Turns what the caller asked to send into (line, quantity) pairs, against what is really left to send.</summary>
    private sealed class ShipmentBuilder(Guid id, Guid orderId, ShipmentInput input, string? actor)
    {
        public IReadOnlyList<(Guid LineId, int Quantity)> Resolve(IReadOnlyList<(Guid Id, int Remaining)> remaining)
        {
            if (input.Lines is null or { Count: 0 })
            {
                var all = remaining.Where(r => r.Remaining > 0).Select(r => (r.Id, r.Remaining)).ToList();
                if (all.Count == 0) throw new PiranhaCommerceConflictException("Everything on this order has already been sent.");
                return all;
            }
            if (input.Lines.Select(l => l.OrderLineId).Distinct().Count() != input.Lines.Count)
                throw new PiranhaCommerceValidationException(nameof(ShipmentInput.Lines), "List each item once.");
            foreach (var l in input.Lines)
            {
                var line = remaining.FirstOrDefault(r => r.Id == l.OrderLineId);
                if (line.Id == Guid.Empty) throw new PiranhaCommerceValidationException(nameof(ShipmentInput.Lines), "One of those items is not on this order.");
                if (l.Quantity > line.Remaining)
                    throw new PiranhaCommerceConflictException($"Only {line.Remaining} of that item is left to send.");
            }
            return input.Lines.Select(l => (l.OrderLineId, l.Quantity)).ToList();
        }

        public Shipment ToEntity(IReadOnlyList<(Guid LineId, int Quantity)> lines, DateTimeOffset at) => new()
        {
            Id = id, OrderId = orderId, ShippedUtc = at, Actor = actor,
            Carrier = Clean(input.Carrier), TrackingNumber = Clean(input.TrackingNumber), TrackingUrl = Clean(input.TrackingUrl),
            Lines = lines.Select(l => new ShipmentLine { ShipmentId = id, OrderLineId = l.LineId, Quantity = l.Quantity }).ToList(),
        };

        private static string? Clean(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();
    }
}
