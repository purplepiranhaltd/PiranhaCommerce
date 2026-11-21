using System.ComponentModel.DataAnnotations;
using Microsoft.EntityFrameworkCore;
using PiranhaCommerce.Core.Domain;
using PiranhaCommerce.Core.Paging;
using PiranhaCommerce.Core.Validation;
using PiranhaCommerce.Data;

namespace PiranhaCommerce.Ecommerce;

/// <summary>Where a buyer's request to send goods back stands.</summary>
public enum ReturnRequestStatus
{
    /// <summary>Asked for, waiting for staff.</summary>
    Requested,
    /// <summary>Staff said yes. The goods have not been recorded as received yet.</summary>
    Approved,
    Rejected,
    /// <summary>The buyer withdrew it before staff answered.</summary>
    Cancelled,
    /// <summary>The goods arrived and staff recorded the return against this request.</summary>
    Received,
}

/// <summary>
/// A buyer's request to return goods they were sent. It moves no stock and no money: the return itself is recorded when the goods arrive
/// (and can name the request), and any refund is separate, as before. Open requests (Requested or Approved) hold back the quantity they
/// cover, so the same item cannot be asked for twice.
/// </summary>
public class ReturnRequest : Entity
{
    public Guid OrderId { get; set; }
    public ReturnRequestStatus Status { get; set; }
    /// <summary>A short reason, kept like a return's reason.</summary>
    public required string Reason { get; set; }
    /// <summary>The buyer's free text. Personal data: cleared when they are erased.</summary>
    public string? Note { get; set; }
    /// <summary>What staff told the buyer. Cleared on erasure too.</summary>
    public string? ResponseNote { get; set; }
    public DateTimeOffset RequestedUtc { get; set; }
    public DateTimeOffset? RespondedUtc { get; set; }
    public string? RespondedBy { get; set; }
    public Guid? ReceivedReturnId { get; set; }
    public List<ReturnRequestLine> Lines { get; set; } = [];
}

public class ReturnRequestLine : Entity
{
    public Guid ReturnRequestId { get; set; }
    public Guid OrderLineId { get; set; }
    public int Quantity { get; set; }
}

public sealed class ReturnRequestLineInput
{
    public Guid OrderLineId { get; set; }
    [Range(1, OrderLineQuantity.Max)] public int Quantity { get; set; }
}

public sealed class ReturnRequestInput : IValidatableObject
{
    [Required, StringLength(200, MinimumLength = 1)] public string Reason { get; set; } = "";
    [StringLength(1000)] public string? Note { get; set; }
    public List<ReturnRequestLineInput> Lines { get; set; } = [];

    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        if (Lines is null || Lines.Count == 0) yield return new ValidationResult("Choose at least one item to send back.", [nameof(Lines)]);
        else if (Lines.Count > 200) yield return new ValidationResult("Too many items.", [nameof(Lines)]);
        else if (Lines.Any(l => l is null || l.Quantity < 1 || l.Quantity > OrderLineQuantity.Max))
            yield return new ValidationResult($"Each quantity must be between 1 and {OrderLineQuantity.Max}.", [nameof(Lines)]);
    }
}

/// <summary>Staff's answer to a request. The note is shown to the buyer.</summary>
public sealed class ReturnResponseInput
{
    [StringLength(1000)] public string? Note { get; set; }
}

public sealed record ReturnRequestLineDto(Guid OrderLineId, string Name, string? Sku, int Quantity);

public sealed record ReturnRequestDto(
    Guid Id, Guid OrderId, string OrderReference, ReturnRequestStatus Status, string Reason, string? Note, string? ResponseNote,
    DateTimeOffset RequestedUtc, DateTimeOffset? RespondedUtc, string? RespondedBy, Guid? ReceivedReturnId, IReadOnlyList<ReturnRequestLineDto> Lines)
{
    /// <summary>What a buyer sees: not which staff member answered.</summary>
    public ReturnRequestDto ForShopper() => this with { RespondedBy = null };
}

public interface IReturnRequestService
{
    /// <param name="status">null for every status. Newest first.</param>
    Task<PagedResult<ReturnRequestDto>> ListAsync(ReturnRequestStatus? status, int? page, int? pageSize, CancellationToken ct = default);

    Task<ReturnRequestDto?> GetAsync(Guid id, CancellationToken ct = default);

    /// <summary>Newest first. Null when the order does not exist.</summary>
    Task<IReadOnlyList<ReturnRequestDto>?> ListForOrderAsync(Guid orderId, CancellationToken ct = default);

    /// <summary>
    /// Asks to send items back. Only goods that were sent and have not already come back or been asked for can be requested; a cancelled order
    /// cannot. Conflict otherwise. Null when the order does not exist. Who may ask (the order's owner) is the caller's business.
    /// </summary>
    Task<ReturnRequestDto?> RequestAsync(Guid orderId, ReturnRequestInput input, CancellationToken ct = default);

    /// <summary>Says yes, and emails the buyer. Conflict unless it is still Requested. Null when not found.</summary>
    Task<ReturnRequestDto?> ApproveAsync(Guid id, ReturnResponseInput input, string? actor = null, CancellationToken ct = default);

    /// <summary>Says no, and emails the buyer. Conflict unless it is still Requested. Null when not found.</summary>
    Task<ReturnRequestDto?> RejectAsync(Guid id, ReturnResponseInput input, string? actor = null, CancellationToken ct = default);

    /// <summary>The buyer withdraws a request nobody has answered. False when it is not that order's; conflict once answered.</summary>
    Task<bool> CancelAsync(Guid orderId, Guid id, CancellationToken ct = default);
}

internal sealed class ReturnRequestService(PiranhaDbContext db, OrderMailer mailer) : IReturnRequestService
{
    /// <summary>Most requests that can be waiting on one order, so a form cannot be used to bury it.</summary>
    public const int MaxOpenPerOrder = 20;

    private static readonly ReturnRequestStatus[] Open = [ReturnRequestStatus.Requested, ReturnRequestStatus.Approved];

    public async Task<PagedResult<ReturnRequestDto>> ListAsync(ReturnRequestStatus? status, int? page, int? pageSize, CancellationToken ct = default)
    {
        var (p, size) = Paging.Normalize(page, pageSize);
        var query = db.Set<ReturnRequest>().AsNoTracking().Where(r => status == null || r.Status == status);
        var total = await query.CountAsync(ct);
        var rows = await query.OrderByDescending(r => r.RequestedUtc).ThenBy(r => r.Id).Skip((p - 1) * size).Take(size).ToListAsync(ct);
        return new PagedResult<ReturnRequestDto>(await ToDtosAsync(rows, ct), p, size, total);
    }

    public async Task<ReturnRequestDto?> GetAsync(Guid id, CancellationToken ct = default)
    {
        var row = await db.Set<ReturnRequest>().AsNoTracking().FirstOrDefaultAsync(r => r.Id == id, ct);
        return row is null ? null : (await ToDtosAsync([row], ct)).Single();
    }

    public async Task<IReadOnlyList<ReturnRequestDto>?> ListForOrderAsync(Guid orderId, CancellationToken ct = default)
    {
        if (!await db.Set<Order>().AnyAsync(o => o.Id == orderId, ct)) return null;
        var rows = await db.Set<ReturnRequest>().AsNoTracking().Where(r => r.OrderId == orderId).OrderByDescending(r => r.RequestedUtc).ThenBy(r => r.Id).ToListAsync(ct);
        return await ToDtosAsync(rows, ct);
    }

    public async Task<ReturnRequestDto?> RequestAsync(Guid orderId, ReturnRequestInput input, CancellationToken ct = default)
    {
        ModelValidator.ValidateOrThrow(input);
        if (input.Lines.Select(l => l.OrderLineId).Distinct().Count() != input.Lines.Count)
            throw new PiranhaValidationException(nameof(ReturnRequestInput.Lines), "List each item once.");

        var stage = await db.Set<Order>().AsNoTracking().Where(o => o.Id == orderId).Select(o => (OrderStage?)o.Stage).FirstOrDefaultAsync(ct);
        if (stage is null) return null;
        if (stage == OrderStage.Cancelled) throw new PiranhaConflictException("This order was cancelled, so there is nothing to send back.");

        var requestId = Guid.NewGuid();
        var strategy = db.Database.CreateExecutionStrategy();
        try
        {
            await strategy.ExecuteInTransactionAsync(
                async token =>
                {
                    db.ChangeTracker.Clear();
                    // The order's row lock first, as shipping and recording returns do: two requests for the same item are then checked one after the other.
                    var now = DateTimeOffset.UtcNow;
                    await db.Set<Order>().Where(o => o.Id == orderId).ExecuteUpdateAsync(s => s.SetProperty(o => o.UpdatedUtc, (DateTimeOffset?)now), token);

                    if (await db.Set<ReturnRequest>().AsNoTracking().CountAsync(r => r.OrderId == orderId && Open.Contains(r.Status), token) >= MaxOpenPerOrder)
                        throw new PiranhaConflictException("There are already several requests waiting on this order. Please wait for an answer.");

                    var lines = await db.Set<OrderLine>().AsNoTracking().Where(l => l.OrderId == orderId).ToListAsync(token);
                    var held = await (from rl in db.Set<ReturnRequestLine>().AsNoTracking()
                                      join r in db.Set<ReturnRequest>().AsNoTracking() on rl.ReturnRequestId equals r.Id
                                      where r.OrderId == orderId && Open.Contains(r.Status)
                                      select new { rl.OrderLineId, rl.Quantity }).ToListAsync(token);

                    foreach (var item in input.Lines)
                    {
                        var line = lines.FirstOrDefault(l => l.Id == item.OrderLineId)
                                   ?? throw new PiranhaValidationException(nameof(ReturnRequestInput.Lines), "One of those items is not on this order.");
                        var requested = held.Where(h => h.OrderLineId == line.Id).Sum(h => h.Quantity);
                        var available = line.QuantityShipped - line.QuantityReturned - requested;
                        if (item.Quantity > available)
                            throw new PiranhaConflictException(line.QuantityShipped == 0
                                ? $"\"{line.Name}\" has not been sent yet, so it cannot be sent back."
                                : $"Only {Math.Max(0, available)} of \"{line.Name}\" can still be sent back (some may already be returned or asked for).");
                    }

                    db.Add(new ReturnRequest
                    {
                        Id = requestId, OrderId = orderId, Status = ReturnRequestStatus.Requested, Reason = input.Reason.Trim(),
                        Note = string.IsNullOrWhiteSpace(input.Note) ? null : input.Note.Trim(), RequestedUtc = now,
                        Lines = input.Lines.Select(l => new ReturnRequestLine { ReturnRequestId = requestId, OrderLineId = l.OrderLineId, Quantity = l.Quantity }).ToList(),
                    });
                    await db.SaveChangesAsync(token);
                    return true;
                },
                async token => await db.Set<ReturnRequest>().AsNoTracking().AnyAsync(r => r.Id == requestId, token),
                ct);
        }
        catch (DbUpdateException)
        {
            db.ChangeTracker.Clear();
            throw new PiranhaConflictException("The request could not be saved. Please try again.");
        }

        var dto = (await GetAsync(requestId, ct))!;
        await mailer.ReturnRequestedAsync(dto, ct); // tells the shop; never able to fail the request
        return dto;
    }

    public Task<ReturnRequestDto?> ApproveAsync(Guid id, ReturnResponseInput input, string? actor = null, CancellationToken ct = default)
        => AnswerAsync(id, input, ReturnRequestStatus.Approved, actor, ct);

    public Task<ReturnRequestDto?> RejectAsync(Guid id, ReturnResponseInput input, string? actor = null, CancellationToken ct = default)
        => AnswerAsync(id, input, ReturnRequestStatus.Rejected, actor, ct);

    private async Task<ReturnRequestDto?> AnswerAsync(Guid id, ReturnResponseInput input, ReturnRequestStatus to, string? actor, CancellationToken ct)
    {
        ModelValidator.ValidateOrThrow(input);
        var note = string.IsNullOrWhiteSpace(input.Note) ? null : input.Note.Trim();
        var now = DateTimeOffset.UtcNow;

        // One conditional update: of two staff answering at once, only one changes it.
        var changed = await db.Set<ReturnRequest>().Where(r => r.Id == id && r.Status == ReturnRequestStatus.Requested)
            .ExecuteUpdateAsync(s => s.SetProperty(r => r.Status, to).SetProperty(r => r.ResponseNote, note)
                .SetProperty(r => r.RespondedUtc, (DateTimeOffset?)now).SetProperty(r => r.RespondedBy, actor), ct);
        if (changed == 0)
        {
            var current = await db.Set<ReturnRequest>().AsNoTracking().Where(r => r.Id == id).Select(r => (ReturnRequestStatus?)r.Status).FirstOrDefaultAsync(ct);
            if (current is null) return null;
            throw new PiranhaConflictException($"That request is already {current.Value.ToString().ToLowerInvariant()}.");
        }

        var dto = (await GetAsync(id, ct))!;
        await mailer.ReturnAnsweredAsync(dto, ct);
        return dto;
    }

    public async Task<bool> CancelAsync(Guid orderId, Guid id, CancellationToken ct = default)
    {
        var changed = await db.Set<ReturnRequest>().Where(r => r.Id == id && r.OrderId == orderId && r.Status == ReturnRequestStatus.Requested)
            .ExecuteUpdateAsync(s => s.SetProperty(r => r.Status, ReturnRequestStatus.Cancelled).SetProperty(r => r.RespondedUtc, (DateTimeOffset?)DateTimeOffset.UtcNow), ct);
        if (changed > 0) return true;
        var current = await db.Set<ReturnRequest>().AsNoTracking().Where(r => r.Id == id && r.OrderId == orderId).Select(r => (ReturnRequestStatus?)r.Status).FirstOrDefaultAsync(ct);
        if (current is null) return false;
        throw new PiranhaConflictException($"That request is already {current.Value.ToString().ToLowerInvariant()}, so it cannot be withdrawn.");
    }

    private async Task<IReadOnlyList<ReturnRequestDto>> ToDtosAsync(IReadOnlyList<ReturnRequest> rows, CancellationToken ct)
    {
        if (rows.Count == 0) return [];
        var ids = rows.Select(r => r.Id).ToList();
        var orderIds = rows.Select(r => r.OrderId).Distinct().ToList();
        var numbers = await db.Set<Order>().AsNoTracking().Where(o => orderIds.Contains(o.Id)).Select(o => new { o.Id, o.Number }).ToDictionaryAsync(o => o.Id, o => o.Number, ct);
        var lines = await (from rl in db.Set<ReturnRequestLine>().AsNoTracking()
                           join ol in db.Set<OrderLine>().AsNoTracking() on rl.OrderLineId equals ol.Id
                           where ids.Contains(rl.ReturnRequestId)
                           orderby ol.Position
                           select new { rl.ReturnRequestId, rl.OrderLineId, ol.Name, ol.Sku, rl.Quantity }).ToListAsync(ct);
        return rows.Select(r => new ReturnRequestDto(
            r.Id, r.OrderId, OrderNumbers.Format(numbers.GetValueOrDefault(r.OrderId)), r.Status, r.Reason, r.Note, r.ResponseNote,
            r.RequestedUtc, r.RespondedUtc, r.RespondedBy, r.ReceivedReturnId,
            lines.Where(l => l.ReturnRequestId == r.Id).Select(l => new ReturnRequestLineDto(l.OrderLineId, l.Name, l.Sku, l.Quantity)).ToList())).ToList();
    }
}
