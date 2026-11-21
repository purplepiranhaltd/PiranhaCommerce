using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using PiranhaCommerce.Core.Email;
using PiranhaCommerce.Data;

namespace PiranhaCommerce.Ecommerce;

/// <summary>The emails the online store sends. The wording is the email sender's: this only names the email and says what data it has.</summary>
public static class EcommerceEmailTemplates
{
    public const string OrderConfirmation = "order.confirmation";
    public const string OrderShipped = "order.shipped";
    public const string OrderCancelled = "order.cancelled";
    public const string OrderRefunded = "order.refunded";
    public const string UnreconciledPaymentAlert = "alert.unreconciled-payment";
}

internal sealed class EcommerceEmailContributor : IEmailTemplateContributor
{
    public IEnumerable<EmailTemplateDescriptor> GetTemplates() =>
    [
        new(EcommerceEmailTemplates.OrderConfirmation, "Order confirmation", "Sent to the customer when an order is placed.",
        [
            new("reference", "The order reference, e.g. ORD-1001.", "ORD-1001"),
            new("email", "The customer's email address.", "ada@example.com"),
            new("currency", "The currency code, e.g. GBP.", "GBP"),
            new("subtotal", "Goods total before any discount, formatted.", "30.00"),
            new("discount", "What a promo code took off, formatted. Empty when there was none.", "3.00"),
            new("promotionCode", "The promo code used. Empty when there was none.", "SPRING"),
            new("shipping", "The postage charge, formatted.", "5.00"),
            new("shippingMethod", "The name of the postage method. May be empty.", "Royal Mail Tracked"),
            new("tax", "Total tax, formatted.", "3.70"),
            new("total", "The order total, formatted.", "35.70"),
            new("paid", "How much has been paid, formatted. Empty when nothing has.", "35.70"),
            new("stillToPay", "How much is still owed, formatted.", "0.00"),
            new("lines", "The items ordered: a list where each has name, sku, quantity and total.", new List<object?>
            {
                new Dictionary<string, object?> { ["name"] = "Blue mug", ["sku"] = "MUG-BLU", ["quantity"] = 1, ["total"] = "10.00" },
                new Dictionary<string, object?> { ["name"] = "Teapot", ["sku"] = "POT-1", ["quantity"] = 2, ["total"] = "20.00" },
            }),
            new("taxes", "Tax by rate: a list where each has name, rate and amount. Empty when no tax was charged.", new List<object?>
            {
                new Dictionary<string, object?> { ["name"] = "VAT", ["rate"] = "20", ["amount"] = "3.70" },
            }),
            new("shippingAddress", "The delivery address as a list of lines. Empty when there is none.", new List<object?> { "Ada Lovelace", "1 High St", "Leeds", "LS1 1AA", "GB" }),
        ]),

        new(EcommerceEmailTemplates.OrderShipped, "Order sent", "Sent to the customer each time a parcel is recorded as sent.",
        [
            new("reference", "The order reference, e.g. ORD-1001.", "ORD-1001"),
            new("email", "The customer's email address.", "ada@example.com"),
            new("carrier", "The carrier. Empty when none was entered.", "Royal Mail"),
            new("trackingNumber", "The tracking number. Empty when none was entered.", "RM123456789GB"),
            new("trackingUrl", "A web link to track the parcel. Empty when none was entered.", "https://track.example/RM123456789GB"),
            new("lines", "What is in this parcel: a list where each has name, sku and quantity.", new List<object?>
            {
                new Dictionary<string, object?> { ["name"] = "Blue mug", ["sku"] = "MUG-BLU", ["quantity"] = 1 },
            }),
            new("complete", "True when this parcel completes the order, false when more is still to come.", false),
            new("stillToSend", "What has not been sent yet: a list where each has name, sku and quantity. Empty when the order is complete.", new List<object?>
            {
                new Dictionary<string, object?> { ["name"] = "Teapot", ["sku"] = "POT-1", ["quantity"] = 2 },
            }),
            new("shippingAddress", "The delivery address as a list of lines. Empty when there is none.", new List<object?> { "Ada Lovelace", "1 High St", "Leeds", "LS1 1AA", "GB" }),
        ]),

        new(EcommerceEmailTemplates.OrderCancelled, "Order cancelled", "Sent to the customer when an order is cancelled.",
        [
            new("reference", "The order reference, e.g. ORD-1001.", "ORD-1001"),
            new("email", "The customer's email address.", "ada@example.com"),
            new("currency", "The currency code, e.g. GBP.", "GBP"),
            new("total", "The order total, formatted.", "35.70"),
            new("stillToRefund", "Money the customer paid that has not been given back yet, formatted. Empty when nothing is owed to them.", "35.70"),
        ]),

        new(EcommerceEmailTemplates.OrderRefunded, "Refund made", "Sent to the customer when a refund is recorded on their order.",
        [
            new("reference", "The order reference, e.g. ORD-1001.", "ORD-1001"),
            new("email", "The customer's email address.", "ada@example.com"),
            new("currency", "The currency code, e.g. GBP.", "GBP"),
            new("amount", "This refund, formatted.", "10.00"),
            new("totalRefunded", "All refunds on the order so far, formatted.", "10.00"),
            new("orderTotal", "The order total, formatted.", "35.70"),
        ]),

        new(EcommerceEmailTemplates.UnreconciledPaymentAlert, "Unreconciled payment (to the shop)", "Sent to the shop's alert address when money a payment provider confirmed could not be booked against an order and needs a person.",
        [
            new("provider", "The payment provider's name, e.g. stripe.", "stripe"),
            new("kind", "What it was: PaymentConfirmed or RefundConfirmed.", "PaymentConfirmed"),
            new("amount", "The amount, formatted.", "35.70"),
            new("currency", "The currency code, e.g. GBP.", "GBP"),
            new("providerReference", "The provider's id for the payment or refund.", "pi_3Abc123"),
            new("orderReference", "The order reference when the order exists, e.g. ORD-1001. Empty when the provider named an order that is not here.", "ORD-1001"),
            new("reason", "Why it could not be booked.", "The order is cancelled."),
        ]),
    ];
}

internal static class OrderEmails
{
    public static EmailMessage Confirmation(OrderDto o)
    {
        var inv = CultureInfo.InvariantCulture;
        string Money(decimal v) => v.ToString("N2", inv);
        var variables = new Dictionary<string, object?>
        {
            ["reference"] = o.Reference, ["email"] = o.Email, ["currency"] = o.Currency,
            ["subtotal"] = Money(o.Subtotal), ["discount"] = o.DiscountTotal > 0 ? Money(o.DiscountTotal) : null, ["promotionCode"] = o.PromotionCode,
            ["shipping"] = Money(o.ShippingTotal), ["shippingMethod"] = o.ShippingMethodName, ["tax"] = Money(o.TaxTotal), ["total"] = Money(o.Total),
            ["paid"] = o.AmountPaid > 0 ? Money(o.AmountPaid) : null, ["stillToPay"] = Money(Math.Max(0m, o.Total - o.AmountPaid + o.AmountRefunded)),
            ["lines"] = o.Lines.Select(l => (object?)new Dictionary<string, object?>
                { ["name"] = l.Name, ["sku"] = l.Sku, ["quantity"] = l.Quantity, ["total"] = Money(l.LineTotal) }).ToList(),
            ["taxes"] = (o.Taxes ?? []).Where(t => t.Amount > 0).Select(t => (object?)new Dictionary<string, object?>
                { ["name"] = t.Name, ["rate"] = t.Rate.ToString("0.####", inv), ["amount"] = Money(t.Amount) }).ToList(),
            ["shippingAddress"] = o.ShippingAddress is { } a
                ? new[] { a.Recipient, a.Line1, a.Line2, a.City, a.Region, a.PostalCode, a.CountryCode }.Where(l => !string.IsNullOrWhiteSpace(l)).Cast<object?>().ToList()
                : new List<object?>(),
        };
        return new EmailMessage(o.Email, EcommerceEmailTemplates.OrderConfirmation, variables);
    }

    private static string Money(decimal v) => v.ToString("N2", CultureInfo.InvariantCulture);

    private static List<object?> Address(OrderAddress? a) => a is null
        ? []
        : new[] { a.Recipient, a.Line1, a.Line2, a.City, a.Region, a.PostalCode, a.CountryCode }.Where(l => !string.IsNullOrWhiteSpace(l)).Cast<object?>().ToList();

    private static List<object?> Items(IEnumerable<(string Name, string? Sku, int Quantity)> items) =>
        items.Select(i => (object?)new Dictionary<string, object?> { ["name"] = i.Name, ["sku"] = i.Sku, ["quantity"] = i.Quantity }).ToList();

    /// <summary>The order is read AFTER the parcel is saved, so what is "still to send" already excludes it.</summary>
    public static EmailMessage Shipped(Order o, ShipmentDto parcel)
    {
        var unsent = o.Lines.OrderBy(l => l.Position).Where(l => l.Quantity > l.QuantityShipped).Select(l => (l.Name, l.Sku, l.Quantity - l.QuantityShipped)).ToList();
        return new EmailMessage(o.Email, EcommerceEmailTemplates.OrderShipped, new Dictionary<string, object?>
        {
            ["reference"] = OrderNumbers.Format(o.Number), ["email"] = o.Email,
            ["carrier"] = parcel.Carrier, ["trackingNumber"] = parcel.TrackingNumber, ["trackingUrl"] = parcel.TrackingUrl,
            ["lines"] = Items(parcel.Lines.Select(l => (l.Name, l.Sku, l.Quantity))),
            ["complete"] = unsent.Count == 0,
            ["stillToSend"] = Items(unsent),
            ["shippingAddress"] = Address(o.ShippingAddress),
        });
    }

    public static EmailMessage Cancelled(string email, long number, string currency, decimal total, decimal paid, decimal refunded)
    {
        var owed = Math.Max(0m, paid - refunded);
        return new EmailMessage(email, EcommerceEmailTemplates.OrderCancelled, new Dictionary<string, object?>
        {
            ["reference"] = OrderNumbers.Format(number), ["email"] = email, ["currency"] = currency, ["total"] = Money(total),
            ["stillToRefund"] = owed > 0 ? Money(owed) : null,
        });
    }

    public static EmailMessage Refunded(string email, long number, string currency, decimal amount, decimal totalRefunded, decimal orderTotal) =>
        new(email, EcommerceEmailTemplates.OrderRefunded, new Dictionary<string, object?>
        {
            ["reference"] = OrderNumbers.Format(number), ["email"] = email, ["currency"] = currency, ["amount"] = Money(amount),
            ["totalRefunded"] = Money(totalRefunded), ["orderTotal"] = Money(orderTotal),
        });
}

/// <summary>
/// Sends the emails that follow a change to an order (a parcel sent, a cancel, a refund). Always AFTER the change is committed, and a
/// failure here (the mail server is down) is logged without the address and never undoes or fails the change.
/// </summary>
internal sealed class OrderMailer(PiranhaDbContext db, IEmailSender email, IOptions<EcommerceOptions> options, ILogger<OrderMailer> logger)
{
    public Task ShippedAsync(Guid orderId, ShipmentDto parcel, CancellationToken ct) => TryAsync("shipment", orderId, async () =>
    {
        var order = await db.Set<Order>().AsNoTracking().Include(o => o.Lines).AsSplitQuery().FirstOrDefaultAsync(o => o.Id == orderId, ct);
        return order is null or { ErasedUtc: not null } ? null : OrderEmails.Shipped(order, parcel); // an erased order has no one to write to
    }, ct);

    public Task CancelledAsync(Guid orderId, CancellationToken ct) => TryAsync("cancellation", orderId, async () =>
    {
        var o = await db.Set<Order>().AsNoTracking().Where(x => x.Id == orderId).Select(x => new { x.Email, x.Number, x.Currency, x.Total, x.ErasedUtc }).FirstOrDefaultAsync(ct);
        if (o is null or { ErasedUtc: not null }) return null;
        var (paid, refunded) = await SumsAsync(orderId, ct);
        return OrderEmails.Cancelled(o.Email, o.Number, o.Currency, o.Total, paid, refunded);
    }, ct);

    public Task RefundedAsync(Guid orderId, decimal amount, CancellationToken ct) => TryAsync("refund", orderId, async () =>
    {
        var o = await db.Set<Order>().AsNoTracking().Where(x => x.Id == orderId).Select(x => new { x.Email, x.Number, x.Currency, x.Total, x.ErasedUtc }).FirstOrDefaultAsync(ct);
        if (o is null or { ErasedUtc: not null }) return null; // e.g. a late refund webhook for an order that has since been erased
        var (_, refunded) = await SumsAsync(orderId, ct);
        return OrderEmails.Refunded(o.Email, o.Number, o.Currency, amount, refunded, o.Total);
    }, ct);

    /// <summary>Tells the shop's alert address(es) about money that needs a person. Never throws: the webhook has already been answered.</summary>
    public async Task UnreconciledAsync(UnreconciledPayment p, CancellationToken ct)
    {
        var addresses = (options.Value.AlertEmail ?? "").Split([',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (addresses.Length == 0) return;
        try
        {
            var number = await db.Set<Order>().AsNoTracking().Where(o => o.Id == p.OrderId).Select(o => (long?)o.Number).FirstOrDefaultAsync(ct);
            var variables = new Dictionary<string, object?>
            {
                ["provider"] = p.Provider, ["kind"] = p.Kind.ToString(), ["amount"] = Money(p.Amount), ["currency"] = p.Currency,
                ["providerReference"] = p.Reference, ["orderReference"] = number is { } n ? OrderNumbers.Format(n) : null, ["reason"] = p.Reason,
            };
            foreach (var to in addresses.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                try { await email.SendAsync(new EmailMessage(to, EcommerceEmailTemplates.UnreconciledPaymentAlert, variables), ct); }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                catch (Exception ex) { logger.LogWarning(ex, "The unreconciled payment alert could not be sent to one alert address."); } // not the address
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex) { logger.LogWarning(ex, "The unreconciled payment alert could not be prepared."); }
    }

    private static string Money(decimal v) => v.ToString("N2", CultureInfo.InvariantCulture);

    private async Task<(decimal Paid, decimal Refunded)> SumsAsync(Guid orderId, CancellationToken ct)
    {
        var totals = await db.Set<Payment>().AsNoTracking().Where(p => p.OrderId == orderId)
            .GroupBy(p => p.Kind).Select(g => new { Kind = g.Key, Sum = g.Sum(p => p.Amount) }).ToListAsync(ct);
        return (totals.Where(t => t.Kind == PaymentKind.Payment).Sum(t => t.Sum), totals.Where(t => t.Kind == PaymentKind.Refund).Sum(t => t.Sum));
    }

    private async Task TryAsync(string what, Guid orderId, Func<Task<EmailMessage?>> build, CancellationToken ct)
    {
        if (!options.Value.SendOrderUpdates) return;
        try
        {
            if (await build() is { } message) await email.SendAsync(message, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "The {What} email for order {OrderId} could not be sent.", what, orderId); // never the address
        }
    }
}
