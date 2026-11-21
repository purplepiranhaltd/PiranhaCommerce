using Microsoft.EntityFrameworkCore;
using PiranhaCommerce.Core.Domain;
using PiranhaCommerce.Core.Paging;
using PiranhaCommerce.Data;

namespace PiranhaCommerce.Ecommerce;

public enum DisputeStatus { Open, Won, Lost }

/// <summary>
/// A buyer disputed a card payment with their bank (a chargeback). The provider reports it; the shop has until <see cref="RespondByUtc"/> to
/// answer in the provider's own dashboard. Winning moves no money here (the provider gives it back). Losing is booked as a refund on the order,
/// because the money has gone back to the buyer's card.
/// </summary>
public class PaymentDispute : Entity
{
    public required string Provider { get; set; }
    /// <summary>The provider's id for the dispute. Unique per provider, so a replayed webhook never records one twice.</summary>
    public required string Reference { get; set; }
    public Guid OrderId { get; set; }
    public decimal Amount { get; set; }
    public required string Currency { get; set; }
    public DisputeStatus Status { get; set; }
    /// <summary>The reason the bank gave, as the provider reports it (e.g. fraudulent, product_not_received).</summary>
    public string? Reason { get; set; }
    public DateTimeOffset OpenedUtc { get; set; }
    public DateTimeOffset? RespondByUtc { get; set; }
    public DateTimeOffset? ClosedUtc { get; set; }
}

public sealed record PaymentDisputeDto(
    Guid Id, string Provider, string Reference, Guid OrderId, string OrderReference, decimal Amount, string Currency, DisputeStatus Status,
    string? Reason, DateTimeOffset OpenedUtc, DateTimeOffset? RespondByUtc, DateTimeOffset? ClosedUtc);

public interface IDisputeService
{
    /// <param name="status">null for all. Newest first.</param>
    Task<PagedResult<PaymentDisputeDto>> ListAsync(DisputeStatus? status, int? page, int? pageSize, CancellationToken ct = default);

    /// <summary>Newest first. Null when the order does not exist.</summary>
    Task<IReadOnlyList<PaymentDisputeDto>?> ListForOrderAsync(Guid orderId, CancellationToken ct = default);

    /// <summary>How many are still open (for a badge).</summary>
    Task<int> OpenCountAsync(CancellationToken ct = default);
}

internal sealed class DisputeService(PiranhaDbContext db) : IDisputeService
{
    public async Task<PagedResult<PaymentDisputeDto>> ListAsync(DisputeStatus? status, int? page, int? pageSize, CancellationToken ct = default)
    {
        var (p, size) = Paging.Normalize(page, pageSize);
        var query = db.Set<PaymentDispute>().AsNoTracking().Where(d => status == null || d.Status == status);
        var total = await query.CountAsync(ct);
        var rows = await query.OrderByDescending(d => d.OpenedUtc).ThenBy(d => d.Id).Skip((p - 1) * size).Take(size).ToListAsync(ct);
        return new PagedResult<PaymentDisputeDto>(await ToDtosAsync(rows, ct), p, size, total);
    }

    public async Task<IReadOnlyList<PaymentDisputeDto>?> ListForOrderAsync(Guid orderId, CancellationToken ct = default)
    {
        if (!await db.Set<Order>().AnyAsync(o => o.Id == orderId, ct)) return null;
        var rows = await db.Set<PaymentDispute>().AsNoTracking().Where(d => d.OrderId == orderId).OrderByDescending(d => d.OpenedUtc).ThenBy(d => d.Id).ToListAsync(ct);
        return await ToDtosAsync(rows, ct);
    }

    public Task<int> OpenCountAsync(CancellationToken ct = default) => db.Set<PaymentDispute>().AsNoTracking().CountAsync(d => d.Status == DisputeStatus.Open, ct);

    private async Task<IReadOnlyList<PaymentDisputeDto>> ToDtosAsync(IReadOnlyList<PaymentDispute> rows, CancellationToken ct)
    {
        var ids = rows.Select(r => r.OrderId).Distinct().ToList();
        var numbers = await db.Set<Order>().AsNoTracking().Where(o => ids.Contains(o.Id)).Select(o => new { o.Id, o.Number }).ToDictionaryAsync(o => o.Id, o => o.Number, ct);
        return rows.Select(d => new PaymentDisputeDto(d.Id, d.Provider, d.Reference, d.OrderId, OrderNumbers.Format(numbers.GetValueOrDefault(d.OrderId)), d.Amount, d.Currency,
            d.Status, d.Reason, d.OpenedUtc, d.RespondByUtc, d.ClosedUtc)).ToList();
    }
}
