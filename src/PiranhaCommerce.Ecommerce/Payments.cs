using PiranhaCommerce.Core.Events;
using System.ComponentModel.DataAnnotations;
using Microsoft.EntityFrameworkCore;
using PiranhaCommerce.Core.Domain;
using PiranhaCommerce.Core.Validation;
using PiranhaCommerce.Data;
using PiranhaCommerce.Core.Identity;

namespace PiranhaCommerce.Ecommerce;

public enum PaymentKind { Payment = 0, Refund = 1 }

public enum PaymentStatus { Unpaid = 0, PartlyPaid = 1, Paid = 2, Refunded = 3 }

/// <summary>
/// Money received for, or returned against, an order. Append-only like the stock ledger: a mistake is corrected with a refund, never
/// by editing or deleting a row. Amounts are always positive; <see cref="Kind"/> says which way the money went.
/// </summary>
public class Payment : Entity
{
    public Guid OrderId { get; set; }
    public PaymentKind Kind { get; set; }
    public required string Provider { get; set; }
    public decimal Amount { get; set; }
    public required string Currency { get; set; }
    /// <summary>The provider's own id (a card receipt, a bank reference). Unique per provider, which makes retries safe.</summary>
    public string? Reference { get; set; }
    public string? Note { get; set; }
    public string? Actor { get; set; }
    public DateTimeOffset CreatedUtc { get; set; }
}

public sealed class PaymentInput
{
    [Range(0.01, 1_000_000_000)]
    public decimal Amount { get; set; }

    /// <summary>Name of a registered <see cref="IPaymentProvider"/>; "manual" when omitted.</summary>
    [StringLength(50)]
    public string? Provider { get; set; }

    [StringLength(200)] public string? Reference { get; set; }
    [StringLength(500)] public string? Note { get; set; }

    /// <summary>Set by the host, never by a caller (an internal member is not read from a request): do not email the buyer about this refund.</summary>
    internal bool SuppressBuyerEmail { get; set; }
}

public sealed record PaymentDto(
    Guid Id, Guid OrderId, PaymentKind Kind, string Provider, decimal Amount, string Currency, string? Reference, string? Note, string? Actor, DateTimeOffset CreatedUtc);

public sealed record PaymentProviderDto(string Name, string DisplayName, bool IsGateway = false, bool CanRefund = false);

/// <summary>
/// A way of taking money. Today a provider only names itself: recording a payment is something staff (or a gateway plugin's webhook)
/// does through <see cref="IPaymentService"/>. A gateway plugin registers one of these and calls the service when its gateway confirms.
/// </summary>
public interface IPaymentProvider
{
    /// <summary>Short stable id stored on every payment, e.g. "manual", "stripe".</summary>
    string Name { get; }
    string DisplayName { get; }
}

/// <summary>Money handled outside the system: cash, bank transfer, a card terminal, a cheque.</summary>
internal sealed class ManualPaymentProvider : IPaymentProvider
{
    public string Name => "manual";
    public string DisplayName => "Manual (cash, bank transfer, card terminal)";
}

public static class PaymentTotals
{
    public static PaymentStatus StatusOf(decimal orderTotal, decimal paid, decimal refunded)
    {
        var net = paid - refunded;
        if (net <= 0) return refunded > 0 ? PaymentStatus.Refunded : PaymentStatus.Unpaid;
        return net >= orderTotal ? PaymentStatus.Paid : PaymentStatus.PartlyPaid;
    }
}

public sealed class RefundToVoucherInput
{
    [System.ComponentModel.DataAnnotations.Range(0.01, 1_000_000_000)] public decimal Amount { get; set; }
    [System.ComponentModel.DataAnnotations.StringLength(500)] public string? Note { get; set; }
}

public interface IPaymentService
{
    IReadOnlyList<PaymentProviderDto> Providers();

    /// <summary>Oldest first. Null when the order does not exist.</summary>
    Task<IReadOnlyList<PaymentDto>?> ListAsync(Guid orderId, CancellationToken ct = default);

    /// <summary>
    /// Records money received. Conflict when it would take the order past its total, the order is cancelled, or the provider
    /// reference was already recorded. Null when the order does not exist.
    /// </summary>
    [RequiresPermission(Permissions.OrdersFulfil)]
    Task<PaymentDto?> RecordPaymentAsync(Guid orderId, PaymentInput input, string? actor = null, CancellationToken ct = default);

    /// <summary>Records money returned. Conflict when it would return more than has been received. Null when the order does not exist.</summary>
    [RequiresPermission(Permissions.OrdersRefund)]
    Task<PaymentDto?> RecordRefundAsync(Guid orderId, PaymentInput input, string? actor = null, CancellationToken ct = default);

    /// <summary>
    /// Gives money back onto the gift voucher the order was (partly) paid with: the voucher's balance goes up, its ledger gets a Returned row and the
    /// order a refund. Never more than the voucher paid and has not already been returned (cancelling counts), nor more than is refundable.
    /// Conflict when the order was not paid with a voucher or the amount is too much. Null when the order does not exist.
    /// </summary>
    [RequiresPermission(Permissions.OrdersRefund)]
    Task<PaymentDto?> RefundToVoucherAsync(Guid orderId, RefundToVoucherInput input, string? actor = null, CancellationToken ct = default);
}

internal sealed class PaymentService(PiranhaCommerceDbContext db, IEnumerable<IPaymentProvider> providers, IEnumerable<IPaymentGateway> gateways, OrderMailer mailer, IEventPublisher? events = null) : IPaymentService
{
    public IReadOnlyList<PaymentProviderDto> Providers() => providers.Select(p => new PaymentProviderDto(p.Name, p.DisplayName, gateways.Any(g => g.Name == p.Name), gateways.OfType<IRefundingGateway>().Any(g => g.Name == p.Name))).OrderBy(p => p.Name).ToList();

    public async Task<IReadOnlyList<PaymentDto>?> ListAsync(Guid orderId, CancellationToken ct = default)
    {
        if (!await db.Set<Order>().AnyAsync(o => o.Id == orderId, ct)) return null;
        return await db.Set<Payment>().AsNoTracking().Where(p => p.OrderId == orderId).OrderBy(p => p.CreatedUtc).ThenBy(p => p.Id)
            .Select(p => new PaymentDto(p.Id, p.OrderId, p.Kind, p.Provider, p.Amount, p.Currency, p.Reference, p.Note, p.Actor, p.CreatedUtc))
            .ToListAsync(ct);
    }

    public Task<PaymentDto?> RecordPaymentAsync(Guid orderId, PaymentInput input, string? actor = null, CancellationToken ct = default)
        => RecordAsync(orderId, PaymentKind.Payment, input, actor, ct);

    public Task<PaymentDto?> RecordRefundAsync(Guid orderId, PaymentInput input, string? actor = null, CancellationToken ct = default)
        => RecordAsync(orderId, PaymentKind.Refund, input, actor, ct);

    public async Task<PaymentDto?> RefundToVoucherAsync(Guid orderId, RefundToVoucherInput input, string? actor = null, CancellationToken ct = default)
    {
        ModelValidator.ValidateOrThrow(input);
        if (decimal.Round(input.Amount, 2) != input.Amount)
            throw new PiranhaCommerceValidationException(nameof(RefundToVoucherInput.Amount), "Use at most two decimal places.");
        if (!await db.Set<Order>().AnyAsync(o => o.Id == orderId, ct)) return null;

        var paymentId = Guid.NewGuid();
        var strategy = db.Database.CreateExecutionStrategy();
        try
        {
            await strategy.ExecuteInTransactionAsync(
                async token =>
                {
                    db.ChangeTracker.Clear();
                    // The order's row lock first, as for every payment, so this and a cancel or another refund are checked one after another.
                    var now = DateTimeOffset.UtcNow;
                    await db.Set<Order>().Where(o => o.Id == orderId).ExecuteUpdateAsync(s => s.SetProperty(o => o.UpdatedUtc, (DateTimeOffset?)now), token);
                    var order = await db.Set<Order>().AsNoTracking().Where(o => o.Id == orderId).Select(o => new { o.Number, o.Currency, o.VoucherId, o.VoucherPaid }).FirstAsync(token);
                    if (order.VoucherId is not { } voucherId || order.VoucherPaid <= 0)
                        throw new PiranhaCommerceConflictException("This order was not paid with a gift voucher.");

                    var payments = await db.Set<Payment>().AsNoTracking().Where(p => p.OrderId == orderId).Select(p => new { p.Kind, p.Provider, p.Amount }).ToListAsync(token);
                    var paid = payments.Where(p => p.Kind == PaymentKind.Payment).Sum(p => p.Amount);
                    var refunded = payments.Where(p => p.Kind == PaymentKind.Refund).Sum(p => p.Amount);
                    var returnedToVoucher = payments.Where(p => p.Kind == PaymentKind.Refund && p.Provider == "voucher").Sum(p => p.Amount);

                    var room = Math.Min(order.VoucherPaid - returnedToVoucher, paid - refunded);
                    if (input.Amount > room)
                        throw new PiranhaCommerceConflictException($"That is more than can go back onto the gift voucher: {Math.Max(0m, room):N2} {order.Currency} is left to return to it.");

                    var last4 = await db.Set<GiftVoucher>().AsNoTracking().Where(v => v.Id == voucherId).Select(v => v.CodeLast4).FirstAsync(token);
                    var reference = OrderNumbers.Format(order.Number);
                    await db.Set<GiftVoucher>().Where(v => v.Id == voucherId).ExecuteUpdateAsync(s => s.SetProperty(v => v.Balance, v => v.Balance + input.Amount), token);
                    db.Add(new VoucherEntry { VoucherId = voucherId, Amount = input.Amount, Kind = VoucherEntryKinds.Returned, OrderId = orderId, Reference = reference, Actor = actor, CreatedUtc = now });
                    events?.Publish(EcommerceEvents.PaymentRefunded, new { paymentId, orderId, number = order.Number, reference, kind = PaymentKind.Refund, provider = "voucher", amount = input.Amount, currency = order.Currency });
                    db.Add(new Payment
                    {
                        Id = paymentId, OrderId = orderId, Kind = PaymentKind.Refund, Provider = "voucher", Amount = input.Amount, Currency = order.Currency,
                        Reference = $"VCH-{last4}-{reference}-RETURN-{paymentId:N}"[..Math.Min(200, $"VCH-{last4}-{reference}-RETURN-{paymentId:N}".Length)],
                        Note = string.IsNullOrWhiteSpace(input.Note) ? $"Returned to gift voucher ending {last4}" : $"Returned to gift voucher ending {last4}: {input.Note.Trim()}",
                        Actor = actor, CreatedUtc = now,
                    });
                    await db.SaveChangesAsync(token);
                    return true;
                },
                async token => await db.Set<Payment>().AsNoTracking().AnyAsync(p => p.Id == paymentId, token),
                ct);
        }
        catch (DbUpdateException)
        {
            db.ChangeTracker.Clear();
            throw new PiranhaCommerceConflictException("The refund could not be recorded and nothing was changed. Please try again.");
        }

        var saved = await db.Set<Payment>().AsNoTracking().FirstAsync(p => p.Id == paymentId, ct);
        return new PaymentDto(saved.Id, orderId, saved.Kind, saved.Provider, saved.Amount, saved.Currency, saved.Reference, saved.Note, saved.Actor, saved.CreatedUtc);
        // No email: it is credit on a voucher, not money back to a card, the same as when cancelling.
    }

    private async Task<PaymentDto?> RecordAsync(Guid orderId, PaymentKind kind, PaymentInput input, string? actor, CancellationToken ct)
    {
        ModelValidator.ValidateOrThrow(input);
        if (decimal.Round(input.Amount, 2) != input.Amount)
            throw new PiranhaCommerceValidationException(nameof(PaymentInput.Amount), "Use at most two decimal places.");
        var providerName = string.IsNullOrWhiteSpace(input.Provider) ? "manual" : input.Provider.Trim().ToLowerInvariant();
        if (!providers.Any(p => p.Name == providerName))
            throw new PiranhaCommerceValidationException(nameof(PaymentInput.Provider), $"Unknown payment provider \"{providerName}\".");

        if (!await db.Set<Order>().AnyAsync(o => o.Id == orderId, ct)) return null;

        var payment = new Payment
        {
            OrderId = orderId, Kind = kind, Provider = providerName, Amount = input.Amount, Currency = "",
            Reference = string.IsNullOrWhiteSpace(input.Reference) ? null : input.Reference.Trim(),
            Note = string.IsNullOrWhiteSpace(input.Note) ? null : input.Note.Trim(), Actor = actor, CreatedUtc = DateTimeOffset.UtcNow,
        };
        var paymentId = payment.Id;

        var strategy = db.Database.CreateExecutionStrategy();
        try
        {
            await strategy.ExecuteInTransactionAsync(
                async token =>
                {
                    db.ChangeTracker.Clear();

                    // Take the order's row lock first, so simultaneous payments for one order are checked one after another.
                    var now = DateTimeOffset.UtcNow;
                    await db.Set<Order>().Where(o => o.Id == orderId).ExecuteUpdateAsync(s => s.SetProperty(o => o.UpdatedUtc, (DateTimeOffset?)now), token);
                    var order = await db.Set<Order>().AsNoTracking().Where(o => o.Id == orderId).Select(o => new { o.Number, o.Total, o.Stage, o.Currency }).FirstAsync(token);

                    var totals = await db.Set<Payment>().AsNoTracking().Where(p => p.OrderId == orderId)
                        .GroupBy(p => p.Kind).Select(g => new { Kind = g.Key, Sum = g.Sum(p => p.Amount) }).ToListAsync(token);
                    var paid = totals.Where(t => t.Kind == PaymentKind.Payment).Sum(t => t.Sum);
                    var refunded = totals.Where(t => t.Kind == PaymentKind.Refund).Sum(t => t.Sum);

                    if (kind == PaymentKind.Payment)
                    {
                        if (order.Stage == OrderStage.Cancelled) throw new PiranhaCommerceConflictException("A cancelled order cannot take payment.");
                        var outstanding = order.Total - (paid - refunded);
                        if (input.Amount > outstanding)
                            throw new PiranhaCommerceConflictException($"That is more than is owed: {outstanding:N2} {order.Currency} outstanding.");
                    }
                    else if (input.Amount > paid - refunded)
                        throw new PiranhaCommerceConflictException($"That is more than can be refunded: {paid - refunded:N2} {order.Currency} received and not yet returned.");

                    payment.Currency = order.Currency;
                    db.Add(payment);
                    events?.Publish(kind == PaymentKind.Payment ? EcommerceEvents.PaymentReceived : EcommerceEvents.PaymentRefunded,
                        new { paymentId = payment.Id, orderId, number = order.Number, reference = OrderNumbers.Format(order.Number), kind, provider = payment.Provider, amount = payment.Amount, currency = payment.Currency, paymentReference = payment.Reference });
                    await db.SaveChangesAsync(token);
                    return true;
                },
                async token => await db.Set<Payment>().AsNoTracking().AnyAsync(p => p.Id == paymentId, token),
                ct);
        }
        catch (DbUpdateException)
        {
            db.ChangeTracker.Clear();
            throw new PiranhaCommerceConflictException("That payment reference has already been recorded for this provider.");
        }

        // Money going back to the buyer is worth telling them about, however it was booked (by hand, started here, or the provider's webhook).
        // Gift-voucher credit is not: cancelling already tells them, and it is not money back to a card.
        if (kind == PaymentKind.Refund && payment.Provider != "voucher" && !input.SuppressBuyerEmail) await mailer.RefundedAsync(orderId, payment.Amount, ct);

        return new PaymentDto(payment.Id, orderId, kind, payment.Provider, payment.Amount, payment.Currency, payment.Reference, payment.Note, payment.Actor, payment.CreatedUtc);
    }
}
