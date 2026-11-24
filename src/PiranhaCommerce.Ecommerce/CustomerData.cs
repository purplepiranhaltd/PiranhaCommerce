using Microsoft.EntityFrameworkCore;
using PiranhaCommerce.Customers;
using PiranhaCommerce.Data;

namespace PiranhaCommerce.Ecommerce;

/// <summary>
/// The online store's part of exporting and erasing a customer. Orders are never deleted (the shop must keep its accounts): they are
/// anonymised in place. Name, address lines, postcode, email and notes go; totals, lines, payments, shipments and the country (which
/// tax reporting needs) stay.
/// </summary>
internal sealed class EcommerceCustomerData(PiranhaCommerceDbContext db) : ICustomerDataContributor
{
    /// <summary>A reserved, undeliverable address (RFC 2606): nothing sent to an erased order can ever reach anyone.</summary>
    public const string ErasedEmail = "erased@erased.invalid";
    public const string ErasedText = "Erased";

    public string Name => "orders";

    /// <summary>
    /// The person's orders: those on their account, and guest orders placed under the same email address (any letter case; both sides
    /// lower-cased so PostgreSQL matches too). Orders already erased carry the placeholder address and so are not matched again.
    /// </summary>
    private IQueryable<Order> OrdersOf(CustomerSubject s)
    {
        var email = s.Email.Trim().ToLower();
        return db.Set<Order>().Where(o => o.CustomerId == s.CustomerId || (o.CustomerId == null && o.Email.ToLower() == email));
    }

    public async Task<IReadOnlyList<string>> CheckErasureAsync(CustomerSubject subject, CancellationToken ct = default)
    {
        var reasons = new List<string>();
        var orders = await OrdersOf(subject).AsNoTracking().Select(o => new { o.Id, o.Number, o.Stage }).ToListAsync(ct);

        foreach (var o in orders.Where(o => o.Stage is OrderStage.Placed or OrderStage.Processing).OrderBy(o => o.Number))
            reasons.Add($"Order {OrderNumbers.Format(o.Number)} is still open ({o.Stage}): complete or cancel it first.");

        // A cancelled order that was paid and not refunded: the contact details are the only way to return that money.
        var cancelled = orders.Where(o => o.Stage == OrderStage.Cancelled).ToList();
        foreach (var chunk in cancelled.Chunk(500))
        {
            var ids = chunk.Select(o => o.Id).ToList();
            var sums = await db.Set<Payment>().AsNoTracking().Where(p => ids.Contains(p.OrderId))
                .GroupBy(p => new { p.OrderId, p.Kind }).Select(g => new { g.Key.OrderId, g.Key.Kind, Sum = g.Sum(p => p.Amount) }).ToListAsync(ct);
            foreach (var o in chunk.OrderBy(o => o.Number))
            {
                var owed = sums.Where(x => x.OrderId == o.Id && x.Kind == PaymentKind.Payment).Sum(x => x.Sum)
                           - sums.Where(x => x.OrderId == o.Id && x.Kind == PaymentKind.Refund).Sum(x => x.Sum);
                if (owed > 0) reasons.Add($"Order {OrderNumbers.Format(o.Number)} is cancelled but {owed:N2} has not been refunded: refund it first.");
            }
        }
        return reasons;
    }

    public async Task<object?> ExportAsync(CustomerSubject subject, CancellationToken ct = default)
    {
        var orders = await OrdersOf(subject).AsNoTracking().Include(o => o.Lines).AsSplitQuery().OrderBy(o => o.Number).ToListAsync(ct);
        var ids = orders.Select(o => o.Id).ToList();
        var payments = await ChunkedAsync(ids, chunk => db.Set<Payment>().AsNoTracking().Where(p => chunk.Contains(p.OrderId)).ToListAsync(ct));
        var shipments = await ChunkedAsync(ids, chunk => db.Set<Shipment>().AsNoTracking().Where(s => chunk.Contains(s.OrderId)).ToListAsync(ct));
        var returns = await ChunkedAsync(ids, chunk => db.Set<OrderReturn>().AsNoTracking().Where(r => chunk.Contains(r.OrderId)).ToListAsync(ct));
        var requests = await ChunkedAsync(ids, chunk => db.Set<ReturnRequest>().AsNoTracking().Where(r => chunk.Contains(r.OrderId)).ToListAsync(ct));
        var baskets = await db.Set<Basket>().AsNoTracking().Include(b => b.Lines).AsSplitQuery().Where(b => b.CustomerId == subject.CustomerId).ToListAsync(ct);

        object? Address(OrderAddress? a) => a is null ? null : new { a.Recipient, a.Line1, a.Line2, a.City, a.Region, a.PostalCode, a.CountryCode };

        return new
        {
            Orders = orders.Select(o => new
            {
                Reference = OrderNumbers.Format(o.Number), o.Stage, PlacedUtc = o.CreatedUtc, o.Email, o.Currency,
                o.Subtotal, o.DiscountTotal, o.ShippingTotal, o.TaxTotal, o.Total, o.Notes, o.PromotionCode, o.ErasedUtc,
                ShippingAddress = Address(o.ShippingAddress), BillingAddress = Address(o.BillingAddress),
                Lines = o.Lines.OrderBy(l => l.Position).Select(l => new { l.Name, l.Sku, l.UnitPrice, l.Quantity, l.LineTotal, l.QuantityShipped, l.QuantityReturned }),
                Payments = payments.Where(p => p.OrderId == o.Id).OrderBy(p => p.CreatedUtc).Select(p => new { p.Kind, p.Provider, p.Amount, p.Currency, p.Reference, p.Note, p.CreatedUtc }),
                Shipments = shipments.Where(s => s.OrderId == o.Id).OrderBy(s => s.ShippedUtc).Select(s => new { s.ShippedUtc, s.Carrier, s.TrackingNumber, s.TrackingUrl }),
                Returns = returns.Where(r => r.OrderId == o.Id).OrderBy(r => r.CreatedUtc).Select(r => new { r.CreatedUtc, r.Reason, r.Note, r.SuggestedRefund }),
                ReturnRequests = requests.Where(r => r.OrderId == o.Id).OrderBy(r => r.RequestedUtc).Select(r => new { r.RequestedUtc, r.Status, r.Reason, r.Note, r.ResponseNote, r.RespondedUtc }),
            }),
            Baskets = baskets.Select(b => new { b.Id, b.Status, b.CreatedUtc, b.PromotionCode, Lines = b.Lines.Select(l => new { l.ProductId, l.Quantity }) }),
        };
    }

    public async Task<int> EraseAsync(CustomerSubject subject, CancellationToken ct = default)
    {
        var now = DateTimeOffset.UtcNow;

        var orders = await OrdersOf(subject).ToListAsync(ct);
        foreach (var o in orders)
        {
            o.Email = ErasedEmail;
            o.Notes = null;
            o.CustomerId = null;
            o.ErasedUtc = now;
            Anonymise(o.ShippingAddress);
            Anonymise(o.BillingAddress);
        }
        await db.SaveChangesAsync(ct);

        var ids = orders.Select(o => o.Id).ToList();
        foreach (var chunk in ids.Chunk(500))
        {
            var c = chunk.ToList();
            // Notes typed by staff may name the person. Notes the system wrote ("Confirmed by Stripe") say nothing about anyone and stay.
            await db.Set<Payment>().Where(p => c.Contains(p.OrderId) && p.Provider == "manual" && p.Note != null).ExecuteUpdateAsync(s => s.SetProperty(p => p.Note, (string?)null), ct);
            await db.Set<OrderReturn>().Where(r => c.Contains(r.OrderId) && r.Note != null).ExecuteUpdateAsync(s => s.SetProperty(r => r.Note, (string?)null), ct);
            // What the buyer and the shop wrote to each other about a return. An unanswered request has no one left to answer.
            await db.Set<ReturnRequest>().Where(r => c.Contains(r.OrderId) && (r.Note != null || r.ResponseNote != null))
                .ExecuteUpdateAsync(s => s.SetProperty(r => r.Note, (string?)null).SetProperty(r => r.ResponseNote, (string?)null), ct);
            await db.Set<ReturnRequest>().Where(r => c.Contains(r.OrderId) && r.Status == ReturnRequestStatus.Requested)
                .ExecuteUpdateAsync(s => s.SetProperty(r => r.Status, ReturnRequestStatus.Cancelled).SetProperty(r => r.RespondedUtc, (DateTimeOffset?)now), ct);
        }

        // An open basket has nothing worth keeping once its owner is gone; a checked-out one is only the order's history, so just let go of the person.
        var open = await db.Set<Basket>().Where(b => b.CustomerId == subject.CustomerId && b.Status == BasketStatus.Open).ExecuteDeleteAsync(ct);
        await db.Set<Basket>().Where(b => b.CustomerId == subject.CustomerId).ExecuteUpdateAsync(s => s.SetProperty(b => b.CustomerId, (Guid?)null), ct);

        db.ChangeTracker.Clear();
        return orders.Count + open;
    }

    private static void Anonymise(OrderAddress? a)
    {
        if (a is null) return;
        a.Recipient = null; a.Line2 = null; a.Region = null; a.PostalCode = null;
        a.Line1 = ErasedText; a.City = ErasedText; // required columns; the country stays for tax reporting
    }

    private static async Task<List<T>> ChunkedAsync<T>(List<Guid> ids, Func<List<Guid>, Task<List<T>>> load)
    {
        var all = new List<T>();
        foreach (var chunk in ids.Chunk(500)) all.AddRange(await load(chunk.ToList()));
        return all;
    }
}
