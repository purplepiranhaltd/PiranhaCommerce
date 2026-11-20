using Microsoft.EntityFrameworkCore;
using PiranhaCommerce.Core.Paging;
using PiranhaCommerce.Core.Validation;
using PiranhaCommerce.Data;

namespace PiranhaCommerce.Catalog;

public interface IStockService
{
    /// <summary>Null when the product does not exist; zero for a product that never had stock.</summary>
    Task<StockLevelDto?> GetLevelAsync(Guid productId, CancellationToken ct = default);

    /// <summary>Newest first. Null when the product does not exist.</summary>
    Task<PagedResult<StockMovementDto>?> ListMovementsAsync(Guid productId, int? page, int? pageSize, CancellationToken ct = default);

    /// <summary>
    /// Atomically changes the level and records the movement. Safe under concurrent callers and under transient-failure retries
    /// (a movement is never applied twice). Null when the product does not exist. Throws <see cref="PiranhaConflictException"/>
    /// when it would go below zero and <see cref="StockAdjustmentInput.AllowNegative"/> is false.
    /// </summary>
    Task<StockAdjustmentResult?> AdjustAsync(Guid productId, StockAdjustmentInput input, string? actor = null, CancellationToken ct = default);
}

internal sealed class StockService(PiranhaDbContext db) : IStockService
{
    public async Task<StockLevelDto?> GetLevelAsync(Guid productId, CancellationToken ct = default)
    {
        if (!await db.Set<Product>().AnyAsync(p => p.Id == productId, ct)) return null;
        return new StockLevelDto(productId, await ReadQuantityAsync(productId, ct));
    }

    public async Task<PagedResult<StockMovementDto>?> ListMovementsAsync(Guid productId, int? page, int? pageSize, CancellationToken ct = default)
    {
        if (!await db.Set<Product>().AnyAsync(p => p.Id == productId, ct)) return null;
        var (p, size) = Paging.Normalize(page, pageSize);
        var query = db.Set<StockMovement>().AsNoTracking().Where(m => m.ProductId == productId);
        var total = await query.CountAsync(ct);
        var items = await query.OrderByDescending(m => m.CreatedUtc).ThenByDescending(m => m.Id)
            .Skip((p - 1) * size).Take(size)
            .Select(m => new StockMovementDto(m.Id, m.ProductId, m.Delta, m.Reason, m.Reference, m.Actor, m.CreatedUtc))
            .ToListAsync(ct);
        return new PagedResult<StockMovementDto>(items, p, size, total);
    }

    public async Task<StockAdjustmentResult?> AdjustAsync(Guid productId, StockAdjustmentInput input, string? actor = null, CancellationToken ct = default)
    {
        ModelValidator.ValidateOrThrow(input);
        if (!await db.Set<Product>().AnyAsync(p => p.Id == productId, ct)) return null;

        await EnsureLevelRowAsync(productId, ct); // outside the transaction: a failed insert would abort a PostgreSQL transaction

        // Created once, outside the retried delegate, so a retry after an ambiguous commit can tell whether it already happened.
        var movement = new StockMovement
        {
            ProductId = productId,
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
                        ? await db.Set<StockLevel>().Where(l => l.ProductId == productId)
                            .ExecuteUpdateAsync(s => s.SetProperty(l => l.Quantity, l => l.Quantity + input.Delta), token)
                        : await db.Set<StockLevel>().Where(l => l.ProductId == productId && l.Quantity + input.Delta >= 0)
                            .ExecuteUpdateAsync(s => s.SetProperty(l => l.Quantity, l => l.Quantity + input.Delta), token);
                    if (changed == 0)
                        throw new PiranhaConflictException("Not enough stock: this would take the quantity below zero.");

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

        return new StockAdjustmentResult(
            new StockMovementDto(movement.Id, productId, movement.Delta, movement.Reason, movement.Reference, movement.Actor, movement.CreatedUtc),
            await ReadQuantityAsync(productId, ct));
    }

    private async Task<decimal> ReadQuantityAsync(Guid productId, CancellationToken ct)
        => await db.Set<StockLevel>().AsNoTracking().Where(l => l.ProductId == productId).Select(l => l.Quantity).FirstOrDefaultAsync(ct);

    private async Task EnsureLevelRowAsync(Guid productId, CancellationToken ct)
    {
        if (await db.Set<StockLevel>().AnyAsync(l => l.ProductId == productId, ct)) return;
        try
        {
            db.Add(new StockLevel { ProductId = productId, Quantity = 0 });
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException)
        {
            db.ChangeTracker.Clear(); // another caller created it first (or the product vanished); the update below decides
        }
    }
}
