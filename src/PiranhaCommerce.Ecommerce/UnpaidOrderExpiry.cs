using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using PiranhaCommerce.Core.Validation;
using PiranhaCommerce.Data;

namespace PiranhaCommerce.Ecommerce;

public interface IUnpaidOrderExpiry
{
    /// <summary>
    /// Cancels orders still at Placed that are older than <paramref name="olderThan"/> and have had no payment of any kind, putting their stock (and
    /// promo use) back and telling the buyer, exactly as a cancel by staff does. An order paid a moment before is left alone: the check is made inside the
    /// cancel itself, after the order row is held. Returns how many were cancelled. Safe to run at any time and from several hosts at once.
    /// </summary>
    Task<int> ExpireAsync(TimeSpan olderThan, CancellationToken ct = default);
}

internal sealed partial class OrderService : IUnpaidOrderExpiry
{
    public const string ExpiryActor = "system (unpaid order expired)";
    private const int ExpiryBatch = 100;

    public async Task<int> ExpireAsync(TimeSpan olderThan, CancellationToken ct = default)
    {
        if (olderThan <= TimeSpan.Zero) return 0; // never "everything"
        var cutoff = DateTimeOffset.UtcNow - olderThan;
        var expired = 0;
        var skipped = new HashSet<Guid>();
        while (!ct.IsCancellationRequested)
        {
            // Zero-total orders (fully covered by a promotion) never had anything to pay, so they are not "unpaid".
            var ids = await db.Set<Order>().AsNoTracking()
                .Where(o => o.Stage == OrderStage.Placed && o.CreatedUtc < cutoff && o.Total > 0 && !skipped.Contains(o.Id)
                            && !db.Set<Payment>().Any(p => p.OrderId == o.Id))
                .OrderBy(o => o.CreatedUtc).Select(o => o.Id).Take(ExpiryBatch).ToListAsync(ct);
            if (ids.Count == 0) break;
            foreach (var id in ids)
            {
                try
                {
                    if (await ChangeStageCoreAsync(id, OrderStage.Cancelled, ExpiryActor, onlyIfUnpaid: true, ct) is not null) expired++;
                }
                catch (PiranhaConflictException) { skipped.Add(id); } // paid, moved on, or shipped meanwhile: not ours to cancel
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    skipped.Add(id); // never loop on one that keeps failing; it is tried again on the next pass
                    logger.LogWarning(ex, "Unpaid order {OrderId} could not be expired.", id);
                }
            }
        }
        return expired;
    }
}

/// <summary>Cancels unpaid orders after <see cref="EcommerceOptions.UnpaidOrderHours"/>. Off when that is 0.</summary>
internal sealed class UnpaidOrderExpiryService(IServiceScopeFactory scopes, IOptions<EcommerceOptions> options, ILogger<UnpaidOrderExpiryService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await Task.Delay(TimeSpan.FromMinutes(2), stoppingToken); // let the host finish starting (and migrations run) first
            while (!stoppingToken.IsCancellationRequested)
            {
                var hours = options.Value.UnpaidOrderHours;
                if (hours > 0)
                {
                    try
                    {
                        await using var scope = scopes.CreateAsyncScope();
                        var n = await scope.ServiceProvider.GetRequiredService<IUnpaidOrderExpiry>().ExpireAsync(TimeSpan.FromHours(hours), stoppingToken);
                        if (n > 0) logger.LogInformation("Cancelled {Count} unpaid orders.", n);
                    }
                    catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { throw; }
                    catch (Exception ex) { logger.LogWarning(ex, "Expiring unpaid orders failed; it will be tried again."); }
                }
                await Task.Delay(TimeSpan.FromMinutes(15), stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { /* shutting down */ }
    }
}
