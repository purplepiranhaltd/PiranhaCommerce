using System.ComponentModel.DataAnnotations;
using System.Security.Claims;
using System.Security.Cryptography;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using PiranhaCommerce.Api;
using PiranhaCommerce.Catalog;
using PiranhaCommerce.Core.Domain;
using PiranhaCommerce.Core.Email;
using PiranhaCommerce.Core.Paging;
using PiranhaCommerce.Core.Validation;
using PiranhaCommerce.Customers;
using PiranhaCommerce.Data;
using PiranhaCommerce.Core.Identity;

namespace PiranhaCommerce.Ecommerce;

/// <summary>Someone who asked to be emailed when a sold-out product can be bought again. They are emailed once, then the request is spent.</summary>
public class StockAlertSubscription : Entity
{
    /// <summary>The product that can be bought: a simple product or one variant (never a parent that has options).</summary>
    public Guid ProductId { get; set; }
    [PiranhaCommerce.Core.Audit.AuditMask] public required string Email { get; set; }

    /// <summary>Trimmed and lower case, so the same address in another letter case is the same request.</summary>
    public required string NormalizedEmail { get; set; }

    /// <summary>The signed-in shopper who asked, when there was one.</summary>
    public Guid? CustomerId { get; set; }

    /// <summary>A secret in the email's unsubscribe link: it can only remove this one request.</summary>
    public required string UnsubscribeToken { get; set; }
    public DateTimeOffset CreatedUtc { get; set; }
    public DateTimeOffset? NotifiedUtc { get; set; }

    /// <summary>Set while one sender has taken this request, so two running copies of the shop never both send. Expires so a failed send is tried again.</summary>
    public DateTimeOffset? ClaimedUtc { get; set; }
}

public sealed class StockAlertInput
{
    /// <summary>A simple product, or one variant of a product with options.</summary>
    public Guid ProductId { get; set; }

    [Required, EmailAddress, StringLength(256)] public string Email { get; set; } = "";
}

public sealed record StockAlertDemandDto(Guid ProductId, string Name, string? Sku, int Waiting, decimal InStock);

public sealed record StockAlertDto(Guid Id, Guid ProductId, string Email, Guid? CustomerId, DateTimeOffset CreatedUtc, DateTimeOffset? NotifiedUtc);

public interface IStockAlertService
{
    /// <summary>
    /// Records the request. Asking again changes nothing and still succeeds (so the answer never says whether an address was already known); asking again after being emailed asks afresh.
    /// Validation error when the product is unknown, not published, or has options (ask for a variant); conflict when it can be bought now, or the address already waits for too many products.
    /// </summary>
    Task SubscribeAsync(StockAlertInput input, Guid? customerId, CancellationToken ct = default);

    /// <summary>Removes the request the unsubscribe link belongs to. False for an unknown or used link.</summary>
    Task<bool> UnsubscribeAsync(string token, CancellationToken ct = default);

    /// <summary>Products people are waiting for, most wanted first.</summary>
    Task<IReadOnlyList<StockAlertDemandDto>> DemandAsync(CancellationToken ct = default);

    Task<PagedResult<StockAlertDto>> ListAsync(Guid? productId, int? page, int? pageSize, CancellationToken ct = default);
    [RequiresPermission(Permissions.CatalogEdit)]
    Task<bool> DeleteAsync(Guid id, CancellationToken ct = default);
}

internal sealed class StockAlertService(PiranhaCommerceDbContext db, IOptions<EcommerceOptions> options) : IStockAlertService
{
    /// <summary>The most products one address can be waiting for at once: nobody can use the shop to flood an inbox.</summary>
    public const int MaxWaitingPerAddress = 25;

    public async Task SubscribeAsync(StockAlertInput input, Guid? customerId, CancellationToken ct = default)
    {
        ModelValidator.ValidateOrThrow(input);
        var email = input.Email.Trim();
        var normalized = email.ToLowerInvariant();

        if (!await db.Set<ProductListing>().AsNoTracking().AnyAsync(l => l.ProductId == input.ProductId && l.Published, ct))
            throw new PiranhaCommerceValidationException(nameof(StockAlertInput.ProductId), "That product cannot be asked about.");
        if (await ProductVariants.HasOptionsAsync(db, input.ProductId, ct))
            throw new PiranhaCommerceValidationException(nameof(StockAlertInput.ProductId), "Choose one of the product's variants.");
        if (await StockAlerts.IsAvailableAsync(db, options.Value, input.ProductId, ct))
            throw new PiranhaCommerceConflictException("This is in stock now.");

        var existing = await db.Set<StockAlertSubscription>().FirstOrDefaultAsync(s => s.ProductId == input.ProductId && s.NormalizedEmail == normalized, ct);
        if (existing is not null)
        {
            if (existing.NotifiedUtc is not null) { existing.NotifiedUtc = null; existing.ClaimedUtc = null; await db.SaveChangesAsync(ct); }
            return;
        }
        if (await db.Set<StockAlertSubscription>().CountAsync(s => s.NormalizedEmail == normalized && s.NotifiedUtc == null, ct) >= MaxWaitingPerAddress)
            throw new PiranhaCommerceConflictException("This address is already waiting for as many products as it can.");

        db.Add(new StockAlertSubscription
        {
            ProductId = input.ProductId, Email = email, NormalizedEmail = normalized, CustomerId = customerId, CreatedUtc = DateTimeOffset.UtcNow,
            UnsubscribeToken = Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant(),
        });
        try { await db.SaveChangesAsync(ct); }
        catch (DbUpdateException) { db.ChangeTracker.Clear(); } // the same request arrived at the same moment: it is recorded either way
    }

    public async Task<bool> UnsubscribeAsync(string token, CancellationToken ct = default) =>
        !string.IsNullOrWhiteSpace(token) && token.Length <= 64 && await db.Set<StockAlertSubscription>().Where(s => s.UnsubscribeToken == token).ExecuteDeleteAsync(ct) > 0;

    public async Task<IReadOnlyList<StockAlertDemandDto>> DemandAsync(CancellationToken ct = default)
    {
        var location = options.Value.StockLocationId ?? StockLocations.DefaultId;
        var waiting = await db.Set<StockAlertSubscription>().AsNoTracking().Where(s => s.NotifiedUtc == null)
            .GroupBy(s => s.ProductId).Select(g => new { ProductId = g.Key, N = g.Count() }).OrderByDescending(x => x.N).Take(500).ToListAsync(ct);
        var ids = waiting.Select(w => w.ProductId).ToList();
        var products = await db.Set<Product>().AsNoTracking().Where(p => ids.Contains(p.Id)).Select(p => new { p.Id, p.Name, p.Sku }).ToListAsync(ct);
        var stock = await db.Set<StockLevel>().AsNoTracking().Where(l => ids.Contains(l.ProductId) && l.LocationId == location).ToDictionaryAsync(l => l.ProductId, l => l.Quantity, ct);
        return waiting.Join(products, w => w.ProductId, p => p.Id, (w, p) => new StockAlertDemandDto(p.Id, p.Name, p.Sku, w.N, stock.GetValueOrDefault(p.Id)))
            .OrderByDescending(d => d.Waiting).ThenBy(d => d.Name, StringComparer.Ordinal).ToList();
    }

    public async Task<PagedResult<StockAlertDto>> ListAsync(Guid? productId, int? page, int? pageSize, CancellationToken ct = default)
    {
        var (p, size) = Paging.Normalize(page, pageSize);
        var query = db.Set<StockAlertSubscription>().AsNoTracking().Where(s => productId == null || s.ProductId == productId);
        var total = await query.CountAsync(ct);
        var items = await query.OrderBy(s => s.CreatedUtc).ThenBy(s => s.Id).Skip((p - 1) * size).Take(size)
            .Select(s => new StockAlertDto(s.Id, s.ProductId, s.Email, s.CustomerId, s.CreatedUtc, s.NotifiedUtc)).ToListAsync(ct);
        return new PagedResult<StockAlertDto>(items, p, size, total);
    }

    public async Task<bool> DeleteAsync(Guid id, CancellationToken ct = default) => await db.Set<StockAlertSubscription>().Where(s => s.Id == id).ExecuteDeleteAsync(ct) > 0;
}

internal static class StockAlerts
{
    /// <summary>Whether online orders can take this product now: stock at the location checkout takes from.</summary>
    public static Task<bool> IsAvailableAsync(PiranhaCommerceDbContext db, EcommerceOptions options, Guid productId, CancellationToken ct)
    {
        var location = options.StockLocationId ?? StockLocations.DefaultId;
        return db.Set<StockLevel>().AsNoTracking().AnyAsync(l => l.ProductId == productId && l.LocationId == location && l.Quantity > 0, ct);
    }
}

public interface IStockAlertNotifier
{
    /// <summary>Emails everyone waiting for a product that can be bought again. Returns how many were sent. Safe to run from several copies of the shop at once.</summary>
    Task<int> RunOnceAsync(CancellationToken ct = default);
}

/// <summary>
/// Looks for products that are back in stock rather than listening for every kind of stock change (adjustments, cancellations, returns, transfers, imports all add stock).
/// Each waiting request is claimed with a conditional update before its email goes out, and marked done afterwards; a failed send is tried again once the claim expires.
/// </summary>
public sealed class StockAlertNotifier(PiranhaCommerceDbContext db, IEmailSender email, IOptions<EcommerceOptions> options, ILogger<StockAlertNotifier> logger, TimeProvider? time = null) : IStockAlertNotifier
{
    private static readonly TimeSpan Lease = TimeSpan.FromMinutes(10);
    private const int BatchSize = 200;

    public async Task<int> RunOnceAsync(CancellationToken ct = default)
    {
        var o = options.Value;
        var location = o.StockLocationId ?? StockLocations.DefaultId;
        var now = (time ?? TimeProvider.System).GetUtcNow();
        var stale = now - Lease;

        var due = await db.Set<StockAlertSubscription>().AsNoTracking()
            .Where(s => s.NotifiedUtc == null && (s.ClaimedUtc == null || s.ClaimedUtc < stale)
                && db.Set<StockLevel>().Any(l => l.ProductId == s.ProductId && l.LocationId == location && l.Quantity > 0)
                && db.Set<ProductListing>().Any(l => l.ProductId == s.ProductId && l.Published))
            .OrderBy(s => s.CreatedUtc).ThenBy(s => s.Id).Take(BatchSize).Select(s => new { s.Id, s.ProductId, s.Email, s.UnsubscribeToken }).ToListAsync(ct);

        var sent = 0;
        foreach (var s in due)
        {
            // Whoever's update changes the row owns it; anyone else gets 0 and moves on.
            var claimed = await db.Set<StockAlertSubscription>()
                .Where(x => x.Id == s.Id && x.NotifiedUtc == null && (x.ClaimedUtc == null || x.ClaimedUtc < stale))
                .ExecuteUpdateAsync(u => u.SetProperty(x => x.ClaimedUtc, (DateTimeOffset?)now), ct);
            if (claimed == 0) continue;

            try
            {
                var product = await db.Set<Product>().AsNoTracking().Where(p => p.Id == s.ProductId).Select(p => new { p.Name, p.Sku }).FirstOrDefaultAsync(ct);
                if (product is null) continue;
                var url = string.IsNullOrWhiteSpace(o.StockAlertUnsubscribeUrlTemplate) ? "" : o.StockAlertUnsubscribeUrlTemplate.Replace("{token}", Uri.EscapeDataString(s.UnsubscribeToken), StringComparison.Ordinal);
                await email.SendAsync(new EmailMessage(s.Email, EcommerceEmailTemplates.BackInStock, new Dictionary<string, object?>
                {
                    ["email"] = s.Email, ["productName"] = product.Name, ["sku"] = product.Sku ?? "", ["productId"] = s.ProductId.ToString(),
                    ["unsubscribeToken"] = s.UnsubscribeToken, ["unsubscribeUrl"] = url,
                }), ct);
                await db.Set<StockAlertSubscription>().Where(x => x.Id == s.Id).ExecuteUpdateAsync(u => u.SetProperty(x => x.NotifiedUtc, (DateTimeOffset?)now), ct);
                sent++;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex) { logger.LogWarning(ex, "A back-in-stock email could not be sent; it will be tried again."); } // the claim expires and it is picked up again
        }
        return sent;
    }
}

/// <summary>Runs the notifier now and then. Off when <see cref="EcommerceOptions.StockAlertPollSeconds"/> is 0.</summary>
internal sealed class StockAlertBackgroundService(IServiceScopeFactory scopes, IOptions<EcommerceOptions> options, ILogger<StockAlertBackgroundService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (options.Value.StockAlertPollSeconds <= 0) return;
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken); // let the host finish starting (and migrations run) first
            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    await using var scope = scopes.CreateAsyncScope();
                    var sent = await scope.ServiceProvider.GetRequiredService<IStockAlertNotifier>().RunOnceAsync(stoppingToken);
                    if (sent > 0) logger.LogInformation("Sent {Count} back-in-stock emails.", sent);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { throw; }
                catch (Exception ex) { logger.LogWarning(ex, "Checking for back-in-stock products failed; it will be tried again."); }
                await Task.Delay(TimeSpan.FromSeconds(options.Value.StockAlertPollSeconds), stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { /* shutting down */ }
    }
}

/// <summary>Requests are listed in an export and removed on erasure, matched by account or by email address (any letter case).</summary>
internal sealed class StockAlertCustomerData(PiranhaCommerceDbContext db) : ICustomerDataContributor
{
    public string Name => "stockAlerts";

    private IQueryable<StockAlertSubscription> Of(CustomerSubject s)
    {
        var email = s.Email.Trim().ToLowerInvariant();
        return db.Set<StockAlertSubscription>().Where(x => x.CustomerId == s.CustomerId || x.NormalizedEmail == email);
    }

    public Task<IReadOnlyList<string>> CheckErasureAsync(CustomerSubject subject, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<string>>([]);

    public async Task<object?> ExportAsync(CustomerSubject subject, CancellationToken ct = default)
    {
        var rows = await Of(subject).AsNoTracking().OrderBy(x => x.CreatedUtc).Select(x => new { x.ProductId, x.Email, x.CreatedUtc, x.NotifiedUtc }).ToListAsync(ct);
        return rows.Count == 0 ? null : rows;
    }

    public async Task<int> EraseAsync(CustomerSubject subject, CancellationToken ct = default) => await Of(subject).ExecuteDeleteAsync(ct);
}

internal sealed class StockAlertEndpoints : IApiEndpointContributor
{
    public void MapEndpoints(IEndpointRouteBuilder api)
    {
        var g = api.MapGroup("/stock-alerts").WithTags("Back-in-stock requests").RequirePermission(Permissions.CatalogView);
        g.MapGet("/demand", async (IStockAlertService svc, CancellationToken ct) => Results.Ok(await svc.DemandAsync(ct)));
        g.MapGet("/", async (Guid? productId, int? page, int? pageSize, IStockAlertService svc, CancellationToken ct) => Results.Ok(await svc.ListAsync(productId, page, pageSize, ct)));
        g.MapDelete("/{id:guid}", async (Guid id, IStockAlertService svc, CancellationToken ct) =>
            await svc.DeleteAsync(id, ct) ? Results.NoContent() : Results.NotFound()).RequirePermission(Permissions.CatalogEdit);
    }
}

internal sealed class StockAlertStorefrontEndpoints : IStorefrontEndpointContributor
{
    public void MapEndpoints(IEndpointRouteBuilder storefront)
    {
        // Anyone may ask (a guest gives an email address); a signed-in shopper's request is also tied to their account.
        storefront.MapPost("/stock-alerts", async (StockAlertInput input, ClaimsPrincipal user, IStockAlertService svc, CancellationToken ct) =>
        {
            await svc.SubscribeAsync(input, user.CustomerId(), ct);
            return Results.NoContent();
        }).AllowAnonymous().WithTags("Storefront back-in-stock");
        storefront.MapDelete("/stock-alerts/{token}", async (string token, IStockAlertService svc, CancellationToken ct) =>
            await svc.UnsubscribeAsync(token, ct) ? Results.NoContent() : Results.NotFound()).AllowAnonymous().WithTags("Storefront back-in-stock");
    }
}
