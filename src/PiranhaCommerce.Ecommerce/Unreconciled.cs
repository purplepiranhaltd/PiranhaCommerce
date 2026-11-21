using System.ComponentModel.DataAnnotations;
using Microsoft.EntityFrameworkCore;
using PiranhaCommerce.Core.Domain;
using PiranhaCommerce.Core.Paging;
using PiranhaCommerce.Core.Validation;
using PiranhaCommerce.Data;

namespace PiranhaCommerce.Ecommerce;

/// <summary>
/// Money a gateway says moved that the core could not book against an order (the order was cancelled, the payment would over-pay it,
/// the currency differs, the order is unknown). The gateway has been answered, so it will not retry: this row is how a person finds out.
/// It stays open until someone resolves it, or a retry books it.
/// </summary>
public class UnreconciledPayment : Entity
{
    public required string Provider { get; set; }
    public GatewayEventKind Kind { get; set; }

    /// <summary>The order the gateway named. A plain value, not a link: it may not exist.</summary>
    public Guid OrderId { get; set; }
    public decimal Amount { get; set; }
    public required string Currency { get; set; }

    /// <summary>The gateway's id for the payment or refund. Unique per provider, so a replayed webhook never lists the same money twice.</summary>
    public required string Reference { get; set; }

    /// <summary>Why it could not be booked, as of the last attempt.</summary>
    public required string Reason { get; set; }
    public DateTimeOffset CreatedUtc { get; set; }
    public DateTimeOffset? ResolvedUtc { get; set; }
    public string? ResolvedBy { get; set; }

    /// <summary>What was done about it ("refunded in the Stripe dashboard", "booked by retry").</summary>
    public string? Resolution { get; set; }
}

public sealed class ResolveUnreconciledInput
{
    [Required, StringLength(500)] public string Resolution { get; set; } = "";
}

public sealed record UnreconciledPaymentDto(
    Guid Id, string Provider, GatewayEventKind Kind, Guid OrderId, string? OrderReference, decimal Amount, string Currency, string Reference,
    string Reason, DateTimeOffset CreatedUtc, DateTimeOffset? ResolvedUtc, string? ResolvedBy, string? Resolution);

public interface IUnreconciledPaymentService
{
    /// <param name="resolved">null for both, false for what still needs attention, true for what has been dealt with. Newest first.</param>
    Task<PagedResult<UnreconciledPaymentDto>> ListAsync(int? page, int? pageSize, bool? resolved, CancellationToken ct = default);

    /// <summary>How many are still open (for a badge or a health check).</summary>
    Task<int> OpenCountAsync(CancellationToken ct = default);

    Task<UnreconciledPaymentDto?> GetAsync(Guid id, CancellationToken ct = default);

    /// <summary>
    /// Marks it dealt with, with a note of what was done (the money is not moved: that happened at the gateway or by hand). Conflict when
    /// it is already resolved. Null when not found.
    /// </summary>
    Task<UnreconciledPaymentDto?> ResolveAsync(Guid id, ResolveUnreconciledInput input, string? actor = null, CancellationToken ct = default);

    /// <summary>
    /// Tries to book it again (for example after the order was corrected). On success it is resolved with the new payment. Conflict, with
    /// the reason, when it still cannot be booked. Null when not found.
    /// </summary>
    Task<UnreconciledPaymentDto?> RetryAsync(Guid id, string? actor = null, CancellationToken ct = default);
}

internal static class GatewayBooking
{
    /// <summary>Books a confirmed gateway event against its order. Throws conflict or validation when it cannot be booked.</summary>
    public static async Task BookAsync(PiranhaDbContext db, IPaymentService payments, string provider, string note, string actor, GatewayEvent e, CancellationToken ct)
    {
        var orderCurrency = await db.Set<Order>().AsNoTracking().Where(o => o.Id == e.OrderId).Select(o => o.Currency).FirstOrDefaultAsync(ct);
        if (orderCurrency is null) throw new PiranhaConflictException("No such order.");
        if (!string.Equals(orderCurrency, e.Currency, StringComparison.OrdinalIgnoreCase))
            throw new PiranhaConflictException($"Currency {e.Currency} does not match the order's {orderCurrency}.");

        if (e.Kind is GatewayEventKind.DisputeOpened or GatewayEventKind.DisputeWon or GatewayEventKind.RefundFailed)
            throw new PiranhaConflictException("This is not money to book: deal with it at the provider, then resolve it here.");

        // A lost dispute is not the shop refunding: the buyer is not emailed as if they had been.
        var input = new PaymentInput { Amount = e.Amount, Provider = provider, Reference = e.Reference, Note = note, SuppressBuyerEmail = e.Kind == GatewayEventKind.DisputeLost };
        var saved = e.Kind == GatewayEventKind.PaymentConfirmed
            ? await payments.RecordPaymentAsync(e.OrderId, input, actor, ct)
            : await payments.RecordRefundAsync(e.OrderId, input, actor, ct);
        if (saved is null) throw new PiranhaConflictException("No such order.");
    }
}

internal sealed class UnreconciledPaymentService(PiranhaDbContext db, IPaymentService payments) : IUnreconciledPaymentService
{
    public async Task<PagedResult<UnreconciledPaymentDto>> ListAsync(int? page, int? pageSize, bool? resolved, CancellationToken ct = default)
    {
        var (p, size) = Paging.Normalize(page, pageSize);
        var query = db.Set<UnreconciledPayment>().AsNoTracking().AsQueryable();
        if (resolved is { } r) query = r ? query.Where(u => u.ResolvedUtc != null) : query.Where(u => u.ResolvedUtc == null);
        var total = await query.CountAsync(ct);
        var rows = await query.OrderByDescending(u => u.CreatedUtc).ThenBy(u => u.Id).Skip((p - 1) * size).Take(size).ToListAsync(ct);
        return new PagedResult<UnreconciledPaymentDto>(await ToDtosAsync(rows, ct), p, size, total);
    }

    public Task<int> OpenCountAsync(CancellationToken ct = default) =>
        db.Set<UnreconciledPayment>().AsNoTracking().CountAsync(u => u.ResolvedUtc == null, ct);

    public async Task<UnreconciledPaymentDto?> GetAsync(Guid id, CancellationToken ct = default)
    {
        var row = await db.Set<UnreconciledPayment>().AsNoTracking().FirstOrDefaultAsync(u => u.Id == id, ct);
        return row is null ? null : (await ToDtosAsync([row], ct))[0];
    }

    public async Task<UnreconciledPaymentDto?> ResolveAsync(Guid id, ResolveUnreconciledInput input, string? actor = null, CancellationToken ct = default)
    {
        ModelValidator.ValidateOrThrow(input);
        if (string.IsNullOrWhiteSpace(input.Resolution)) throw new PiranhaValidationException(nameof(input.Resolution), "Say what was done about it.");
        var note = input.Resolution.Trim();
        var now = DateTimeOffset.UtcNow;

        // Conditional: two people resolving the same item cannot both succeed, and the second hears about it.
        var changed = await db.Set<UnreconciledPayment>().Where(u => u.Id == id && u.ResolvedUtc == null)
            .ExecuteUpdateAsync(s => s.SetProperty(u => u.ResolvedUtc, (DateTimeOffset?)now).SetProperty(u => u.ResolvedBy, actor).SetProperty(u => u.Resolution, note), ct);
        if (changed == 0)
        {
            if (!await db.Set<UnreconciledPayment>().AnyAsync(u => u.Id == id, ct)) return null;
            throw new PiranhaConflictException("That has already been resolved.");
        }
        return await GetAsync(id, ct);
    }

    public async Task<UnreconciledPaymentDto?> RetryAsync(Guid id, string? actor = null, CancellationToken ct = default)
    {
        var row = await db.Set<UnreconciledPayment>().AsNoTracking().FirstOrDefaultAsync(u => u.Id == id, ct);
        if (row is null) return null;
        if (row.ResolvedUtc is not null) throw new PiranhaConflictException("That has already been resolved.");

        var e = new GatewayEvent(row.Kind, row.OrderId, row.Amount, row.Currency, row.Reference);
        try
        {
            await GatewayBooking.BookAsync(db, payments, row.Provider, $"Confirmed by {row.Provider} (booked on retry)", actor ?? $"gateway:{row.Provider}", e, ct);
        }
        catch (Exception ex) when (ex is PiranhaConflictException or PiranhaValidationException)
        {
            // Booked by someone else in the meantime (the same reference exists): that is a resolution, not a failure.
            if (!await db.Set<Payment>().AsNoTracking().AnyAsync(p => p.Provider == row.Provider && p.Reference == row.Reference, ct))
            {
                db.ChangeTracker.Clear();
                var reason = Truncate(ex.Message, 500);
                await db.Set<UnreconciledPayment>().Where(u => u.Id == id).ExecuteUpdateAsync(s => s.SetProperty(u => u.Reason, reason), ct);
                throw new PiranhaConflictException($"Still cannot be booked: {ex.Message}");
            }
        }

        var now = DateTimeOffset.UtcNow;
        await db.Set<UnreconciledPayment>().Where(u => u.Id == id && u.ResolvedUtc == null)
            .ExecuteUpdateAsync(s => s.SetProperty(u => u.ResolvedUtc, (DateTimeOffset?)now).SetProperty(u => u.ResolvedBy, actor)
                .SetProperty(u => u.Resolution, "Booked against the order."), ct);
        return await GetAsync(id, ct);
    }

    private async Task<IReadOnlyList<UnreconciledPaymentDto>> ToDtosAsync(IReadOnlyList<UnreconciledPayment> rows, CancellationToken ct)
    {
        var ids = rows.Select(r => r.OrderId).Distinct().ToList();
        var numbers = await db.Set<Order>().AsNoTracking().Where(o => ids.Contains(o.Id)).Select(o => new { o.Id, o.Number }).ToListAsync(ct);
        var byId = numbers.ToDictionary(n => n.Id, n => OrderNumbers.Format(n.Number));
        return rows.Select(r => new UnreconciledPaymentDto(r.Id, r.Provider, r.Kind, r.OrderId, byId.GetValueOrDefault(r.OrderId), r.Amount, r.Currency,
            r.Reference, r.Reason, r.CreatedUtc, r.ResolvedUtc, r.ResolvedBy, r.Resolution)).ToList();
    }

    internal static string Truncate(string s, int max) => s.Length <= max ? s : s[..max];
}
