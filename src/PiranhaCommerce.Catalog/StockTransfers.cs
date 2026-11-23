using System.ComponentModel.DataAnnotations;
using PiranhaCommerce.Core.Audit;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using PiranhaCommerce.Core.Domain;
using PiranhaCommerce.Core.Identity;
using PiranhaCommerce.Core.Paging;
using PiranhaCommerce.Core.Validation;
using PiranhaCommerce.Data;

namespace PiranhaCommerce.Catalog;

// ---- Entities --------------------------------------------------------------------------------------------------------------

public enum StockTransferStatus { InTransit = 0, Received = 1, Cancelled = 2 }

/// <summary>
/// Stock moved from one location to another by two people: one sends it (and the stock leaves the sending level at once), another receives it
/// (and it joins the receiving level). Between the two it is "in transit": counted on neither shelf, but listed here so it is never lost.
/// </summary>
[Audited]
public class StockTransfer : Entity
{
    public long Number { get; set; }
    public Guid FromLocationId { get; set; }
    public Guid ToLocationId { get; set; }
    public StockTransferStatus Status { get; set; }
    public string? Note { get; set; }

    public DateTimeOffset SentUtc { get; set; }
    public string? SentBy { get; set; }
    public Guid? SentByUserId { get; set; }

    public DateTimeOffset? ReceivedUtc { get; set; }
    public string? ReceivedBy { get; set; }
    public Guid? ReceivedByUserId { get; set; }

    public DateTimeOffset? CancelledUtc { get; set; }
    public string? CancelledBy { get; set; }

    /// <summary>True when what was received differs from what was sent on any line (damage, a miscount). Set when it is received.</summary>
    public bool HasDiscrepancy { get; set; }

    public List<StockTransferLine> Lines { get; set; } = [];
}

public class StockTransferLine
{
    public Guid TransferId { get; set; }
    public Guid ProductId { get; set; }
    /// <summary>Copies, so the transfer still reads the same if the product is renamed.</summary>
    public required string ProductName { get; set; }
    public string? Sku { get; set; }
    public decimal QuantitySent { get; set; }
    /// <summary>Null until the transfer is received.</summary>
    public decimal? QuantityReceived { get; set; }
}

/// <summary>Single-row counter behind transfer numbers, taken inside the send transaction.</summary>
public class StockTransferCounter
{
    public int Id { get; set; } = 1;
    public long LastNumber { get; set; }
}

/// <summary>Limits a staff user to certain locations. A user with no rows here is not limited at all.</summary>
public class StaffLocation
{
    public Guid UserId { get; set; }
    public Guid LocationId { get; set; }
}

public static class StockTransferNumbers
{
    public const long First = 1001;
    public static string Format(long number) => $"TRF-{number}";
}

public sealed class StockOptions
{
    public const string SectionName = "Piranha:Stock";

    /// <summary>
    /// Sending and receiving a transfer are two people's jobs. Set this to let one user do both, for a shop with a single member of staff.
    /// </summary>
    public bool AllowSameUserToReceive { get; set; }
}

// ---- Who is acting ---------------------------------------------------------------------------------------------------------

/// <summary>The staff user making a stock change, read from their token (never from the request body).</summary>
public sealed record StockActor(Guid? UserId, string? Name);

// ---- Inputs and DTOs -------------------------------------------------------------------------------------------------------

public sealed class StockTransferLineInput
{
    public Guid ProductId { get; set; }
    /// <summary>Up to 4 decimal places. When sending it must be above zero; when receiving, zero is allowed (nothing of it arrived).</summary>
    public decimal Quantity { get; set; }
}

public sealed class StockTransferInput
{
    public Guid FromLocationId { get; set; }
    public Guid ToLocationId { get; set; }
    [StringLength(500)] public string? Note { get; set; }
    public List<StockTransferLineInput> Lines { get; set; } = [];
}

public sealed class StockTransferReceiveInput
{
    /// <summary>What actually arrived, by product. Lines left out count as received in full. Omit to receive everything as sent.</summary>
    public List<StockTransferLineInput>? Lines { get; set; }
}

public sealed record StockTransferLineDto(Guid ProductId, string ProductName, string? Sku, decimal QuantitySent, decimal? QuantityReceived);

public sealed record StockTransferDto(
    Guid Id, long Number, string Reference, Guid FromLocationId, string FromCode, Guid ToLocationId, string ToCode, StockTransferStatus Status, string? Note,
    DateTimeOffset SentUtc, string? SentBy, DateTimeOffset? ReceivedUtc, string? ReceivedBy, DateTimeOffset? CancelledUtc, string? CancelledBy,
    bool HasDiscrepancy, IReadOnlyList<StockTransferLineDto> Lines);

/// <summary>Stock that has left one location and not yet arrived at another, summed over the transfers still in transit.</summary>
public sealed record InTransitDto(Guid ProductId, string ProductName, string? Sku, Guid FromLocationId, string FromCode, Guid ToLocationId, string ToCode, decimal Quantity);

public sealed record StaffLocationsDto(Guid UserId, string Email, string? DisplayName, bool Active, IReadOnlyList<Guid> LocationIds);

// ---- Staff location assignment ---------------------------------------------------------------------------------------------

public interface IStaffLocationService
{
    /// <summary>Every staff user with the locations they are limited to (an empty list means not limited).</summary>
    Task<IReadOnlyList<StaffLocationsDto>> ListAsync(CancellationToken ct = default);

    /// <summary>Replaces the locations a user is limited to. An empty list removes the limit. Null when the user does not exist.</summary>
    Task<StaffLocationsDto?> SetAsync(Guid userId, IReadOnlyList<Guid> locationIds, CancellationToken ct = default);

    /// <summary>True when the user may act at the location: they have no limit, or the location is one of theirs.</summary>
    Task<bool> CanActAtAsync(Guid? userId, Guid locationId, CancellationToken ct = default);
}

internal sealed class StaffLocationService(PiranhaDbContext db) : IStaffLocationService
{
    public async Task<IReadOnlyList<StaffLocationsDto>> ListAsync(CancellationToken ct = default)
    {
        var users = await db.Set<PiranhaUser>().AsNoTracking().OrderBy(u => u.Email).Select(u => new { u.Id, u.Email, u.DisplayName, u.Active }).ToListAsync(ct);
        var rows = await db.Set<StaffLocation>().AsNoTracking().ToListAsync(ct);
        return users.Select(u => new StaffLocationsDto(u.Id, u.Email ?? "", u.DisplayName, u.Active, rows.Where(r => r.UserId == u.Id).Select(r => r.LocationId).Order().ToList())).ToList();
    }

    public async Task<StaffLocationsDto?> SetAsync(Guid userId, IReadOnlyList<Guid> locationIds, CancellationToken ct = default)
    {
        var user = await db.Set<PiranhaUser>().AsNoTracking().Where(u => u.Id == userId).Select(u => new { u.Id, u.Email, u.DisplayName, u.Active }).FirstOrDefaultAsync(ct);
        if (user is null) return null;
        var wanted = locationIds.Distinct().ToList();
        var known = await db.Set<StockLocation>().AsNoTracking().Where(l => wanted.Contains(l.Id)).Select(l => l.Id).ToListAsync(ct);
        if (known.Count != wanted.Count) throw new PiranhaValidationException("locationIds", "One of those locations does not exist.");

        var strategy = db.Database.CreateExecutionStrategy();
        await strategy.ExecuteInTransactionAsync(async token =>
        {
            db.ChangeTracker.Clear();
            await db.Set<StaffLocation>().Where(r => r.UserId == userId).ExecuteDeleteAsync(token);
            foreach (var id in wanted) db.Add(new StaffLocation { UserId = userId, LocationId = id });
            await db.SaveChangesAsync(token);
            return true;
        }, async token => await db.Set<StaffLocation>().AsNoTracking().CountAsync(r => r.UserId == userId, token) == wanted.Count, ct);
        return new StaffLocationsDto(user.Id, user.Email ?? "", user.DisplayName, user.Active, wanted.Order().ToList());
    }

    public async Task<bool> CanActAtAsync(Guid? userId, Guid locationId, CancellationToken ct = default)
    {
        if (userId is not { } id) return true;
        var mine = await db.Set<StaffLocation>().AsNoTracking().Where(r => r.UserId == id).Select(r => r.LocationId).ToListAsync(ct);
        return mine.Count == 0 || mine.Contains(locationId);
    }
}

// ---- Transfers -------------------------------------------------------------------------------------------------------------

public interface IStockTransferService
{
    Task<PagedResult<StockTransferDto>> ListAsync(int? page, int? pageSize, StockTransferStatus? status, Guid? locationId, bool? discrepancies, CancellationToken ct = default);
    Task<StockTransferDto?> GetAsync(Guid id, CancellationToken ct = default);

    /// <summary>Everything in transit, per product and route.</summary>
    Task<IReadOnlyList<InTransitDto>> ListInTransitAsync(CancellationToken ct = default);

    /// <summary>
    /// Sends stock: it leaves the sending location now (refused when there is not enough there) and is in transit until received. The caller must be
    /// allowed to act at the sending location.
    /// </summary>
    Task<StockTransferDto> SendAsync(StockTransferInput input, StockActor actor, CancellationToken ct = default);

    /// <summary>
    /// Receives an in-transit transfer at its destination: what arrived joins that location's stock. The caller must be allowed to act there and,
    /// unless <see cref="StockOptions.AllowSameUserToReceive"/> is set, must not be the person who sent it. Null when it does not exist; conflict when it is no longer in transit.
    /// </summary>
    Task<StockTransferDto?> ReceiveAsync(Guid id, StockTransferReceiveInput input, StockActor actor, CancellationToken ct = default);

    /// <summary>Cancels a transfer still in transit: the stock goes back to the sending location. Allowed at either end.</summary>
    Task<StockTransferDto?> CancelAsync(Guid id, StockActor actor, CancellationToken ct = default);

    /// <summary>How many transfers are still in transit (for the menu badge).</summary>
    Task<int> InTransitCountAsync(CancellationToken ct = default);
}

internal sealed class StockTransferService(PiranhaDbContext db, IStockService stock, IStaffLocationService staff, IOptions<StockOptions> options) : IStockTransferService
{
    private const int MaxLines = 200;

    private async Task<List<StockTransferDto>> ToDtosAsync(IQueryable<StockTransfer> query, CancellationToken ct)
    {
        var rows = await query.AsNoTracking().Include(t => t.Lines).ToListAsync(ct);
        var codes = await db.Set<StockLocation>().AsNoTracking().ToDictionaryAsync(l => l.Id, l => l.Code, ct);
        return rows.Select(t => new StockTransferDto(
            t.Id, t.Number, StockTransferNumbers.Format(t.Number), t.FromLocationId, codes.GetValueOrDefault(t.FromLocationId, "?"), t.ToLocationId, codes.GetValueOrDefault(t.ToLocationId, "?"),
            t.Status, t.Note, t.SentUtc, t.SentBy, t.ReceivedUtc, t.ReceivedBy, t.CancelledUtc, t.CancelledBy, t.HasDiscrepancy,
            t.Lines.OrderBy(l => l.ProductName).Select(l => new StockTransferLineDto(l.ProductId, l.ProductName, l.Sku, l.QuantitySent, l.QuantityReceived)).ToList())).ToList();
    }

    public async Task<PagedResult<StockTransferDto>> ListAsync(int? page, int? pageSize, StockTransferStatus? status, Guid? locationId, bool? discrepancies, CancellationToken ct = default)
    {
        var (p, size) = Paging.Normalize(page, pageSize);
        var query = db.Set<StockTransfer>().AsNoTracking().AsQueryable();
        if (status is { } s) query = query.Where(t => t.Status == s);
        if (locationId is { } loc) query = query.Where(t => t.FromLocationId == loc || t.ToLocationId == loc);
        if (discrepancies == true) query = query.Where(t => t.HasDiscrepancy);
        var total = await query.CountAsync(ct);
        // Outstanding ones oldest first would hide new work; newest first is what a person scanning the list expects.
        var items = await ToDtosAsync(query.OrderByDescending(t => t.Number).Skip((p - 1) * size).Take(size), ct);
        return new PagedResult<StockTransferDto>(items, p, size, total);
    }

    public async Task<StockTransferDto?> GetAsync(Guid id, CancellationToken ct = default) =>
        (await ToDtosAsync(db.Set<StockTransfer>().Where(t => t.Id == id), ct)).FirstOrDefault();

    public async Task<IReadOnlyList<InTransitDto>> ListInTransitAsync(CancellationToken ct = default)
    {
        var rows = await (from l in db.Set<StockTransferLine>().AsNoTracking()
                          join t in db.Set<StockTransfer>().AsNoTracking() on l.TransferId equals t.Id
                          where t.Status == StockTransferStatus.InTransit
                          select new { l.ProductId, l.ProductName, l.Sku, t.FromLocationId, t.ToLocationId, l.QuantitySent }).ToListAsync(ct);
        var codes = await db.Set<StockLocation>().AsNoTracking().ToDictionaryAsync(l => l.Id, l => l.Code, ct);
        return rows.GroupBy(r => new { r.ProductId, r.FromLocationId, r.ToLocationId })
            .Select(g => new InTransitDto(g.Key.ProductId, g.First().ProductName, g.First().Sku, g.Key.FromLocationId, codes.GetValueOrDefault(g.Key.FromLocationId, "?"),
                g.Key.ToLocationId, codes.GetValueOrDefault(g.Key.ToLocationId, "?"), g.Sum(x => x.QuantitySent)))
            .OrderBy(x => x.ProductName).ThenBy(x => x.FromCode).ThenBy(x => x.ToCode).ToList();
    }

    public Task<int> InTransitCountAsync(CancellationToken ct = default) =>
        db.Set<StockTransfer>().AsNoTracking().CountAsync(t => t.Status == StockTransferStatus.InTransit, ct);

    private static void CheckQuantity(decimal q, string field, bool allowZero)
    {
        if (q < 0 || (!allowZero && q == 0)) throw new PiranhaValidationException(field, allowZero ? "A quantity cannot be negative." : "A quantity must be above zero.");
        if (decimal.Round(q, 4) != q) throw new PiranhaValidationException(field, "A quantity can have at most 4 decimal places.");
        if (q > StockAdjustmentInput.MaxMagnitude) throw new PiranhaValidationException(field, "That quantity is too large.");
    }

    private async Task RequireAtAsync(StockActor actor, Guid locationId, string code, string what, CancellationToken ct)
    {
        if (!await staff.CanActAtAsync(actor.UserId, locationId, ct))
            throw new PiranhaForbiddenException($"You are not assigned to {code}, so you cannot {what}.");
    }

    public async Task<StockTransferDto> SendAsync(StockTransferInput input, StockActor actor, CancellationToken ct = default)
    {
        ModelValidator.ValidateOrThrow(input);
        if (input.FromLocationId == input.ToLocationId) throw new PiranhaValidationException(nameof(StockTransferInput.ToLocationId), "A transfer needs two different locations.");
        if (input.Lines.Count == 0) throw new PiranhaValidationException(nameof(StockTransferInput.Lines), "Add at least one product.");
        if (input.Lines.Count > MaxLines) throw new PiranhaValidationException(nameof(StockTransferInput.Lines), $"A transfer can have at most {MaxLines} lines.");
        if (input.Lines.Select(l => l.ProductId).Distinct().Count() != input.Lines.Count) throw new PiranhaValidationException(nameof(StockTransferInput.Lines), "A product can appear once.");
        foreach (var l in input.Lines) CheckQuantity(l.Quantity, nameof(StockTransferInput.Lines), allowZero: false);

        var locations = await db.Set<StockLocation>().AsNoTracking().Where(l => l.Id == input.FromLocationId || l.Id == input.ToLocationId).ToListAsync(ct);
        var from = locations.FirstOrDefault(l => l.Id == input.FromLocationId) ?? throw new PiranhaValidationException(nameof(StockTransferInput.FromLocationId), "That location does not exist.");
        var to = locations.FirstOrDefault(l => l.Id == input.ToLocationId) ?? throw new PiranhaValidationException(nameof(StockTransferInput.ToLocationId), "That location does not exist.");
        if (!from.Active) throw new PiranhaValidationException(nameof(StockTransferInput.FromLocationId), "That location is not active.");
        if (!to.Active) throw new PiranhaValidationException(nameof(StockTransferInput.ToLocationId), "That location is not active.");
        await RequireAtAsync(actor, from.Id, from.Code, "send stock from it", ct);

        var ids = input.Lines.Select(l => l.ProductId).ToList();
        var products = await db.Set<Product>().AsNoTracking().Where(p => ids.Contains(p.Id)).Select(p => new { p.Id, p.Name, p.Sku }).ToDictionaryAsync(p => p.Id, ct);
        if (products.Count != ids.Count) throw new PiranhaValidationException(nameof(StockTransferInput.Lines), "One of those products does not exist.");

        await stock.EnsureLevelsAsync(ids, from.Id, ct); // before the transaction: a failed insert would abort a PostgreSQL one
        var transferId = Guid.NewGuid();
        var strategy = db.Database.CreateExecutionStrategy();
        try
        {
            await strategy.ExecuteInTransactionAsync(async token =>
            {
                db.ChangeTracker.Clear();
                await db.Set<StockTransferCounter>().Where(c => c.Id == 1).ExecuteUpdateAsync(s => s.SetProperty(c => c.LastNumber, c => c.LastNumber + 1), token);
                var number = await db.Set<StockTransferCounter>().AsNoTracking().Where(c => c.Id == 1).Select(c => c.LastNumber).FirstAsync(token);

                db.Add(new StockTransfer
                {
                    Id = transferId, Number = number, FromLocationId = from.Id, ToLocationId = to.Id, Status = StockTransferStatus.InTransit,
                    Note = string.IsNullOrWhiteSpace(input.Note) ? null : input.Note.Trim(), SentUtc = DateTimeOffset.UtcNow, SentBy = actor.Name, SentByUserId = actor.UserId,
                    Lines = input.Lines.Select(l => new StockTransferLine
                    {
                        TransferId = transferId, ProductId = l.ProductId, ProductName = products[l.ProductId].Name, Sku = products[l.ProductId].Sku, QuantitySent = l.Quantity,
                    }).ToList(),
                });
                // The stock leaves in the same transaction. Short stock throws and rolls the whole transfer back.
                var reference = StockTransferNumbers.Format(number);
                await stock.ApplyInTransactionAsync(
                    input.Lines.Select(l => new StockChange(l.ProductId, -l.Quantity, "Transfer sent", reference, $"\"{products[l.ProductId].Name}\"")).ToList(),
                    from.Id, allowNegative: false, actor.Name, token);
                await db.SaveChangesAsync(token);
                return true;
            }, async token => await db.Set<StockTransfer>().AsNoTracking().AnyAsync(t => t.Id == transferId, token), ct);
        }
        catch (DbUpdateException)
        {
            db.ChangeTracker.Clear();
            throw new PiranhaConflictException("The transfer could not be sent. Please try again.");
        }
        return (await GetAsync(transferId, ct))!;
    }

    public async Task<StockTransferDto?> ReceiveAsync(Guid id, StockTransferReceiveInput input, StockActor actor, CancellationToken ct = default)
    {
        var current = await db.Set<StockTransfer>().AsNoTracking().Include(t => t.Lines).FirstOrDefaultAsync(t => t.Id == id, ct);
        if (current is null) return null;
        if (current.Status != StockTransferStatus.InTransit) throw new PiranhaConflictException($"This transfer is {current.Status.ToString().ToLowerInvariant()}, so it cannot be received.");

        var to = await db.Set<StockLocation>().AsNoTracking().FirstAsync(l => l.Id == current.ToLocationId, ct);
        await RequireAtAsync(actor, to.Id, to.Code, "receive stock there", ct);
        if (!options.Value.AllowSameUserToReceive && actor.UserId is { } me && current.SentByUserId == me)
            throw new PiranhaForbiddenException("A transfer has to be received by someone other than the person who sent it.");

        // What arrived: by default all of it. Lines named in the input override that; naming a product that is not on the transfer is an error.
        var received = current.Lines.ToDictionary(l => l.ProductId, l => l.QuantitySent);
        foreach (var l in input.Lines ?? [])
        {
            if (!received.ContainsKey(l.ProductId)) throw new PiranhaValidationException(nameof(StockTransferReceiveInput.Lines), "That product is not on this transfer.");
            CheckQuantity(l.Quantity, nameof(StockTransferReceiveInput.Lines), allowZero: true);
            received[l.ProductId] = l.Quantity;
        }
        if ((input.Lines ?? []).Select(l => l.ProductId).Distinct().Count() != (input.Lines ?? []).Count) throw new PiranhaValidationException(nameof(StockTransferReceiveInput.Lines), "A product can appear once.");
        var discrepancy = current.Lines.Any(l => received[l.ProductId] != l.QuantitySent);

        await stock.EnsureLevelsAsync(current.Lines.Select(l => l.ProductId), to.Id, ct);
        var strategy = db.Database.CreateExecutionStrategy();
        try
        {
            await strategy.ExecuteInTransactionAsync(async token =>
            {
                db.ChangeTracker.Clear();
                var now = DateTimeOffset.UtcNow;
                // Only one caller can take it out of "in transit": a second receive (or a cancel) at the same moment changes nothing and must not add stock.
                var moved = await db.Set<StockTransfer>().Where(t => t.Id == id && t.Status == StockTransferStatus.InTransit)
                    .ExecuteUpdateAsync(s => s.SetProperty(t => t.Status, StockTransferStatus.Received).SetProperty(t => t.ReceivedUtc, (DateTimeOffset?)now)
                        .SetProperty(t => t.ReceivedBy, actor.Name).SetProperty(t => t.ReceivedByUserId, actor.UserId).SetProperty(t => t.HasDiscrepancy, discrepancy), token);
                if (moved == 0) throw new PiranhaConflictException("This transfer was changed by someone else. Reload it and try again.");

                foreach (var l in current.Lines)
                {
                    var q = received[l.ProductId];
                    await db.Set<StockTransferLine>().Where(x => x.TransferId == id && x.ProductId == l.ProductId)
                        .ExecuteUpdateAsync(s => s.SetProperty(x => x.QuantityReceived, (decimal?)q), token);
                }
                var reference = StockTransferNumbers.Format(current.Number);
                var arrivals = current.Lines.Where(l => received[l.ProductId] > 0)
                    .Select(l => new StockChange(l.ProductId, received[l.ProductId], "Transfer received", reference, $"\"{l.ProductName}\"")).ToList();
                if (arrivals.Count > 0)
                {
                    await stock.ApplyInTransactionAsync(arrivals, to.Id, allowNegative: true, actor.Name, token);
                    await db.SaveChangesAsync(token);
                }
                return true;
            }, async token => await db.Set<StockTransfer>().AsNoTracking().AnyAsync(t => t.Id == id && t.Status == StockTransferStatus.Received, token), ct);
        }
        catch (DbUpdateException)
        {
            db.ChangeTracker.Clear();
            throw new PiranhaConflictException("The transfer could not be received. Please try again.");
        }
        return await GetAsync(id, ct);
    }

    public async Task<StockTransferDto?> CancelAsync(Guid id, StockActor actor, CancellationToken ct = default)
    {
        var current = await db.Set<StockTransfer>().AsNoTracking().Include(t => t.Lines).FirstOrDefaultAsync(t => t.Id == id, ct);
        if (current is null) return null;
        if (current.Status != StockTransferStatus.InTransit) throw new PiranhaConflictException($"This transfer is {current.Status.ToString().ToLowerInvariant()}, so it cannot be cancelled.");

        var locations = await db.Set<StockLocation>().AsNoTracking().Where(l => l.Id == current.FromLocationId || l.Id == current.ToLocationId).ToListAsync(ct);
        var from = locations.First(l => l.Id == current.FromLocationId);
        var to = locations.First(l => l.Id == current.ToLocationId);
        if (!await staff.CanActAtAsync(actor.UserId, from.Id, ct) && !await staff.CanActAtAsync(actor.UserId, to.Id, ct))
            throw new PiranhaForbiddenException($"You are not assigned to {from.Code} or {to.Code}, so you cannot cancel this transfer.");

        await stock.EnsureLevelsAsync(current.Lines.Select(l => l.ProductId), from.Id, ct);
        var strategy = db.Database.CreateExecutionStrategy();
        try
        {
            await strategy.ExecuteInTransactionAsync(async token =>
            {
                db.ChangeTracker.Clear();
                var now = DateTimeOffset.UtcNow;
                var moved = await db.Set<StockTransfer>().Where(t => t.Id == id && t.Status == StockTransferStatus.InTransit)
                    .ExecuteUpdateAsync(s => s.SetProperty(t => t.Status, StockTransferStatus.Cancelled).SetProperty(t => t.CancelledUtc, (DateTimeOffset?)now)
                        .SetProperty(t => t.CancelledBy, actor.Name), token);
                if (moved == 0) throw new PiranhaConflictException("This transfer was changed by someone else. Reload it and try again.");
                var reference = StockTransferNumbers.Format(current.Number);
                await stock.ApplyInTransactionAsync(
                    current.Lines.Select(l => new StockChange(l.ProductId, l.QuantitySent, "Transfer cancelled", reference, $"\"{l.ProductName}\"")).ToList(),
                    from.Id, allowNegative: true, actor.Name, token);
                await db.SaveChangesAsync(token);
                return true;
            }, async token => await db.Set<StockTransfer>().AsNoTracking().AnyAsync(t => t.Id == id && t.Status == StockTransferStatus.Cancelled, token), ct);
        }
        catch (DbUpdateException)
        {
            db.ChangeTracker.Clear();
            throw new PiranhaConflictException("The transfer could not be cancelled. Please try again.");
        }
        return await GetAsync(id, ct);
    }
}
