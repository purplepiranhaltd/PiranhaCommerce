using System.Globalization;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using PiranhaCommerce.Core.Plugins;
using PiranhaCommerce.Ecommerce;

namespace PiranhaCommerce.Payments.Stripe;

/// <summary>
/// Stripe settings (section <c>Piranha:Stripe</c>). Keep the two secrets out of source control: user secrets in development, the host's
/// secret store (environment variables, Key Vault) in production.
/// </summary>
public sealed class StripeOptions
{
    public const string SectionName = "Piranha:Stripe";

    /// <summary>The secret (or restricted) API key, sk_test_... or sk_live_.... Never sent to the browser, never logged.</summary>
    public string? SecretKey { get; set; }

    /// <summary>The webhook endpoint's signing secret, whsec_.... With none set every webhook is rejected.</summary>
    public string? WebhookSecret { get; set; }

    /// <summary>Where Stripe sends the shopper after paying. Set by you, never by the caller, so it cannot be turned into an open redirect. May contain {CHECKOUT_SESSION_ID}.</summary>
    public string? SuccessUrl { get; set; }

    /// <summary>Where Stripe sends the shopper if they back out.</summary>
    public string? CancelUrl { get; set; }

    public string ApiBaseUrl { get; set; } = "https://api.stripe.com";

    /// <summary>Pinned so the responses this code reads do not change under it when the account's default version moves.</summary>
    public string? ApiVersion { get; set; } = "2024-06-20";

    /// <summary>How far a webhook's timestamp may be from now. Stops an old captured webhook being replayed.</summary>
    public int WebhookToleranceSeconds { get; set; } = 300;
}

/// <summary>Takes card payments through Stripe Checkout (a page Stripe hosts, so card numbers never touch this application).</summary>
public sealed class StripeGateway(IHttpClientFactory http, IOptions<StripeOptions> options, ILogger<StripeGateway> logger, TimeProvider? time = null) : IRefundingGateway
{
    public const string HttpClientName = "PiranhaCommerce.Stripe";

    public string Name => "stripe";
    public string DisplayName => "Stripe (card payments)";

    private readonly TimeProvider _time = time ?? TimeProvider.System;
    private StripeOptions Options => options.Value;

    public async Task<GatewaySession> StartAsync(GatewayStartRequest request, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(Options.SecretKey)) throw new GatewayException("Stripe is not configured: Piranha:Stripe:SecretKey is empty.");
        if (string.IsNullOrWhiteSpace(Options.SuccessUrl) || string.IsNullOrWhiteSpace(Options.CancelUrl))
            throw new GatewayException("Stripe is not configured: Piranha:Stripe:SuccessUrl and CancelUrl are required.");

        var order = request.OrderId.ToString();
        var form = new List<KeyValuePair<string, string>>
        {
            new("mode", "payment"),
            new("success_url", Options.SuccessUrl),
            new("cancel_url", Options.CancelUrl),
            new("client_reference_id", order),
            new("metadata[order_id]", order),
            // Copied onto the payment itself too: refunds are matched back to the order through it.
            new("payment_intent_data[metadata][order_id]", order),
            new("line_items[0][quantity]", "1"),
            new("line_items[0][price_data][currency]", request.Currency.ToLowerInvariant()),
            new("line_items[0][price_data][unit_amount]", ToMinorUnits(request.Amount, request.Currency).ToString(CultureInfo.InvariantCulture)),
            new("line_items[0][price_data][product_data][name]", $"Order {request.OrderReference}"),
        };
        if (!string.IsNullOrWhiteSpace(request.CustomerEmail)) form.Add(new("customer_email", request.CustomerEmail));

        // The same order and amount within the same half hour is the same attempt: a retry returns the same session instead of a second one.
        var bucket = _time.GetUtcNow().ToUnixTimeSeconds() / 1800;
        using var response = await SendAsync(HttpMethod.Post, "/v1/checkout/sessions", new FormUrlEncodedContent(form), $"checkout-{order}-{request.Amount:0.00}-{bucket}", ct);
        using var body = await ReadAsync(response, ct);
        var url = body.RootElement.TryGetProperty("url", out var u) && u.ValueKind == JsonValueKind.String ? u.GetString() : null;
        var id = body.RootElement.TryGetProperty("id", out var i) ? i.GetString() : null;
        if (url is null) throw new GatewayException("Stripe answered without a checkout URL.");
        return new GatewaySession(url, id);
    }

    public async Task<GatewayRefund> RefundAsync(GatewayRefundRequest request, CancellationToken ct = default)
    {
        var form = new List<KeyValuePair<string, string>>
        {
            new("payment_intent", request.PaymentReference),
            new("amount", ToMinorUnits(request.Amount, request.Currency).ToString(CultureInfo.InvariantCulture)),
            new("metadata[order_id]", request.OrderId.ToString()),
        };
        // The same key with the same amount is the same refund: Stripe answers a repeat with the refund it already made.
        using var response = await SendAsync(HttpMethod.Post, "/v1/refunds", new FormUrlEncodedContent(form), "refund-" + request.IdempotencyKey, ct);
        using var body = await ReadAsync(response, ct);
        var root = body.RootElement;
        var id = Str(root, "id");
        var status = Str(root, "status") ?? "unknown";
        var currency = (Str(root, "currency") ?? request.Currency).ToUpperInvariant();
        if (id is null || !root.TryGetProperty("amount", out var amount) || !amount.TryGetInt64(out var minor))
            throw new GatewayException("Stripe answered a refund without an id or amount.");
        return new GatewayRefund(id, status, status == "succeeded", FromMinorUnits(minor, currency), currency);
    }

    public async Task<IReadOnlyList<GatewayEvent>> ReadWebhookAsync(GatewayWebhookRequest request, CancellationToken ct = default)
    {
        VerifySignature(request);

        using var doc = JsonDocument.Parse(request.Body);
        var root = doc.RootElement;
        var type = Str(root, "type");
        if (!root.TryGetProperty("data", out var data) || !data.TryGetProperty("object", out var obj)) return [];

        switch (type)
        {
            case "checkout.session.completed":
            case "checkout.session.async_payment_succeeded":
            {
                // A card payment is "paid" on completion; a delayed method (bank debit) completes unpaid and is confirmed by the async event.
                if (Str(obj, "payment_status") != "paid") return [];
                if (!TryOrderId(obj, out var orderId)) return []; // not one of ours (the same Stripe account may serve other things)
                var currency = (Str(obj, "currency") ?? "").ToUpperInvariant();
                var reference = Str(obj, "payment_intent") ?? Str(obj, "id");
                if (reference is null || !obj.TryGetProperty("amount_total", out var total) || !total.TryGetInt64(out var minor)) return [];
                return [new GatewayEvent(GatewayEventKind.PaymentConfirmed, orderId, FromMinorUnits(minor, currency), currency, reference)];
            }

            case "refund.created":
            case "refund.updated":
            case "refund.failed":
            {
                // A refund may start pending; only a succeeded one is money that has gone back. A failed one means it will not. The update event repeats the same id.
                var status = Str(obj, "status");
                var failed = type == "refund.failed" || status is "failed" or "canceled";
                if (status != "succeeded" && !failed) return [];
                var intent = Str(obj, "payment_intent");
                var reference = Str(obj, "id");
                var currency = (Str(obj, "currency") ?? "").ToUpperInvariant();
                if (intent is null || reference is null || !obj.TryGetProperty("amount", out var amount) || !amount.TryGetInt64(out var minor)) return [];

                // A refund carries no order id (a dashboard refund copies no metadata): read it from the payment we tagged.
                using var pi = await FetchAsync($"/v1/payment_intents/{Uri.EscapeDataString(intent)}", ct);
                if (!pi.RootElement.TryGetProperty("metadata", out var meta) || !TryOrderId(meta, out var orderId, "order_id")) return [];
                return [new GatewayEvent(failed ? GatewayEventKind.RefundFailed : GatewayEventKind.RefundConfirmed, orderId, FromMinorUnits(minor, currency), currency, reference)];
            }

            case "charge.dispute.created":
            case "charge.dispute.closed":
            {
                var status = Str(obj, "status");
                GatewayEventKind? kind = type == "charge.dispute.created" ? GatewayEventKind.DisputeOpened
                    : status is "won" or "warning_closed" ? GatewayEventKind.DisputeWon
                    : status == "lost" ? GatewayEventKind.DisputeLost : null; // a closed dispute in some other state moves nothing
                var intent = Str(obj, "payment_intent");
                var reference = Str(obj, "id");
                var currency = (Str(obj, "currency") ?? "").ToUpperInvariant();
                if (kind is null || intent is null || reference is null || !obj.TryGetProperty("amount", out var amount) || !amount.TryGetInt64(out var minor)) return [];

                DateTimeOffset? respondBy = null;
                if (obj.TryGetProperty("evidence_details", out var evidence) && evidence.ValueKind == JsonValueKind.Object
                    && evidence.TryGetProperty("due_by", out var due) && due.ValueKind == JsonValueKind.Number && due.TryGetInt64(out var dueSeconds))
                    respondBy = DateTimeOffset.FromUnixTimeSeconds(dueSeconds);

                using var pi = await FetchAsync($"/v1/payment_intents/{Uri.EscapeDataString(intent)}", ct);
                if (!pi.RootElement.TryGetProperty("metadata", out var meta) || !TryOrderId(meta, out var orderId, "order_id")) return [];
                return [new GatewayEvent(kind.Value, orderId, FromMinorUnits(minor, currency), currency, reference, Str(obj, "reason"), respondBy)];
            }

            default:
                return [];
        }
    }

    // ---- signature -------------------------------------------------------------------------------------------------------------

    /// <summary>
    /// Stripe-Signature is <c>t=timestamp,v1=hmac[,v1=hmac]</c> where hmac = HMAC-SHA256(secret, "timestamp.body"). Checked in constant
    /// time, and the timestamp must be recent, so a captured webhook cannot be replayed later.
    /// </summary>
    private void VerifySignature(GatewayWebhookRequest request)
    {
        var secret = Options.WebhookSecret;
        if (string.IsNullOrWhiteSpace(secret)) throw new GatewayWebhookRejectedException("No webhook secret is configured.");
        var header = request.Headers.FirstOrDefault(h => string.Equals(h.Key, "Stripe-Signature", StringComparison.OrdinalIgnoreCase)).Value; // do not rely on the caller having made the dictionary case-insensitive
        if (string.IsNullOrWhiteSpace(header))
            throw new GatewayWebhookRejectedException("Missing signature.");

        string? timestamp = null;
        var candidates = new List<string>();
        foreach (var part in header.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            var eq = part.IndexOf('=');
            if (eq <= 0) continue;
            var key = part[..eq];
            var value = part[(eq + 1)..];
            if (key == "t") timestamp = value;
            else if (key == "v1") candidates.Add(value);
        }
        if (timestamp is null || candidates.Count == 0 || !long.TryParse(timestamp, NumberStyles.None, CultureInfo.InvariantCulture, out var seconds))
            throw new GatewayWebhookRejectedException("Malformed signature.");

        var age = Math.Abs(_time.GetUtcNow().ToUnixTimeSeconds() - seconds);
        if (age > Options.WebhookToleranceSeconds) throw new GatewayWebhookRejectedException("Timestamp outside tolerance.");

        var payload = Encoding.UTF8.GetBytes(timestamp + ".").Concat(request.Body).ToArray();
        var expected = HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), payload);
        foreach (var candidate in candidates)
        {
            byte[] given;
            try { given = Convert.FromHexString(candidate); }
            catch (FormatException) { continue; }
            if (CryptographicOperations.FixedTimeEquals(given, expected)) return;
        }
        throw new GatewayWebhookRejectedException("Signature does not match.");
    }

    // ---- http ------------------------------------------------------------------------------------------------------------------

    private Task<JsonDocument> FetchAsync(string path, CancellationToken ct) => FetchCoreAsync(path, ct);

    private async Task<JsonDocument> FetchCoreAsync(string path, CancellationToken ct)
    {
        using var response = await SendAsync(HttpMethod.Get, path, null, null, ct);
        return await ReadAsync(response, ct);
    }

    private async Task<HttpResponseMessage> SendAsync(HttpMethod method, string path, HttpContent? content, string? idempotencyKey, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(Options.SecretKey)) throw new GatewayException("Stripe is not configured: Piranha:Stripe:SecretKey is empty.");
        var client = http.CreateClient(HttpClientName);
        using var message = new HttpRequestMessage(method, Options.ApiBaseUrl.TrimEnd('/') + path) { Content = content };
        message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", Options.SecretKey);
        if (!string.IsNullOrWhiteSpace(Options.ApiVersion)) message.Headers.Add("Stripe-Version", Options.ApiVersion);
        if (idempotencyKey is not null) message.Headers.Add("Idempotency-Key", idempotencyKey);

        HttpResponseMessage response;
        try { response = await client.SendAsync(message, ct); }
        catch (HttpRequestException ex) { throw new GatewayException("Could not reach Stripe.", ex); }
        catch (TaskCanceledException ex) when (!ct.IsCancellationRequested) { throw new GatewayException("Stripe did not answer in time.", ex); }

        if (!response.IsSuccessStatusCode)
        {
            // Stripe's error object names the problem without echoing our request; log it (never the key) and fail generically.
            var text = await response.Content.ReadAsStringAsync(ct);
            logger.LogWarning("Stripe answered {Status} to {Method} {Path}: {Body}", (int)response.StatusCode, method, path, Truncate(text, 500));
            response.Dispose();
            throw new GatewayException($"Stripe answered {(int)response.StatusCode}.");
        }
        return response;
    }

    private static async Task<JsonDocument> ReadAsync(HttpResponseMessage response, CancellationToken ct)
    {
        try { return await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(ct), cancellationToken: ct); }
        catch (JsonException ex) { throw new GatewayException("Stripe answered with something that is not JSON.", ex); }
    }

    // ---- helpers ---------------------------------------------------------------------------------------------------------------

    private static string? Str(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static bool TryOrderId(JsonElement obj, out Guid id, string? metadataKey = null)
    {
        var text = metadataKey is not null ? Str(obj, metadataKey)
            : Str(obj, "client_reference_id") ?? (obj.TryGetProperty("metadata", out var m) ? Str(m, "order_id") : null);
        return Guid.TryParse(text, out id);
    }

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max] + "...";

    private static readonly HashSet<string> ZeroDecimal = new(StringComparer.OrdinalIgnoreCase)
    {
        "BIF", "CLP", "DJF", "GNF", "JPY", "KMF", "KRW", "MGA", "PYG", "RWF", "UGX", "VND", "VUV", "XAF", "XOF", "XPF",
    };

    private static readonly HashSet<string> ThreeDecimal = new(StringComparer.OrdinalIgnoreCase) { "BHD", "JOD", "KWD", "OMR", "TND" };

    private static int Digits(string currency)
    {
        if (ThreeDecimal.Contains(currency)) throw new GatewayException($"Currency {currency} (three decimal places) is not supported by this gateway yet.");
        return ZeroDecimal.Contains(currency) ? 0 : 2;
    }

    /// <summary>Stripe amounts are whole minor units: pence, cents, or whole yen.</summary>
    internal static long ToMinorUnits(decimal amount, string currency)
    {
        var factor = (decimal)Math.Pow(10, Digits(currency));
        var minor = amount * factor;
        if (minor != decimal.Truncate(minor)) throw new GatewayException($"{amount} {currency} is not a whole number of minor units.");
        return (long)minor;
    }

    internal static decimal FromMinorUnits(long minor, string currency) => minor / (decimal)Math.Pow(10, Digits(currency));
}

/// <summary>Adds Stripe card payments. Requires the Ecommerce plugin; configure <see cref="StripeOptions"/>.</summary>
public sealed class StripePlugin : IPiranhaPlugin
{
    public PluginInfo Info { get; } = new("PiranhaCommerce.Payments.Stripe", "Stripe", "0.1.0", "Card payments through Stripe Checkout.");

    public IReadOnlyList<IPiranhaPlugin> Requires { get; } = [new EcommercePlugin()];

    public void ConfigureServices(IServiceCollection services)
    {
        services.AddOptions<StripeOptions>().BindConfiguration(StripeOptions.SectionName);
        services.AddHttpClient(StripeGateway.HttpClientName, c => c.Timeout = TimeSpan.FromSeconds(20));
        services.AddPaymentGateway<StripeGateway>();
    }
}
