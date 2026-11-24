using System.ComponentModel.DataAnnotations;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using PiranhaCommerce.Core.Files;
using PiranhaCommerce.Core.Domain;
using PiranhaCommerce.Core.Paging;
using PiranhaCommerce.Core.Validation;
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
    /// <summary>What the provider charged the shop for the dispute (a cost of doing business, not part of the order), when it said.</summary>
    public decimal? Fee { get; set; }
    /// <summary>When the shop's evidence was sent to the bank. Null until then; once set it cannot be changed.</summary>
    public DateTimeOffset? EvidenceSubmittedUtc { get; set; }
}

public sealed record PaymentDisputeDto(
    Guid Id, string Provider, string Reference, Guid OrderId, string OrderReference, decimal Amount, string Currency, DisputeStatus Status,
    string? Reason, DateTimeOffset OpenedUtc, DateTimeOffset? RespondByUtc, DateTimeOffset? ClosedUtc, decimal? Fee = null, DateTimeOffset? EvidenceSubmittedUtc = null);

public sealed class DisputeEvidenceInput
{
    [StringLength(5000)] public string? ProductDescription { get; set; }
    [StringLength(200)] public string? CustomerName { get; set; }
    [EmailAddress, StringLength(256)] public string? CustomerEmail { get; set; }
    [StringLength(500)] public string? ShippingAddress { get; set; }
    [StringLength(100)] public string? ShippingCarrier { get; set; }
    [StringLength(100)] public string? TrackingNumber { get; set; }
    public DateOnly? ShippingDate { get; set; }
    /// <summary>Anything else that shows the buyer received what they paid for.</summary>
    [StringLength(10000)] public string? Notes { get; set; }
    /// <summary>False saves it at the provider and can be changed; true sends it to the bank and is final.</summary>
    public bool Submit { get; set; }
}

public interface IDisputeService
{
    /// <summary>
    /// Sends evidence for an open dispute through its provider. Validation error when the provider cannot take evidence from here, nothing is
    /// filled in, or the dispute has already been answered; conflict when it is no longer open. Null when the dispute does not exist.
    /// </summary>
    Task<PaymentDisputeDto?> SubmitEvidenceAsync(Guid disputeId, DisputeEvidenceInput input, CancellationToken ct = default);

    /// <param name="status">null for all. Newest first.</param>
    Task<PagedResult<PaymentDisputeDto>> ListAsync(DisputeStatus? status, int? page, int? pageSize, CancellationToken ct = default);

    /// <summary>Newest first. Null when the order does not exist.</summary>
    Task<IReadOnlyList<PaymentDisputeDto>?> ListForOrderAsync(Guid orderId, CancellationToken ct = default);

    /// <summary>The documents attached to a dispute. Null when the dispute does not exist.</summary>
    Task<IReadOnlyList<DisputeFileDto>?> ListFilesAsync(Guid disputeId, CancellationToken ct = default);

    /// <summary>
    /// Attaches a PDF, JPEG or PNG document (one per kind) to an open dispute whose evidence has not been sent. It goes to the provider with the next save or send.
    /// Validation error for a wrong type, an empty or too big file; conflict when the dispute is decided, the evidence has been sent, or there is one of that kind already. Null when the dispute does not exist.
    /// </summary>
    Task<DisputeFileDto?> AddFileAsync(Guid disputeId, DisputeEvidenceKind kind, Stream content, string fileName, string? actor, CancellationToken ct = default);

    Task<DisputeFileDownload?> OpenFileAsync(Guid disputeId, Guid fileId, CancellationToken ct = default);

    /// <summary>Removes a document; conflict once the evidence has been sent.</summary>
    Task<bool> DeleteFileAsync(Guid disputeId, Guid fileId, CancellationToken ct = default);

    /// <summary>How many are still open (for a badge).</summary>
    Task<int> OpenCountAsync(CancellationToken ct = default);
}

internal sealed partial class DisputeService(PiranhaCommerceDbContext db, IEnumerable<IDisputeEvidenceGateway> evidenceGateways, IFileStore files, IOptions<DisputeFileOptions> fileOptions) : IDisputeService
{
    public async Task<PaymentDisputeDto?> SubmitEvidenceAsync(Guid disputeId, DisputeEvidenceInput input, CancellationToken ct = default)
    {
        ModelValidator.ValidateOrThrow(input);
        var d = await db.Set<PaymentDispute>().AsNoTracking().FirstOrDefaultAsync(x => x.Id == disputeId, ct);
        if (d is null) return null;
        if (d.Status != DisputeStatus.Open) throw new PiranhaCommerceConflictException("This dispute has already been decided.");
        if (d.EvidenceSubmittedUtc is not null) throw new PiranhaCommerceConflictException("Evidence has already been sent to the bank for this dispute and cannot be changed.");
        var documents = await DocumentsAsync(d.Id, ct);
        if (documents.Count == 0 && string.IsNullOrWhiteSpace(input.ProductDescription + input.CustomerName + input.CustomerEmail + input.ShippingAddress + input.ShippingCarrier + input.TrackingNumber + input.Notes) && input.ShippingDate is null)
            throw new PiranhaCommerceValidationException(nameof(DisputeEvidenceInput.Notes), "Give at least some evidence.");
        var gateway = evidenceGateways.FirstOrDefault(g => string.Equals(g.Name, d.Provider, StringComparison.OrdinalIgnoreCase))
            ?? throw new PiranhaCommerceValidationException(nameof(PaymentDisputeDto.Provider), $"Evidence for {d.Provider} disputes has to be sent in the provider's own dashboard.");

        try
        {
            await gateway.SubmitEvidenceAsync(d.Reference, new DisputeEvidence(
                Clean(input.ProductDescription), Clean(input.CustomerName), Clean(input.CustomerEmail), Clean(input.ShippingAddress), Clean(input.ShippingCarrier),
                Clean(input.TrackingNumber), input.ShippingDate, Clean(input.Notes), input.Submit, documents), ct);
        }
        catch (GatewayException ex)
        {
            throw new PiranhaCommerceConflictException("The payment provider did not accept the evidence: " + ex.Message);
        }

        if (input.Submit)
            await db.Set<PaymentDispute>().Where(x => x.Id == d.Id && x.EvidenceSubmittedUtc == null)
                .ExecuteUpdateAsync(s => s.SetProperty(x => x.EvidenceSubmittedUtc, (DateTimeOffset?)DateTimeOffset.UtcNow), ct);
        return (await ToDtosAsync([await db.Set<PaymentDispute>().AsNoTracking().FirstAsync(x => x.Id == d.Id, ct)], ct))[0];
    }

    private static string? Clean(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();

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
            d.Status, d.Reason, d.OpenedUtc, d.RespondByUtc, d.ClosedUtc, d.Fee, d.EvidenceSubmittedUtc)).ToList();
    }
}
