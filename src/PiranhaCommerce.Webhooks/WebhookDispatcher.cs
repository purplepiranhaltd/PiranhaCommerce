using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using PiranhaCommerce.Data;
using PiranhaCommerce.Core.Identity;

namespace PiranhaCommerce.Webhooks;

public interface IWebhookDispatcher
{
    /// <summary>
    /// One pass of the sender: turns new events into deliveries (one per interested endpoint that already existed when the event happened), sends the
    /// deliveries that are due, and puts failed ones back for a later try or gives up on them. Safe to run at any time and from several hosts at once.
    /// </summary>
    Task<DispatchResult> DispatchDueAsync(CancellationToken ct = default);

    /// <summary>Sends a <c>webhook.test</c> event to one endpoint now, whether or not it is switched on, and returns what happened. Null when the endpoint does not exist.</summary>
    [RequiresPermission(Permissions.WebhooksManage)]
    Task<WebhookDeliveryDto?> SendTestAsync(Guid endpointId, CancellationToken ct = default);

    /// <summary>Deletes events and finished deliveries older than the retention period. Returns how many events went.</summary>
    Task<int> PurgeAsync(CancellationToken ct = default);
}

/// <summary>
/// Sends the messages. Each is a POST of <c>{ id, type, created, apiVersion, data }</c> signed with the endpoint's secret: header
/// <c>PiranhaCommerce-Signature: t=&lt;unix seconds&gt;,v1=&lt;hex HMAC-SHA256 of "t.body"&gt;</c>. A 2xx answer is success; anything else (or no answer within 10 seconds,
/// or a redirect) is a failure, tried again after 1 minute, 5 minutes, 15 minutes, 1, 3, 6 and 12 hours. Delivery is at least once and in no guaranteed
/// order, so a receiver should ignore an event id it has already handled.
/// </summary>
public sealed class WebhookDispatcher(PiranhaCommerceDbContext db, IHttpClientFactory http, IOptions<WebhookOptions> options, ILogger<WebhookDispatcher> logger, TimeProvider? time = null) : IWebhookDispatcher
{
    public const string HttpClientName = "PiranhaCommerce.Webhooks";
    internal const string TestType = "webhook.test";
    private const int Batch = 50;
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan Lease = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan[] Backoff = [TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(15), TimeSpan.FromHours(1), TimeSpan.FromHours(3), TimeSpan.FromHours(6), TimeSpan.FromHours(12)];

    private DateTimeOffset Now => (time ?? TimeProvider.System).GetUtcNow();

    public async Task<DispatchResult> DispatchDueAsync(CancellationToken ct = default)
    {
        var fanned = await FanOutAsync(ct);
        var (delivered, retrying, gaveUp) = await DeliverAsync(ct);
        return new DispatchResult(fanned, delivered, retrying, gaveUp);
    }

    private async Task<int> FanOutAsync(CancellationToken ct)
    {
        db.ChangeTracker.Clear();
        var events = await db.Set<WebhookEvent>().AsNoTracking().Where(e => !e.FannedOut).OrderBy(e => e.OccurredUtc).ThenBy(e => e.Id).Take(Batch).ToListAsync(ct);
        if (events.Count == 0) return 0;
        var endpoints = await db.Set<WebhookEndpoint>().AsNoTracking().Where(e => e.Active).ToListAsync(ct);
        var strategy = db.Database.CreateExecutionStrategy();
        var done = 0;
        foreach (var e in events)
        {
            var wanted = endpoints.Where(x => x.CreatedUtc <= e.OccurredUtc && Matches(x.EventTypes, e.Type)).ToList();
            var took = await strategy.ExecuteInTransactionAsync(
                async token =>
                {
                    db.ChangeTracker.Clear();
                    // Whoever flips the flag first makes the deliveries; a second host finds it already done and does nothing.
                    var claimed = await db.Set<WebhookEvent>().Where(x => x.Id == e.Id && !x.FannedOut).ExecuteUpdateAsync(s => s.SetProperty(x => x.FannedOut, true), token);
                    if (claimed == 0) return false;
                    foreach (var ep in wanted)
                        db.Add(new WebhookDelivery { EventId = e.Id, EndpointId = ep.Id, EventType = e.Type, Status = WebhookDeliveryStatus.Pending, NextAttemptUtc = Now, CreatedUtc = Now });
                    await db.SaveChangesAsync(token);
                    return true;
                },
                async token => await db.Set<WebhookEvent>().AsNoTracking().AnyAsync(x => x.Id == e.Id && x.FannedOut, token),
                ct);
            if (took) done++;
        }
        return done;
    }

    internal static bool Matches(string subscribed, string type) => subscribed == "*" || subscribed.Split(',').Contains(type, StringComparer.Ordinal);

    private async Task<(int Delivered, int Retrying, int GaveUp)> DeliverAsync(CancellationToken ct)
    {
        int delivered = 0, retrying = 0, gaveUp = 0;
        var now = Now;
        db.ChangeTracker.Clear();
        var ids = await db.Set<WebhookDelivery>().AsNoTracking()
            .Where(d => d.Status == WebhookDeliveryStatus.Pending && d.NextAttemptUtc <= now && (d.LockedUntilUtc == null || d.LockedUntilUtc < now))
            .OrderBy(d => d.NextAttemptUtc).ThenBy(d => d.Id).Select(d => d.Id).Take(Batch).ToListAsync(ct);
        foreach (var id in ids)
        {
            // Take it for a couple of minutes with one conditional update, so another host does not send it as well.
            var lockedUntil = Now + Lease;
            var mine = await db.Set<WebhookDelivery>()
                .Where(d => d.Id == id && d.Status == WebhookDeliveryStatus.Pending && d.NextAttemptUtc <= now && (d.LockedUntilUtc == null || d.LockedUntilUtc < now))
                .ExecuteUpdateAsync(s => s.SetProperty(d => d.LockedUntilUtc, (DateTimeOffset?)lockedUntil), ct);
            if (mine == 0) continue;
            switch (await AttemptAsync(id, ct))
            {
                case WebhookDeliveryStatus.Succeeded: delivered++; break;
                case WebhookDeliveryStatus.Failed: gaveUp++; break;
                default: retrying++; break;
            }
        }
        return (delivered, retrying, gaveUp);
    }

    /// <summary>Sends one delivery that this host holds, and records the outcome.</summary>
    private async Task<WebhookDeliveryStatus> AttemptAsync(Guid deliveryId, CancellationToken ct)
    {
        db.ChangeTracker.Clear();
        var delivery = await db.Set<WebhookDelivery>().AsNoTracking().FirstAsync(d => d.Id == deliveryId, ct);
        var endpoint = await db.Set<WebhookEndpoint>().AsNoTracking().FirstOrDefaultAsync(e => e.Id == delivery.EndpointId, ct);
        var ev = await db.Set<WebhookEvent>().AsNoTracking().FirstOrDefaultAsync(e => e.Id == delivery.EventId, ct);
        var attempts = delivery.Attempts + 1;

        int? status = null;
        string? error = null, response = null;
        var ok = false;
        if (endpoint is null || ev is null) error = "The endpoint or the event no longer exists.";
        else if (!endpoint.Active && !delivery.IsTest) error = "The endpoint is switched off.";
        else
        {
            try
            {
                (status, response, ok) = await SendAsync(endpoint, ev, delivery, attempts, ct);
                if (!ok) error = status is null ? "No answer." : $"The endpoint answered {status}.";
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or InvalidOperationException)
            {
                error = Truncate(ex is OperationCanceledException ? "No answer within the time allowed." : ex.Message, 500);
            }
        }

        var finished = Now;
        var noRetry = endpoint is null || ev is null || (!endpoint.Active && !delivery.IsTest);
        var next = ok ? WebhookDeliveryStatus.Succeeded
            : noRetry || attempts >= Math.Max(1, options.Value.MaxAttempts) ? WebhookDeliveryStatus.Failed
            : WebhookDeliveryStatus.Pending;
        var retryAt = finished + Backoff[Math.Min(attempts, Backoff.Length) - 1];
        await db.Set<WebhookDelivery>().Where(d => d.Id == deliveryId).ExecuteUpdateAsync(s => s
            .SetProperty(d => d.Status, next).SetProperty(d => d.Attempts, attempts).SetProperty(d => d.LastStatusCode, status)
            .SetProperty(d => d.LastError, ok ? null : error).SetProperty(d => d.LastResponse, response)
            .SetProperty(d => d.NextAttemptUtc, next == WebhookDeliveryStatus.Pending ? retryAt : finished)
            .SetProperty(d => d.LockedUntilUtc, (DateTimeOffset?)null)
            .SetProperty(d => d.CompletedUtc, next == WebhookDeliveryStatus.Pending ? null : (DateTimeOffset?)finished), ct);
        if (next == WebhookDeliveryStatus.Failed) logger.LogWarning("Webhook delivery {DeliveryId} to {EndpointId} gave up after {Attempts} attempts: {Error}", deliveryId, delivery.EndpointId, attempts, error);
        return next;
    }

    private async Task<(int? Status, string? Response, bool Ok)> SendAsync(WebhookEndpoint endpoint, WebhookEvent ev, WebhookDelivery delivery, int attempt, CancellationToken ct)
    {
        var body = Envelope(ev);
        var t = Now.ToUnixTimeSeconds();
        var mac = Convert.ToHexString(HMACSHA256.HashData(Encoding.UTF8.GetBytes(endpoint.Secret), Encoding.UTF8.GetBytes($"{t}.{body}"))).ToLowerInvariant();

        using var request = new HttpRequestMessage(HttpMethod.Post, endpoint.Url) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        request.Headers.TryAddWithoutValidation("User-Agent", "PiranhaCommerce-Webhooks/1");
        request.Headers.TryAddWithoutValidation("PiranhaCommerce-Event-Id", ev.Id.ToString());
        request.Headers.TryAddWithoutValidation("PiranhaCommerce-Event-Type", ev.Type);
        request.Headers.TryAddWithoutValidation("PiranhaCommerce-Delivery-Id", delivery.Id.ToString());
        request.Headers.TryAddWithoutValidation("PiranhaCommerce-Delivery-Attempt", attempt.ToString());
        request.Headers.TryAddWithoutValidation("PiranhaCommerce-Signature", $"t={t},v1={mac}");

        // One deadline for the whole exchange, the reply body included (HttpClient.Timeout stops at the headers).
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
        limit.CancelAfter(Timeout);
        using var response = await http.CreateClient(HttpClientName).SendAsync(request, HttpCompletionOption.ResponseHeadersRead, limit.Token);
        var code = (int)response.StatusCode;
        var text = await ReadStartAsync(response, limit.Token);
        return (code, text, code is >= 200 and < 300);
    }

    /// <summary>The message as sent: <c>{ id, type, created, apiVersion, data }</c>, with the event's data written in as it was saved.</summary>
    internal static string Envelope(WebhookEvent ev)
    {
        using var stream = new MemoryStream();
        using (var w = new Utf8JsonWriter(stream))
        {
            w.WriteStartObject();
            w.WriteString("id", ev.Id);
            w.WriteString("type", ev.Type);
            w.WriteString("created", ev.OccurredUtc);
            w.WriteNumber("apiVersion", 1);
            w.WritePropertyName("data");
            w.WriteRawValue(ev.Payload);
            w.WriteEndObject();
        }
        return Encoding.UTF8.GetString(stream.ToArray());
    }

    private static async Task<string?> ReadStartAsync(HttpResponseMessage response, CancellationToken ct)
    {
        await using var s = await response.Content.ReadAsStreamAsync(ct);
        var buffer = new byte[1000];
        var n = 0;
        int read;
        while (n < buffer.Length && (read = await s.ReadAsync(buffer.AsMemory(n), ct)) > 0) n += read;
        return n == 0 ? null : Encoding.UTF8.GetString(buffer, 0, n).Replace("\0", "");
    }

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max];

    public async Task<WebhookDeliveryDto?> SendTestAsync(Guid endpointId, CancellationToken ct = default)
    {
        db.ChangeTracker.Clear();
        if (!await db.Set<WebhookEndpoint>().AnyAsync(e => e.Id == endpointId, ct)) return null;
        var ev = new WebhookEvent { Type = TestType, OccurredUtc = Now, Payload = "{\"message\":\"This is a test from PiranhaCommerce.\"}", FannedOut = true };
        var now = Now;
        var delivery = new WebhookDelivery
        {
            EventId = ev.Id, EndpointId = endpointId, EventType = TestType, Status = WebhookDeliveryStatus.Pending, NextAttemptUtc = now, CreatedUtc = now,
            LockedUntilUtc = now + Lease, IsTest = true,
        };
        db.Add(ev);
        db.Add(delivery);
        await db.SaveChangesAsync(ct);
        await AttemptAsync(delivery.Id, ct);
        return WebhookService.ToDto(await db.Set<WebhookDelivery>().AsNoTracking().FirstAsync(d => d.Id == delivery.Id, ct));
    }

    public async Task<int> PurgeAsync(CancellationToken ct = default)
    {
        var cutoff = Now - TimeSpan.FromDays(Math.Max(1, options.Value.RetentionDays));
        db.ChangeTracker.Clear();
        await db.Set<WebhookDelivery>().Where(d => d.Status != WebhookDeliveryStatus.Pending && d.CreatedUtc < cutoff).ExecuteDeleteAsync(ct);
        // An event goes when it is old, has been turned into deliveries and none of those are left.
        return await db.Set<WebhookEvent>()
            .Where(e => e.FannedOut && e.OccurredUtc < cutoff && !db.Set<WebhookDelivery>().Any(d => d.EventId == e.Id))
            .ExecuteDeleteAsync(ct);
    }
}

/// <summary>Runs <see cref="IWebhookDispatcher.DispatchDueAsync"/> every few seconds. Off when <see cref="WebhookOptions.PollSeconds"/> is 0.</summary>
internal sealed class WebhookDispatchService(IServiceScopeFactory scopes, IOptions<WebhookOptions> options, ILogger<WebhookDispatchService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(20), stoppingToken); // let the host finish starting (and migrations run) first
            var lastPurge = DateTimeOffset.MinValue;
            while (!stoppingToken.IsCancellationRequested)
            {
                var seconds = options.Value.PollSeconds;
                if (seconds > 0)
                {
                    try
                    {
                        await using var scope = scopes.CreateAsyncScope();
                        var dispatcher = scope.ServiceProvider.GetRequiredService<IWebhookDispatcher>();
                        await dispatcher.DispatchDueAsync(stoppingToken);
                        if (DateTimeOffset.UtcNow - lastPurge > TimeSpan.FromHours(6)) { await dispatcher.PurgeAsync(stoppingToken); lastPurge = DateTimeOffset.UtcNow; }
                    }
                    catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { throw; }
                    catch (Exception ex) { logger.LogWarning(ex, "Sending webhooks failed; it will be tried again."); }
                }
                await Task.Delay(TimeSpan.FromSeconds(Math.Max(1, seconds)), stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { /* shutting down */ }
    }
}
