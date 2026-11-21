using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using PiranhaCommerce.Data;

namespace PiranhaCommerce.Ecommerce;

public interface IBasketHousekeeper
{
    /// <summary>
    /// Deletes open baskets nobody has touched for <paramref name="olderThan"/> (their lines go with them). Checked-out baskets are never
    /// touched: they are the record of an order. Returns how many were deleted. Safe to run at any time and from several hosts at once.
    /// </summary>
    Task<int> RemoveAbandonedAsync(TimeSpan olderThan, CancellationToken ct = default);
}

internal sealed class BasketHousekeeper(PiranhaDbContext db) : IBasketHousekeeper
{
    /// <summary>Per statement, so a large backlog never holds one long lock.</summary>
    private const int BatchSize = 500;

    public async Task<int> RemoveAbandonedAsync(TimeSpan olderThan, CancellationToken ct = default)
    {
        if (olderThan <= TimeSpan.Zero) return 0; // never "everything"
        var cutoff = DateTimeOffset.UtcNow - olderThan;
        var total = 0;
        while (true)
        {
            // Last touched = last change, or creation for a basket never changed. The status is part of the delete itself, so a basket
            // checked out a moment ago is not removed.
            var ids = await db.Set<Basket>().AsNoTracking()
                .Where(b => b.Status == BasketStatus.Open && (b.UpdatedUtc ?? b.CreatedUtc) < cutoff)
                .OrderBy(b => b.CreatedUtc).Select(b => b.Id).Take(BatchSize).ToListAsync(ct);
            if (ids.Count == 0) return total;
            var deleted = await db.Set<Basket>()
                .Where(b => ids.Contains(b.Id) && b.Status == BasketStatus.Open && (b.UpdatedUtc ?? b.CreatedUtc) < cutoff)
                .ExecuteDeleteAsync(ct);
            total += deleted;
            if (deleted == 0) return total; // nothing left that qualifies (they were all touched meanwhile)
        }
    }
}

/// <summary>Removes abandoned baskets now and then. Off when <see cref="EcommerceOptions.AbandonedBasketDays"/> is 0.</summary>
internal sealed class BasketHousekeepingService(IServiceScopeFactory scopes, IOptions<EcommerceOptions> options, ILogger<BasketHousekeepingService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            // Not at the very start: let the host finish starting (and migrations run) first.
            await Task.Delay(TimeSpan.FromMinutes(1), stoppingToken);
            while (!stoppingToken.IsCancellationRequested)
            {
                var days = options.Value.AbandonedBasketDays;
                if (days > 0)
                {
                    try
                    {
                        await using var scope = scopes.CreateAsyncScope();
                        var removed = await scope.ServiceProvider.GetRequiredService<IBasketHousekeeper>().RemoveAbandonedAsync(TimeSpan.FromDays(days), stoppingToken);
                        if (removed > 0) logger.LogInformation("Removed {Count} abandoned baskets.", removed);
                    }
                    catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { throw; }
                    catch (Exception ex) { logger.LogWarning(ex, "Removing abandoned baskets failed; it will be tried again."); }
                }
                await Task.Delay(TimeSpan.FromHours(6), stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { /* shutting down */ }
    }
}
