using System.ComponentModel.DataAnnotations;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using PiranhaCommerce.Core.Validation;
using PiranhaCommerce.Data;

namespace PiranhaCommerce.Ecommerce;

/// <summary>
/// A card or wallet gateway (Stripe, Adyen, PayPal...), written as a plugin package. It does two things: starts a payment for an
/// order and says where to send the shopper, and, later, reads the gateway's webhook and says what happened. Everything else (the
/// payment ledger, over-payment checks, idempotency, order status) stays in the core, so a gateway plugin is small.
/// Register one with <see cref="PaymentGatewayExtensions.AddPaymentGateway{T}"/>.
/// </summary>
public interface IPaymentGateway : IPaymentProvider
{
    /// <summary>
    /// Opens a payment for the amount still owed. Put <see cref="GatewayStartRequest.OrderId"/> into the gateway's metadata: the
    /// webhook must carry it back, because that is how the money is matched to the order.
    /// </summary>
    Task<GatewaySession> StartAsync(GatewayStartRequest request, CancellationToken ct = default);

    /// <summary>
    /// Verifies and reads a webhook. The gateway MUST check its signature here and throw <see cref="GatewayWebhookRejectedException"/>
    /// when it is wrong: this endpoint is public, so the signature is the only thing that makes the call trustworthy. Return no events
    /// for a webhook you do not act on (the host answers 200 so the gateway stops retrying).
    /// </summary>
    Task<IReadOnlyList<GatewayEvent>> ReadWebhookAsync(GatewayWebhookRequest request, CancellationToken ct = default);
}

/// <param name="Amount">What is still owed on the order, in <paramref name="Currency"/>.</param>
public sealed record GatewayStartRequest(Guid OrderId, string OrderReference, decimal Amount, string Currency, string? CustomerEmail);

/// <param name="RedirectUrl">Where to send the shopper to pay (a hosted page), or null for a gateway that needs no redirect.</param>
/// <param name="ProviderReference">The gateway's id for this attempt, for support. Not the payment reference: that comes with the confirmation.</param>
public sealed record GatewaySession(string? RedirectUrl, string? ProviderReference);

/// <param name="Headers">Header names are matched case-insensitively.</param>
/// <param name="Body">The exact bytes received. A signature is over these, so do not re-serialise them.</param>
public sealed record GatewayWebhookRequest(IReadOnlyDictionary<string, string> Headers, byte[] Body);

public enum GatewayEventKind { PaymentConfirmed = 0, RefundConfirmed = 1 }

/// <param name="Reference">The gateway's unique id for this payment or refund. Replays of the same webhook carry the same one, which is what makes them harmless.</param>
public sealed record GatewayEvent(GatewayEventKind Kind, Guid OrderId, decimal Amount, string Currency, string Reference);

public sealed class GatewayWebhookRejectedException(string message) : Exception(message);

/// <summary>
/// A gateway that can also send money back to the buyer's card. Optional: a gateway without it simply cannot refund from the admin
/// (staff record a refund they made elsewhere instead). Implement it on the same class as <see cref="IPaymentGateway"/>.
/// </summary>
public interface IRefundingGateway : IPaymentGateway
{
    /// <summary>
    /// Asks the gateway to refund part or all of one payment. The core has already checked the amount against what was paid and not
    /// yet returned. <see cref="GatewayRefundRequest.IdempotencyKey"/> must make a repeat of the same request harmless (pass it on to
    /// the gateway), so a double click or a retry cannot refund twice. Throw <see cref="GatewayException"/> when it is refused.
    /// </summary>
    Task<GatewayRefund> RefundAsync(GatewayRefundRequest request, CancellationToken ct = default);
}

/// <param name="PaymentReference">The gateway's id for the payment being refunded, as recorded when the payment was booked.</param>
public sealed record GatewayRefundRequest(Guid OrderId, string PaymentReference, decimal Amount, string Currency, string IdempotencyKey);

/// <param name="Reference">The gateway's id for the refund: the same one its webhook will carry, which is what stops the refund being booked twice.</param>
/// <param name="Succeeded">False while the gateway is still working on it (some methods take days); the webhook books it when it finishes.</param>
public sealed record GatewayRefund(string Reference, string Status, bool Succeeded, decimal Amount, string Currency);

public sealed class RefundStartInput
{
    [Range(0.01, 1_000_000_000)]
    public decimal Amount { get; set; }

    /// <summary>Send the same value again to repeat a request safely (a double click, a retry). A new value is a new refund.</summary>
    [StringLength(100)]
    public string? IdempotencyKey { get; set; }
}

/// <param name="Outcome">Recorded, AlreadyRecorded, Unreconciled (money moved but could not be booked: see the unreconciled list) or Pending (the gateway is still processing it).</param>
public sealed record RefundResultDto(string Provider, string Reference, string Status, decimal Amount, string Currency, string Outcome);

/// <summary>A gateway could not do what was asked (it is down, refused the request, or is not configured). The message is for logs, not shoppers: the host answers with a generic one.</summary>
public sealed class GatewayException(string message, Exception? inner = null) : Exception(message, inner);

public sealed class StartPaymentInput
{
    [Required, StringLength(50)] public string Provider { get; set; } = "";
}

public sealed record PaymentSessionDto(string Provider, decimal Amount, string Currency, string? RedirectUrl, string? ProviderReference);

public sealed record WebhookResultDto(int Recorded, int AlreadyRecorded, int Unreconciled);

public static class PaymentGatewayExtensions
{
    /// <summary>Registers a gateway as both a payment provider (so it shows in the list and on payments) and a gateway (so it can start and confirm payments).</summary>
    public static IServiceCollection AddPaymentGateway<T>(this IServiceCollection services) where T : class, IPaymentGateway
    {
        services.TryAddSingleton<T>();
        services.AddSingleton<IPaymentGateway>(sp => sp.GetRequiredService<T>());
        services.AddSingleton<IPaymentProvider>(sp => sp.GetRequiredService<T>());
        if (typeof(IRefundingGateway).IsAssignableFrom(typeof(T))) services.AddSingleton<IRefundingGateway>(sp => (IRefundingGateway)(object)sp.GetRequiredService<T>());
        return services;
    }
}

public interface IGatewayService
{
    /// <summary>
    /// Opens a payment of whatever is still owed. Validation error for an unknown or non-gateway provider; conflict when nothing is
    /// owed or the order is cancelled. Null when the order does not exist.
    /// </summary>
    Task<PaymentSessionDto?> StartAsync(Guid orderId, string provider, CancellationToken ct = default);

    /// <summary>
    /// Refunds part or all of one gateway payment through the gateway and books the refund. Null when the order does not exist.
    /// Validation error when the payment is not on this order or its provider cannot refund; conflict when the amount is more than
    /// can still be returned through that provider, or the gateway refuses.
    /// </summary>
    Task<RefundResultDto?> RefundAsync(Guid orderId, Guid paymentId, RefundStartInput input, string? actor = null, CancellationToken ct = default);

    /// <summary>
    /// Handles a gateway's webhook. Null when there is no such gateway (404). Throws <see cref="GatewayWebhookRejectedException"/> for a
    /// bad signature. Money the gateway confirms that cannot be matched or recorded is logged as an error and counted as unreconciled.
    /// </summary>
    Task<WebhookResultDto?> HandleWebhookAsync(string provider, GatewayWebhookRequest request, CancellationToken ct = default);
}

internal sealed class GatewayService(
    PiranhaDbContext db, IEnumerable<IPaymentGateway> gateways, IPaymentService payments, OrderMailer mailer, ILogger<GatewayService> logger) : IGatewayService
{
    private IPaymentGateway? Find(string provider) =>
        gateways.FirstOrDefault(g => string.Equals(g.Name, provider?.Trim(), StringComparison.OrdinalIgnoreCase));

    public async Task<PaymentSessionDto?> StartAsync(Guid orderId, string provider, CancellationToken ct = default)
    {
        var gateway = Find(provider) ?? throw new PiranhaValidationException(nameof(StartPaymentInput.Provider), "That is not a payment gateway.");

        var order = await db.Set<Order>().AsNoTracking().Where(o => o.Id == orderId)
            .Select(o => new { o.Number, o.Total, o.Stage, o.Currency, o.Email }).FirstOrDefaultAsync(ct);
        if (order is null) return null;
        if (order.Stage == OrderStage.Cancelled) throw new PiranhaConflictException("A cancelled order cannot take payment.");

        var sums = await db.Set<Payment>().AsNoTracking().Where(p => p.OrderId == orderId)
            .GroupBy(p => p.Kind).Select(g => new { Kind = g.Key, Sum = g.Sum(p => p.Amount) }).ToListAsync(ct);
        var outstanding = order.Total - (sums.Where(s => s.Kind == PaymentKind.Payment).Sum(s => s.Sum) - sums.Where(s => s.Kind == PaymentKind.Refund).Sum(s => s.Sum));
        if (outstanding <= 0) throw new PiranhaConflictException("Nothing is owed on this order.");

        GatewaySession session;
        try { session = await gateway.StartAsync(new GatewayStartRequest(orderId, OrderNumbers.Format(order.Number), outstanding, order.Currency, order.Email), ct); }
        catch (GatewayException ex)
        {
            logger.LogError(ex, "Gateway {Gateway} could not start a payment for order {OrderId}: {Reason}", gateway.Name, orderId, ex.Message);
            throw new PiranhaConflictException("The payment service could not start this payment. Please try again shortly.");
        }
        return new PaymentSessionDto(gateway.Name, outstanding, order.Currency, session.RedirectUrl, session.ProviderReference);
    }

    public async Task<WebhookResultDto?> HandleWebhookAsync(string provider, GatewayWebhookRequest request, CancellationToken ct = default)
    {
        var gateway = Find(provider);
        if (gateway is null) return null;

        var events = await gateway.ReadWebhookAsync(request, ct); // throws on a bad signature
        int recorded = 0, already = 0, unreconciled = 0;

        foreach (var e in events)
        {
            switch (await BookEventAsync(gateway, e, $"Confirmed by {gateway.DisplayName}", $"gateway:{gateway.Name}", ct))
            {
                case BookOutcome.Recorded: recorded++; break;
                case BookOutcome.AlreadyRecorded: already++; break;
                default: unreconciled++; break;
            }
        }

        return new WebhookResultDto(recorded, already, unreconciled);
    }

    private enum BookOutcome { Recorded, AlreadyRecorded, Unreconciled }

    /// <summary>
    /// Books one confirmed gateway event: nothing when it is already booked (gateways replay), the unreconciled list when the money
    /// cannot be booked. Used for webhooks and for refunds the admin started, so both end in exactly the same record.
    /// </summary>
    private async Task<BookOutcome> BookEventAsync(IPaymentGateway gateway, GatewayEvent e, string note, string actor, CancellationToken ct)
    {
        // A replay of an event we have already recorded is the normal case (gateways retry): answer success, record nothing.
        if (await db.Set<Payment>().AsNoTracking().AnyAsync(p => p.Provider == gateway.Name && p.Reference == e.Reference, ct))
            return BookOutcome.AlreadyRecorded;

        // Money already on the unreconciled list and dealt with by a person is not listed or counted again when the gateway replays it.
        if (await db.Set<UnreconciledPayment>().AsNoTracking().AnyAsync(u => u.Provider == gateway.Name && u.Reference == e.Reference && u.ResolvedUtc != null, ct))
            return BookOutcome.AlreadyRecorded;

        try
        {
            await GatewayBooking.BookAsync(db, payments, gateway.Name, note, actor, e, ct);

            // A retry of something that was waiting on the list books it, and closes the list entry.
            var now = DateTimeOffset.UtcNow;
            await db.Set<UnreconciledPayment>().Where(u => u.Provider == gateway.Name && u.Reference == e.Reference && u.ResolvedUtc == null)
                .ExecuteUpdateAsync(s => s.SetProperty(u => u.ResolvedUtc, (DateTimeOffset?)now).SetProperty(u => u.Resolution, "Booked when the gateway sent it again."), ct);
            return BookOutcome.Recorded;
        }
        catch (Exception ex) when (ex is PiranhaConflictException or PiranhaValidationException)
        {
            // A simultaneous replay can win the race for the same reference: that is "already recorded", not a problem.
            if (await db.Set<Payment>().AsNoTracking().AnyAsync(p => p.Provider == gateway.Name && p.Reference == e.Reference, ct)) return BookOutcome.AlreadyRecorded;

            // The gateway says money moved but we cannot book it (order cancelled, over-paid, wrong currency, unknown order).
            // Answering an error would only make the gateway retry for days; so it goes on the unreconciled list for a person, and in the log.
            logger.LogError(ex, "Gateway {Gateway} confirmed a {Kind} of {Amount} {Currency} (reference {Reference}) for order {OrderId} that could not be recorded: {Reason}",
                gateway.Name, e.Kind, e.Amount, e.Currency, e.Reference, e.OrderId, ex.Message);
            await RememberAsync(gateway.Name, e, ex.Message, ct);
            return BookOutcome.Unreconciled;
        }
    }

    public async Task<RefundResultDto?> RefundAsync(Guid orderId, Guid paymentId, RefundStartInput input, string? actor = null, CancellationToken ct = default)
    {
        ModelValidator.ValidateOrThrow(input);
        if (decimal.Round(input.Amount, 2) != input.Amount)
            throw new PiranhaValidationException(nameof(RefundStartInput.Amount), "Use at most two decimal places.");

        var order = await db.Set<Order>().AsNoTracking().Where(o => o.Id == orderId).Select(o => new { o.Currency }).FirstOrDefaultAsync(ct);
        if (order is null) return null;

        var payment = await db.Set<Payment>().AsNoTracking().FirstOrDefaultAsync(p => p.Id == paymentId && p.OrderId == orderId && p.Kind == PaymentKind.Payment, ct)
                      ?? throw new PiranhaValidationException("PaymentId", "That is not a payment on this order.");
        var gateway = gateways.OfType<IRefundingGateway>().FirstOrDefault(g => g.Name == payment.Provider);
        if (gateway is null || string.IsNullOrWhiteSpace(payment.Reference))
            throw new PiranhaValidationException("PaymentId", "That payment was not taken through a provider that can refund it. Record the refund you made yourself instead.");

        // Only what this provider took and has not already given back; the gateway enforces its own limit per payment as well.
        var sums = await db.Set<Payment>().AsNoTracking().Where(p => p.OrderId == orderId && p.Provider == gateway.Name)
            .GroupBy(p => p.Kind).Select(g => new { Kind = g.Key, Sum = g.Sum(p => p.Amount) }).ToListAsync(ct);
        var refundable = sums.Where(x => x.Kind == PaymentKind.Payment).Sum(x => x.Sum) - sums.Where(x => x.Kind == PaymentKind.Refund).Sum(x => x.Sum);
        if (input.Amount > refundable)
            throw new PiranhaConflictException($"That is more than can be refunded through {gateway.DisplayName}: {refundable:N2} {order.Currency} taken and not yet returned.");

        var key = string.IsNullOrWhiteSpace(input.IdempotencyKey) ? Guid.NewGuid().ToString("N") : input.IdempotencyKey.Trim();
        GatewayRefund result;
        try { result = await gateway.RefundAsync(new GatewayRefundRequest(orderId, payment.Reference!, input.Amount, order.Currency, $"{orderId:N}-{paymentId:N}-{input.Amount:0.00}-{key}"), ct); }
        catch (GatewayException ex)
        {
            logger.LogError(ex, "Gateway {Gateway} could not refund {Amount} on order {OrderId}: {Reason}", gateway.Name, input.Amount, orderId, ex.Message);
            throw new PiranhaConflictException("The payment service could not make this refund. Nothing has been refunded; check the payment in the provider's dashboard before trying again.");
        }

        // Still being processed: nothing is booked yet. Its webhook books it when it succeeds (and not at all if it fails).
        if (!result.Succeeded)
            return new RefundResultDto(gateway.Name, result.Reference, result.Status, result.Amount, result.Currency, "Pending");

        var outcome = await BookEventAsync(gateway,
            new GatewayEvent(GatewayEventKind.RefundConfirmed, orderId, result.Amount, result.Currency, result.Reference),
            $"Refunded through {gateway.DisplayName}", actor ?? $"gateway:{gateway.Name}", ct);
        return new RefundResultDto(gateway.Name, result.Reference, result.Status, result.Amount, result.Currency, outcome.ToString());
    }

    private async Task RememberAsync(string provider, GatewayEvent e, string reason, CancellationToken ct)
    {
        db.ChangeTracker.Clear();
        if (await db.Set<UnreconciledPayment>().AsNoTracking().AnyAsync(u => u.Provider == provider && u.Reference == e.Reference, ct))
        {
            // Already listed (the gateway is retrying): keep the reason current, add nothing.
            await db.Set<UnreconciledPayment>().Where(u => u.Provider == provider && u.Reference == e.Reference && u.ResolvedUtc == null)
                .ExecuteUpdateAsync(s => s.SetProperty(u => u.Reason, UnreconciledPaymentService.Truncate(reason, 500)), ct);
            return;
        }
        var listed = new UnreconciledPayment
        {
            Provider = provider, Kind = e.Kind, OrderId = e.OrderId, Amount = e.Amount, Currency = UnreconciledPaymentService.Truncate(e.Currency ?? "", 3),
            Reference = UnreconciledPaymentService.Truncate(e.Reference, 200), Reason = UnreconciledPaymentService.Truncate(reason, 500), CreatedUtc = DateTimeOffset.UtcNow,
        };
        db.Add(listed);
        try { await db.SaveChangesAsync(ct); }
        catch (DbUpdateException) { db.ChangeTracker.Clear(); return; } // a simultaneous replay listed it first, and that one tells the shop

        await mailer.UnreconciledAsync(listed, ct); // only when newly listed: a retried webhook for the same money does not email again
    }
}
