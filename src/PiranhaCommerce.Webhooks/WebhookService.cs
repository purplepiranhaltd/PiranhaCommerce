using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using PiranhaCommerce.Core.Events;
using PiranhaCommerce.Core.Paging;
using PiranhaCommerce.Core.Validation;
using PiranhaCommerce.Data;

namespace PiranhaCommerce.Webhooks;

public interface IWebhookService
{
    Task<IReadOnlyList<WebhookEndpointDto>> ListAsync(CancellationToken ct = default);
    Task<WebhookEndpointDto?> GetAsync(Guid id, CancellationToken ct = default);

    /// <summary>The result carries the new signing secret, once. Validation error for a bad address or an event type that does not exist.</summary>
    Task<WebhookEndpointDto> CreateAsync(WebhookEndpointInput input, CancellationToken ct = default);

    /// <summary>The secret is kept. Null when not found.</summary>
    Task<WebhookEndpointDto?> UpdateAsync(Guid id, WebhookEndpointInput input, CancellationToken ct = default);

    /// <summary>Makes a new secret (the old one stops working at once) and returns it, once. Null when not found.</summary>
    Task<WebhookEndpointDto?> RotateSecretAsync(Guid id, CancellationToken ct = default);

    /// <summary>Also deletes its deliveries.</summary>
    Task<bool> DeleteAsync(Guid id, CancellationToken ct = default);

    /// <summary>Newest first. Null when the endpoint does not exist.</summary>
    Task<PagedResult<WebhookDeliveryDto>?> ListDeliveriesAsync(Guid endpointId, WebhookDeliveryStatus? status, int? page, int? pageSize, CancellationToken ct = default);

    /// <summary>Puts a delivery (failed, or even a successful one) back to be sent again, with its full set of attempts. Null when not found.</summary>
    Task<WebhookDeliveryDto?> RedeliverAsync(Guid deliveryId, CancellationToken ct = default);

    /// <summary>The event types that can be chosen.</summary>
    IReadOnlyList<WebhookEventTypeDto> EventTypes();
}

internal sealed class WebhookService(PiranhaCommerceDbContext db, IEnumerable<IEventTypeContributor> contributors, IOptions<WebhookOptions> options, TimeProvider? time = null) : IWebhookService
{
    private TimeProvider Clock => time ?? TimeProvider.System;

    public IReadOnlyList<WebhookEventTypeDto> EventTypes() =>
        contributors.SelectMany(c => c.GetEventTypes()).GroupBy(t => t.Type).Select(g => new WebhookEventTypeDto(g.Key, g.First().Description)).OrderBy(t => t.Type).ToList();

    public async Task<IReadOnlyList<WebhookEndpointDto>> ListAsync(CancellationToken ct = default) =>
        (await db.Set<WebhookEndpoint>().AsNoTracking().OrderBy(e => e.CreatedUtc).ThenBy(e => e.Id).ToListAsync(ct)).Select(e => ToDto(e)).ToList();

    public async Task<WebhookEndpointDto?> GetAsync(Guid id, CancellationToken ct = default) =>
        await db.Set<WebhookEndpoint>().AsNoTracking().FirstOrDefaultAsync(e => e.Id == id, ct) is { } e ? ToDto(e) : null;

    public async Task<WebhookEndpointDto> CreateAsync(WebhookEndpointInput input, CancellationToken ct = default)
    {
        var (url, types) = Check(input);
        var endpoint = new WebhookEndpoint { Url = url, Description = Clean(input.Description), Secret = NewSecret(), EventTypes = types, Active = input.Active };
        db.Add(endpoint);
        await db.SaveChangesAsync(ct);
        return ToDto(endpoint, showSecret: true);
    }

    public async Task<WebhookEndpointDto?> UpdateAsync(Guid id, WebhookEndpointInput input, CancellationToken ct = default)
    {
        var (url, types) = Check(input);
        var endpoint = await db.Set<WebhookEndpoint>().FirstOrDefaultAsync(e => e.Id == id, ct);
        if (endpoint is null) return null;
        endpoint.Url = url; endpoint.Description = Clean(input.Description); endpoint.EventTypes = types; endpoint.Active = input.Active;
        await db.SaveChangesAsync(ct);
        return ToDto(endpoint);
    }

    public async Task<WebhookEndpointDto?> RotateSecretAsync(Guid id, CancellationToken ct = default)
    {
        var endpoint = await db.Set<WebhookEndpoint>().FirstOrDefaultAsync(e => e.Id == id, ct);
        if (endpoint is null) return null;
        endpoint.Secret = NewSecret();
        await db.SaveChangesAsync(ct);
        return ToDto(endpoint, showSecret: true);
    }

    public async Task<bool> DeleteAsync(Guid id, CancellationToken ct = default) => await db.DeleteTrackedAsync<WebhookEndpoint>(e => e.Id == id, ct) > 0;

    public async Task<PagedResult<WebhookDeliveryDto>?> ListDeliveriesAsync(Guid endpointId, WebhookDeliveryStatus? status, int? page, int? pageSize, CancellationToken ct = default)
    {
        if (!await db.Set<WebhookEndpoint>().AnyAsync(e => e.Id == endpointId, ct)) return null;
        var (p, size) = Paging.Normalize(page, pageSize);
        var query = db.Set<WebhookDelivery>().AsNoTracking().Where(d => d.EndpointId == endpointId);
        if (status is { } s) query = query.Where(d => d.Status == s);
        var total = await query.CountAsync(ct);
        var rows = await query.OrderByDescending(d => d.CreatedUtc).ThenBy(d => d.Id).Skip((p - 1) * size).Take(size).ToListAsync(ct);
        return new PagedResult<WebhookDeliveryDto>(rows.Select(ToDto).ToList(), p, size, total);
    }

    public async Task<WebhookDeliveryDto?> RedeliverAsync(Guid deliveryId, CancellationToken ct = default)
    {
        var now = Clock.GetUtcNow();
        var changed = await db.Set<WebhookDelivery>().Where(d => d.Id == deliveryId)
            .ExecuteUpdateAsync(s => s.SetProperty(d => d.Status, WebhookDeliveryStatus.Pending).SetProperty(d => d.Attempts, 0)
                .SetProperty(d => d.NextAttemptUtc, now).SetProperty(d => d.LockedUntilUtc, (DateTimeOffset?)null).SetProperty(d => d.CompletedUtc, (DateTimeOffset?)null), ct);
        if (changed == 0) return null;
        return ToDto(await db.Set<WebhookDelivery>().AsNoTracking().FirstAsync(d => d.Id == deliveryId, ct));
    }

    private (string Url, string Types) Check(WebhookEndpointInput input)
    {
        ModelValidator.ValidateOrThrow(input);
        var raw = input.Url.Trim();
        if (!Uri.TryCreate(raw, UriKind.Absolute, out var uri) || uri.Scheme is not ("https" or "http") || uri.UserInfo.Length > 0 || uri.Fragment.Length > 0)
            throw new PiranhaCommerceValidationException(nameof(WebhookEndpointInput.Url), "Give a full web address such as https://example.com/hooks, with no user name, password or # part.");
        if (uri.Scheme == "http" && !uri.IsLoopback && !options.Value.AllowInsecureUrls)
            throw new PiranhaCommerceValidationException(nameof(WebhookEndpointInput.Url), "Use https:// (http:// is only allowed for localhost).");

        var wanted = input.EventTypes.Select(t => t.Trim()).Where(t => t.Length > 0).Distinct(StringComparer.Ordinal).ToList();
        if (wanted.Count == 0 || wanted.Contains("*")) return (uri.ToString(), "*");
        var known = EventTypes().Select(t => t.Type).ToHashSet();
        var unknown = wanted.FirstOrDefault(t => !known.Contains(t));
        if (unknown is not null) throw new PiranhaCommerceValidationException(nameof(WebhookEndpointInput.EventTypes), $"\"{unknown}\" is not an event type.");
        return (uri.ToString(), string.Join(',', wanted.Order(StringComparer.Ordinal)));
    }

    private static string? Clean(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();

    public static string NewSecret() => "whsec_" + Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).Replace('+', '-').Replace('/', '_').TrimEnd('=');

    public static IReadOnlyList<string> Split(string types) => types == "*" ? ["*"] : types.Split(',', StringSplitOptions.RemoveEmptyEntries);

    private static WebhookEndpointDto ToDto(WebhookEndpoint e, bool showSecret = false) =>
        new(e.Id, e.Url, e.Description, Split(e.EventTypes), e.Active, "…" + e.Secret[^4..], e.CreatedUtc, showSecret ? e.Secret : null);

    internal static WebhookDeliveryDto ToDto(WebhookDelivery d) =>
        new(d.Id, d.EventId, d.EndpointId, d.EventType, d.Status, d.Attempts, d.NextAttemptUtc, d.LastStatusCode, d.LastError, d.LastResponse, d.CreatedUtc, d.CompletedUtc, d.IsTest);
}

/// <summary>Writes events into the current transaction. See <see cref="IEventPublisher"/>.</summary>
internal sealed class EventPublisher(PiranhaCommerceDbContext db, TimeProvider? time = null) : IEventPublisher
{
    internal static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };

    public void Publish(string type, object data) => db.Add(new WebhookEvent
    {
        Type = type, OccurredUtc = (time ?? TimeProvider.System).GetUtcNow(), Payload = JsonSerializer.Serialize(data, Json),
    });
}
