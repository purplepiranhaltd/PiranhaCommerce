using Microsoft.EntityFrameworkCore;
using PiranhaCommerce.Core.Paging;
using PiranhaCommerce.Core.Validation;
using PiranhaCommerce.Data;

namespace PiranhaCommerce.Catalog;

public interface IStockService
{
    /// <summary>Null when the product does not exist. Quantity is the total over locations; zero for a product that never had stock.</summary>
    Task<StockLevelDto?> GetLevelAsync(Guid productId, CancellationToken ct = default);

    /// <summary>Newest first. Null when the product does not exist.</summary>
    Task<PagedResult<StockMovementDto>?> ListMovementsAsync(Guid productId, int? page, int? pageSize, CancellationToken ct = default);

    /// <summary>
    /// Atomically changes the level at a location and records the movement. Safe under concurrent callers and under
    /// transient-failure retries (a movement is never applied twice). Null when the product does not exist. Throws
    /// <see cref="PiranhaConflictException"/> when it would go below zero and <see cref="StockAdjustmentInput.AllowNegative"/> is false.
    /// </summary>
    Task<StockAdjustmentResult?> AdjustAsync(Guid productId, StockAdjustmentInput input, string? actor = null, CancellationToken ct = default);

    /// <summary>
    /// For callers that make stock part of a larger all-or-nothing operation (an order). Creates any missing level rows so
    /// <see cref="ApplyInTransactionAsync"/> can run inside the caller's transaction. Call it BEFORE opening that transaction.
    /// </summary>
    Task EnsureLevelsAsync(IEnumerable<Guid> productIds, Guid locationId, CancellationToken ct = default);

    /// <summary>
    /// Applies the changes at one location as part of a transaction the CALLER has opened on this same scope (it must run inside the
    /// caller's execution-strategy delegate). Records one movement per change; the caller's <c>SaveChangesAsync</c> saves them and the
    /// caller's commit makes them permanent. Throws <see cref="PiranhaConflictException"/> when a change would take stock below
    /// zero (<paramref name="allowNegative"/> false), which should roll the caller's transaction back.
    /// </summary>
    Task ApplyInTransactionAsync(IReadOnlyList<StockChange> changes, Guid locationId, bool allowNegative, string? actor, CancellationToken ct = default);

    /// <summary>Every product/location currently below zero, most negative first. This is the "recount needed" report.</summary>
    Task<IReadOnlyList<NegativeStockDto>> ListNegativeAsync(CancellationToken ct = default);
}

internal sealed class StockService(PiranhaDbContext db) : IStockService
{
    public async Task<StockLevelDto?> GetLevelAsync(Guid productId, CancellationToken ct = default)
    {
        if (!await db.Set<Product>().AnyAsync(p => p.Id == productId, ct)) return null;
        var locations = await db.Set<StockLevel>().AsNoTracking().Where(l => l.ProductId == productId)
            .Join(db.Set<StockLocation>(), l => l.LocationId, loc => loc.Id, (l, loc) => new { loc.Id, loc.Code, loc.Name, l.Quantity })
            .OrderBy(x => x.Code)
            .Select(x => new StockLocationLevelDto(x.Id, x.Code, x.Name, x.Quantity))
            .ToListAsync(ct);
        var inTransit = await (from l in db.Set<StockTransferLine>().AsNoTracking()
                               join t in db.Set<StockTransfer>().AsNoTracking() on l.TransferId equals t.Id
                               where l.ProductId == productId && t.Status == StockTransferStatus.InTransit
                               select (decimal?)l.QuantitySent).SumAsync(ct) ?? 0m;
        return new StockLevelDto(productId, locations.Sum(x => x.Quantity), locations, inTransit);
    }

    public async Task<PagedResult<StockMovementDto>?> ListMovementsAsync(Guid productId, int? page, int? pageSize, CancellationToken ct = default)
    {
        if (!await db.Set<Product>().AnyAsync(p => p.Id == productId, ct)) return null;
        var (p, size) = Paging.Normalize(page, pageSize);
        var query = db.Set<StockMovement>().AsNoTracking().Where(m => m.ProductId == productId);
        var total = await query.CountAsync(ct);
        var items = await query.OrderByDescending(m => m.CreatedUtc).ThenByDescending(m => m.Id)
            .Skip((p - 1) * size).Take(size)
            .Select(m => new StockMovementDto(m.Id, m.ProductId, m.LocationId, m.Delta, m.Balance, m.Reason, m.Reference, m.Actor, m.CreatedUtc))
            .ToListAsync(ct);
        return new PagedResult<StockMovementDto>(items, p, size, total);
    }

    public async Task<StockAdjustmentResult?> AdjustAsync(Guid productId, StockAdjustmentInput input, string? actor = null, CancellationToken ct = default)
    {
        ModelValidator.ValidateOrThrow(input);
        if (!await db.Set<Product>().AnyAsync(p => p.Id == productId, ct)) return null;

        var locationId = input.LocationId ?? StockLocations.DefaultId;
        var location = await db.Set<StockLocation>().AsNoTracking().Where(l => l.Id == locationId).Select(l => new { l.Active }).FirstOrDefaultAsync(ct);
        if (location is null)
            throw new PiranhaValidationException(nameof(StockAdjustmentInput.LocationId), "That stock location does not exist.");
        if (!location.Active)
            throw new PiranhaValidationException(nameof(StockAdjustmentInput.LocationId), "That stock location is not active.");

        await EnsureLevelRowAsync(productId, locationId, ct); // outside the transaction: a failed insert would abort a PostgreSQL transaction

        // Created once, outside the retried delegate, so a retry after an ambiguous commit can tell whether it already happened.
        var movement = new StockMovement
        {
            ProductId = productId,
            LocationId = locationId,
            Delta = input.Delta,
            Reason = input.Reason.Trim(),
            Reference = string.IsNullOrWhiteSpace(input.Reference) ? null : input.Reference.Trim(),
            Actor = actor,
            CreatedUtc = DateTimeOffset.UtcNow,
        };
        var movementId = movement.Id;

        var strategy = db.Database.CreateExecutionStrategy();
        try
        {
            await strategy.ExecuteInTransactionAsync(
                async token =>
                {
                    db.ChangeTracker.Clear(); // every attempt starts clean

                    var changed = input.AllowNegative
                        ? await db.Set<StockLevel>().Where(l => l.ProductId == productId && l.LocationId == locationId)
                            .ExecuteUpdateAsync(s => s.SetProperty(l => l.Quantity, l => l.Quantity + input.Delta), token)
                        : await db.Set<StockLevel>().Where(l => l.ProductId == productId && l.LocationId == locationId && l.Quantity + input.Delta >= 0)
                            .ExecuteUpdateAsync(s => s.SetProperty(l => l.Quantity, l => l.Quantity + input.Delta), token);
                    if (changed == 0)
                        throw new PiranhaConflictException("Not enough stock: this would take the quantity below zero.");

                    // Our update holds the row lock until commit, so this read is the exact balance after our movement.
                    movement.Balance = await db.Set<StockLevel>().AsNoTracking()
                        .Where(l => l.ProductId == productId && l.LocationId == locationId).Select(l => l.Quantity).FirstAsync(token);

                    db.Add(movement);
                    await db.SaveChangesAsync(token);
                    return true;
                },
                async token => await db.Set<StockMovement>().AsNoTracking().AnyAsync(m => m.Id == movementId, token),
                ct);
        }
        catch (DbUpdateException)
        {
            db.ChangeTracker.Clear();
            if (!await db.Set<Product>().AnyAsync(p => p.Id == productId, CancellationToken.None))
                return null; // product deleted while we were adjusting
            throw;
        }

        var quantity = await db.Set<StockLevel>().AsNoTracking().Where(l => l.ProductId == productId).SumAsync(l => (decimal?)l.Quantity, ct) ?? 0;
        return new StockAdjustmentResult(
            new StockMovementDto(movement.Id, productId, locationId, movement.Delta, movement.Balance, movement.Reason, movement.Reference, movement.Actor, movement.CreatedUtc),
            quantity);
    }

    public async Task<IReadOnlyList<NegativeStockDto>> ListNegativeAsync(CancellationToken ct = default)
        => await db.Set<StockLevel>().AsNoTracking().Where(l => l.Quantity < 0)
            .Join(db.Set<Product>(), l => l.ProductId, p => p.Id, (l, p) => new { l, p })
            .Join(db.Set<StockLocation>(), x => x.l.LocationId, loc => loc.Id,
                (x, loc) => new { x.p.Id, x.p.Name, x.p.Sku, LocationId = loc.Id, loc.Code, LocationName = loc.Name, x.l.Quantity })
            .OrderBy(n => n.Quantity).ThenBy(n => n.Name).ThenBy(n => n.Id)
            .Select(n => new NegativeStockDto(n.Id, n.Name, n.Sku, n.LocationId, n.Code, n.LocationName, n.Quantity))
            .ToListAsync(ct);

    public async Task EnsureLevelsAsync(IEnumerable<Guid> productIds, Guid locationId, CancellationToken ct = default)
    {
        foreach (var id in productIds.Distinct())
            await EnsureLevelRowAsync(id, locationId, ct);
    }

    public async Task ApplyInTransactionAsync(
        IReadOnlyList<StockChange> changes, Guid locationId, bool allowNegative, string? actor, CancellationToken ct = default)
    {
        if (db.Database.CurrentTransaction is null)
            throw new InvalidOperationException("ApplyInTransactionAsync must run inside a transaction opened by the caller.");

        // Fixed order (by product) so two checkouts that share products always lock rows in the same order and cannot deadlock.
        foreach (var change in changes.OrderBy(c => c.ProductId))
        {
            var delta = change.Delta;
            var changed = allowNegative
                ? await db.Set<StockLevel>().Where(l => l.ProductId == change.ProductId && l.LocationId == locationId)
                    .ExecuteUpdateAsync(s => s.SetProperty(l => l.Quantity, l => l.Quantity + delta), ct)
                : await db.Set<StockLevel>().Where(l => l.ProductId == change.ProductId && l.LocationId == locationId && l.Quantity + delta >= 0)
                    .ExecuteUpdateAsync(s => s.SetProperty(l => l.Quantity, l => l.Quantity + delta), ct);
            if (changed == 0)
                throw new PiranhaConflictException($"Not enough stock for {change.Label ?? "a product"}.");

            var balance = await db.Set<StockLevel>().AsNoTracking()
                .Where(l => l.ProductId == change.ProductId && l.LocationId == locationId).Select(l => l.Quantity).FirstAsync(ct);
            db.Add(new StockMovement
            {
                ProductId = change.ProductId,
                LocationId = locationId,
                Delta = delta,
                Balance = balance,
                Reason = change.Reason,
                Reference = change.Reference,
                Actor = actor,
                CreatedUtc = DateTimeOffset.UtcNow,
            });
        }
    }

    private async Task EnsureLevelRowAsync(Guid productId, Guid locationId, CancellationToken ct)
    {
        if (await db.Set<StockLevel>().AnyAsync(l => l.ProductId == productId && l.LocationId == locationId, ct)) return;
        try
        {
            db.Add(new StockLevel { ProductId = productId, LocationId = locationId, Quantity = 0 });
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException)
        {
            db.ChangeTracker.Clear(); // another caller created it first (or the product vanished); the update below decides
        }
    }
}
