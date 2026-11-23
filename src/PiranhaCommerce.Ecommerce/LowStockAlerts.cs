using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using PiranhaCommerce.Catalog;
using PiranhaCommerce.Core.Email;
using PiranhaCommerce.Data;

namespace PiranhaCommerce.Ecommerce;

/// <summary>
/// Remembers that the shop's alert address has been told a product is low, so the next check does not tell it again. The row goes when the product is
/// no longer low, so the next time it runs short is a new alert. The product id is the key: two copies of the shop cannot both add it.
/// </summary>
public class LowStockAlertState
{
    public Guid ProductId { get; set; }
    public DateTimeOffset AlertedUtc { get; set; }
}

public interface ILowStockAlerter
{
    /// <summary>
    /// Emails the alert address(es) one digest of the on-sale products that are newly at or below <see cref="EcommerceOptions.LowStockAlertLevel"/>.
    /// Returns how many products it told them about (0 when off, when nothing is newly low, or when no email could be sent). Safe to run from several copies of the shop at once.
    /// </summary>
    Task<int> RunOnceAsync(CancellationToken ct = default);
}

/// <summary>
/// A product is "low" by the same measure as the Low stock page: what is on hand across every location, counting each variant on its own. Only products on sale count,
/// so a draft you are still setting up does not nag. One digest per check, not one email per product, and each product once per dip.
/// </summary>
public sealed class LowStockAlerter(PiranhaDbContext db, IStockService stock, IEmailSender email, IOptions<EcommerceOptions> options, ILogger<LowStockAlerter> logger, TimeProvider? time = null) : ILowStockAlerter
{
    public async Task<int> RunOnceAsync(CancellationToken ct = default)
    {
        var o = options.Value;
        if (o.LowStockAlertLevel is not { } level) return 0;
        var addresses = (o.AlertEmail ?? "").Split([',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (addresses.Count == 0) return 0;

        var low = await stock.ListLowAsync(level, ct);
        var lowIds = low.Select(l => l.ProductId).ToList();
        var onSale = (await db.Set<ProductListing>().AsNoTracking().Where(l => l.Published && lowIds.Contains(l.ProductId)).Select(l => l.ProductId).ToListAsync(ct)).ToHashSet();
        var current = low.Where(l => onSale.Contains(l.ProductId)).ToList();
        var currentIds = current.Select(l => l.ProductId).ToList();

        // A product that is no longer low (restocked, withdrawn, deleted) can be told about again next time.
        await db.Set<LowStockAlertState>().Where(s => !currentIds.Contains(s.ProductId)).ExecuteDeleteAsync(ct);

        var known = (await db.Set<LowStockAlertState>().AsNoTracking().Where(s => currentIds.Contains(s.ProductId)).Select(s => s.ProductId).ToListAsync(ct)).ToHashSet();
        var now = (time ?? TimeProvider.System).GetUtcNow();
        var claimed = new List<LowStockDto>();
        foreach (var p in current.Where(p => !known.Contains(p.ProductId)))
        {
            var state = new LowStockAlertState { ProductId = p.ProductId, AlertedUtc = now };
            db.Add(state);
            try { await db.SaveChangesAsync(ct); claimed.Add(p); }
            catch (DbUpdateException) { db.Entry(state).State = EntityState.Detached; } // another copy of the shop added it first, and will send
        }
        if (claimed.Count == 0) return 0;

        var variables = new Dictionary<string, object?>
        {
            ["level"] = level.ToString("0.####", CultureInfo.InvariantCulture),
            ["count"] = claimed.Count,
            ["products"] = claimed.Select(p => (object?)new Dictionary<string, object?>
            {
                ["name"] = p.Name, ["sku"] = p.Sku ?? "", ["quantity"] = p.Quantity.ToString("0.####", CultureInfo.InvariantCulture), ["productId"] = p.ProductId.ToString(),
            }).ToList(),
        };

        var sent = 0;
        foreach (var to in addresses)
        {
            try { await email.SendAsync(new EmailMessage(to, EcommerceEmailTemplates.LowStock, variables), ct); sent++; }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex) { logger.LogWarning(ex, "The low stock alert could not be sent to one alert address."); } // not the address
        }

        if (sent == 0)
        {
            // Nobody was told: forget these so the next check tries again.
            var ids = claimed.Select(p => p.ProductId).ToList();
            await db.Set<LowStockAlertState>().Where(s => ids.Contains(s.ProductId)).ExecuteDeleteAsync(ct);
            return 0;
        }
        return claimed.Count;
    }
}

/// <summary>Runs the alerter now and then. Off when <see cref="EcommerceOptions.LowStockAlertPollSeconds"/> is 0 (or no level is set).</summary>
internal sealed class LowStockAlertBackgroundService(IServiceScopeFactory scopes, IOptions<EcommerceOptions> options, ILogger<LowStockAlertBackgroundService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (options.Value.LowStockAlertPollSeconds <= 0 || options.Value.LowStockAlertLevel is null) return;
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(45), stoppingToken); // let the host finish starting (and migrations run) first
            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    await using var scope = scopes.CreateAsyncScope();
                    var told = await scope.ServiceProvider.GetRequiredService<ILowStockAlerter>().RunOnceAsync(stoppingToken);
                    if (told > 0) logger.LogInformation("Told the alert address about {Count} low stock products.", told);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { throw; }
                catch (Exception ex) { logger.LogWarning(ex, "Checking for low stock failed; it will be tried again."); }
                await Task.Delay(TimeSpan.FromSeconds(options.Value.LowStockAlertPollSeconds), stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { /* shutting down */ }
    }
}
