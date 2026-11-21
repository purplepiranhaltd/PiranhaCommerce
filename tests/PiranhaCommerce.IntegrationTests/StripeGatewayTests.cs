using System.Net;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using PiranhaCommerce.Ecommerce;
using PiranhaCommerce.Payments.Stripe;

namespace PiranhaCommerce.IntegrationTests;

/// <summary>The Stripe gateway on its own: no network, no host. Stripe's API is a fake handler; webhooks are signed the way Stripe signs them.</summary>
public class StripeGatewayTests
{
    private const string Whsec = "whsec_test_secret";
    private static readonly DateTimeOffset Now = new(2026, 11, 22, 12, 0, 0, TimeSpan.Zero);

    private sealed class Clock : TimeProvider { public override DateTimeOffset GetUtcNow() => Now; }

    private sealed class FakeStripe(Func<HttpRequestMessage, (HttpStatusCode, string)> reply) : HttpMessageHandler, IHttpClientFactory
    {
        public List<(HttpRequestMessage Request, string Body)> Calls { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(ct);
            Calls.Add((request, body));
            var (status, json) = reply(request);
            return new HttpResponseMessage(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
        }

        public HttpClient CreateClient(string name) => new(this, disposeHandler: false);
    }

    private static StripeGateway Gateway(FakeStripe? stripe = null, Action<StripeOptions>? tweak = null)
    {
        var o = new StripeOptions { SecretKey = "sk_test_x", WebhookSecret = Whsec, SuccessUrl = "https://shop.example/thanks?s={CHECKOUT_SESSION_ID}", CancelUrl = "https://shop.example/cart" };
        tweak?.Invoke(o);
        return new StripeGateway(stripe ?? new FakeStripe(_ => (HttpStatusCode.OK, "{}")), Options.Create(o), NullLogger<StripeGateway>.Instance, new Clock());
    }

    private static GatewayWebhookRequest Signed(string json, DateTimeOffset? at = null, string secret = Whsec)
    {
        var body = Encoding.UTF8.GetBytes(json);
        var t = (at ?? Now).ToUnixTimeSeconds();
        var mac = Convert.ToHexString(HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), Encoding.UTF8.GetBytes($"{t}.").Concat(body).ToArray())).ToLowerInvariant();
        return new GatewayWebhookRequest(new Dictionary<string, string> { ["stripe-signature"] = $"t={t},v1={mac}" }, body);
    }

    private static string Completed(Guid order, string status = "paid", long total = 1250, string currency = "gbp", string type = "checkout.session.completed") =>
        "{\"type\":\"" + type + "\",\"data\":{\"object\":{\"id\":\"cs_1\",\"client_reference_id\":\"" + order + "\",\"payment_status\":\"" + status
        + "\",\"amount_total\":" + total + ",\"currency\":\"" + currency + "\",\"payment_intent\":\"pi_123\"}}}";

    private static Dictionary<string, string> Form(string body) =>
        body.Split('&').Select(p => p.Split('=')).ToDictionary(p => Uri.UnescapeDataString(p[0]), p => Uri.UnescapeDataString(p[1]));

    [Fact]
    public async Task Starting_a_payment_creates_a_checkout_session_for_the_exact_amount_in_pence_and_tags_it_with_the_order()
    {
        var order = Guid.NewGuid();
        var stripe = new FakeStripe(_ => (HttpStatusCode.OK, "{\"id\":\"cs_test_1\",\"url\":\"https://checkout.stripe.com/c/pay/cs_test_1\"}"));
        var session = await Gateway(stripe).StartAsync(new GatewayStartRequest(order, "PC-1001", 12.34m, "GBP", "ada@example.com"));

        Assert.Equal("https://checkout.stripe.com/c/pay/cs_test_1", session.RedirectUrl);
        var (request, body) = Assert.Single(stripe.Calls);
        Assert.Equal("https://api.stripe.com/v1/checkout/sessions", request.RequestUri!.ToString());
        Assert.Equal("Bearer sk_test_x", request.Headers.Authorization!.ToString());
        Assert.Contains("Idempotency-Key", request.Headers.Select(h => h.Key));
        var form = Form(body);
        Assert.Equal("1234", form["line_items[0][price_data][unit_amount]"]);
        Assert.Equal("gbp", form["line_items[0][price_data][currency]"]);
        Assert.Equal(order.ToString(), form["client_reference_id"]);
        Assert.Equal(order.ToString(), form["payment_intent_data[metadata][order_id]"]);
        Assert.Equal("https://shop.example/cart", form["cancel_url"]);
        Assert.Equal("ada@example.com", form["customer_email"]);
    }

    [Fact]
    public async Task A_failed_or_unconfigured_start_is_a_gateway_exception_that_never_carries_the_key()
    {
        var down = new FakeStripe(_ => (HttpStatusCode.InternalServerError, "{\"error\":{\"message\":\"boom\"}}"));
        var ex = await Assert.ThrowsAsync<GatewayException>(() => Gateway(down).StartAsync(new GatewayStartRequest(Guid.NewGuid(), "PC-1", 5m, "GBP", null)));
        Assert.DoesNotContain("sk_test_x", ex.Message);

        await Assert.ThrowsAsync<GatewayException>(() => Gateway(tweak: o => o.SecretKey = null).StartAsync(new GatewayStartRequest(Guid.NewGuid(), "PC-1", 5m, "GBP", null)));
        await Assert.ThrowsAsync<GatewayException>(() => Gateway(tweak: o => o.SuccessUrl = null).StartAsync(new GatewayStartRequest(Guid.NewGuid(), "PC-1", 5m, "GBP", null)));
        await Assert.ThrowsAsync<GatewayException>(() => Gateway().StartAsync(new GatewayStartRequest(Guid.NewGuid(), "PC-1", 5m, "KWD", null)));
    }

    [Fact]
    public async Task Whole_unit_currencies_are_not_multiplied_by_a_hundred()
    {
        var stripe = new FakeStripe(_ => (HttpStatusCode.OK, "{\"id\":\"cs\",\"url\":\"https://x\"}"));
        await Gateway(stripe).StartAsync(new GatewayStartRequest(Guid.NewGuid(), "PC-1", 1500m, "JPY", null));
        Assert.Equal("1500", Form(stripe.Calls[0].Body)["line_items[0][price_data][unit_amount]"]);
    }

    [Fact]
    public async Task A_paid_session_becomes_a_confirmed_payment_matched_by_the_payment_intent()
    {
        var order = Guid.NewGuid();
        var e = Assert.Single(await Gateway().ReadWebhookAsync(Signed(Completed(order))));
        Assert.Equal((GatewayEventKind.PaymentConfirmed, order, 12.50m, "GBP", "pi_123"), (e.Kind, e.OrderId, e.Amount, e.Currency, e.Reference));

        // The delayed-payment confirmation carries the same payment intent, so recording it twice is harmless.
        var later = Assert.Single(await Gateway().ReadWebhookAsync(Signed(Completed(order, type: "checkout.session.async_payment_succeeded"))));
        Assert.Equal(e.Reference, later.Reference);
    }

    [Fact]
    public async Task Unpaid_sessions_other_event_types_and_sessions_that_are_not_ours_are_ignored()
    {
        Assert.Empty(await Gateway().ReadWebhookAsync(Signed(Completed(Guid.NewGuid(), status: "unpaid"))));
        Assert.Empty(await Gateway().ReadWebhookAsync(Signed("{\"type\":\"customer.created\",\"data\":{\"object\":{\"id\":\"cus_1\"}}}")));
        Assert.Empty(await Gateway().ReadWebhookAsync(Signed("{\"type\":\"checkout.session.completed\",\"data\":{\"object\":{\"id\":\"cs_9\",\"payment_status\":\"paid\",\"amount_total\":100,\"currency\":\"gbp\"}}}")));
    }

    [Fact]
    public async Task A_succeeded_refund_finds_its_order_through_the_payment_it_refunds_and_a_pending_one_waits()
    {
        var order = Guid.NewGuid();
        var stripe = new FakeStripe(r => r.RequestUri!.AbsolutePath == "/v1/payment_intents/pi_123"
            ? (HttpStatusCode.OK, "{\"id\":\"pi_123\",\"metadata\":{\"order_id\":\"" + order + "\"}}")
            : (HttpStatusCode.NotFound, "{}"));
        static string Refund(string status) =>
            "{\"type\":\"refund.created\",\"data\":{\"object\":{\"id\":\"re_1\",\"status\":\"" + status + "\",\"amount\":200,\"currency\":\"gbp\",\"payment_intent\":\"pi_123\"}}}";

        Assert.Empty(await Gateway(stripe).ReadWebhookAsync(Signed(Refund("pending"))));
        Assert.Empty(stripe.Calls); // nothing fetched for a refund that has not happened

        var e = Assert.Single(await Gateway(stripe).ReadWebhookAsync(Signed(Refund("succeeded"))));
        Assert.Equal((GatewayEventKind.RefundConfirmed, order, 2.00m, "GBP", "re_1"), (e.Kind, e.OrderId, e.Amount, e.Currency, e.Reference));
    }

    [Fact]
    public async Task Disputes_and_failed_refunds_are_read_with_their_order_amount_reason_and_deadline()
    {
        var order = Guid.NewGuid();
        var stripe = new FakeStripe(r => r.RequestUri!.AbsolutePath == "/v1/payment_intents/pi_123"
            ? (HttpStatusCode.OK, "{\"id\":\"pi_123\",\"metadata\":{\"order_id\":\"" + order + "\"}}")
            : (HttpStatusCode.NotFound, "{}"));
        static string Dispute(string type, string status) =>
            "{\"type\":\"" + type + "\",\"data\":{\"object\":{\"id\":\"dp_1\",\"status\":\"" + status + "\",\"amount\":1250,\"currency\":\"gbp\",\"payment_intent\":\"pi_123\",\"reason\":\"fraudulent\",\"evidence_details\":{\"due_by\":1893456000}}}}";

        var opened = Assert.Single(await Gateway(stripe).ReadWebhookAsync(Signed(Dispute("charge.dispute.created", "needs_response"))));
        Assert.Equal((GatewayEventKind.DisputeOpened, order, 12.50m, "GBP", "dp_1", "fraudulent"), (opened.Kind, opened.OrderId, opened.Amount, opened.Currency, opened.Reference, opened.Detail));
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1893456000), opened.RespondBy);

        Assert.Equal(GatewayEventKind.DisputeWon, Assert.Single(await Gateway(stripe).ReadWebhookAsync(Signed(Dispute("charge.dispute.closed", "won")))).Kind);
        Assert.Equal(GatewayEventKind.DisputeWon, Assert.Single(await Gateway(stripe).ReadWebhookAsync(Signed(Dispute("charge.dispute.closed", "warning_closed")))).Kind);
        Assert.Equal(GatewayEventKind.DisputeLost, Assert.Single(await Gateway(stripe).ReadWebhookAsync(Signed(Dispute("charge.dispute.closed", "lost")))).Kind);
        Assert.Empty(await Gateway(stripe).ReadWebhookAsync(Signed(Dispute("charge.dispute.closed", "needs_response")))); // closed in no known way

        static string Refund(string type, string status) =>
            "{\"type\":\"" + type + "\",\"data\":{\"object\":{\"id\":\"re_9\",\"status\":\"" + status + "\",\"amount\":200,\"currency\":\"gbp\",\"payment_intent\":\"pi_123\"}}}";
        var failed = Assert.Single(await Gateway(stripe).ReadWebhookAsync(Signed(Refund("refund.failed", "failed"))));
        Assert.Equal((GatewayEventKind.RefundFailed, order, 2.00m, "re_9"), (failed.Kind, failed.OrderId, failed.Amount, failed.Reference));
        Assert.Equal(GatewayEventKind.RefundFailed, Assert.Single(await Gateway(stripe).ReadWebhookAsync(Signed(Refund("refund.updated", "failed")))).Kind);
    }

    [Fact]
    public async Task Forged_stale_missing_and_malformed_signatures_are_all_rejected()
    {
        var json = Completed(Guid.NewGuid());
        await Assert.ThrowsAsync<GatewayWebhookRejectedException>(() => Gateway().ReadWebhookAsync(Signed(json, secret: "whsec_someone_else")));
        await Assert.ThrowsAsync<GatewayWebhookRejectedException>(() => Gateway().ReadWebhookAsync(Signed(json, at: Now.AddMinutes(-10)))); // a captured webhook replayed later
        await Assert.ThrowsAsync<GatewayWebhookRejectedException>(() => Gateway().ReadWebhookAsync(Signed(json, at: Now.AddMinutes(10))));
        await Assert.ThrowsAsync<GatewayWebhookRejectedException>(() => Gateway(tweak: o => o.WebhookSecret = null).ReadWebhookAsync(Signed(json)));
        await Assert.ThrowsAsync<GatewayWebhookRejectedException>(() => Gateway().ReadWebhookAsync(new GatewayWebhookRequest(new Dictionary<string, string>(), Encoding.UTF8.GetBytes(json))));
        foreach (var header in new[] { "", "garbage", "t=abc,v1=00", $"t={Now.ToUnixTimeSeconds()}", $"t={Now.ToUnixTimeSeconds()},v1=zz", "v1=00" })
        {
            var request = new GatewayWebhookRequest(new Dictionary<string, string> { ["Stripe-Signature"] = header }, Encoding.UTF8.GetBytes(json));
            await Assert.ThrowsAsync<GatewayWebhookRejectedException>(() => Gateway().ReadWebhookAsync(request));
        }

        // Changing one byte of a correctly signed body breaks it.
        var signed = Signed(json);
        var tampered = new GatewayWebhookRequest(signed.Headers, Encoding.UTF8.GetBytes(json.Replace("1250", "9999")));
        await Assert.ThrowsAsync<GatewayWebhookRejectedException>(() => Gateway().ReadWebhookAsync(tampered));

        // A correctly signed webhook with several v1 entries (a secret being rolled) is accepted when any one matches.
        var withExtra = new GatewayWebhookRequest(
            new Dictionary<string, string> { ["Stripe-Signature"] = signed.Headers["stripe-signature"] + ",v1=" + new string('0', 64) }, signed.Body);
        Assert.Single(await Gateway().ReadWebhookAsync(withExtra));
    }

    [Fact]
    public async Task Refunding_posts_the_exact_amount_for_the_payment_with_an_idempotency_key_and_reads_the_refund_back()
    {
        var order = Guid.NewGuid();
        var stripe = new FakeStripe(_ => (HttpStatusCode.OK, "{\"id\":\"re_77\",\"status\":\"succeeded\",\"amount\":450,\"currency\":\"gbp\"}"));
        var refund = await Gateway(stripe).RefundAsync(new GatewayRefundRequest(order, "pi_123", 4.50m, "GBP", "key-1"));

        Assert.Equal(("re_77", true, 4.50m, "GBP"), (refund.Reference, refund.Succeeded, refund.Amount, refund.Currency));
        var (request, body) = Assert.Single(stripe.Calls);
        Assert.Equal("https://api.stripe.com/v1/refunds", request.RequestUri!.ToString());
        Assert.Equal("Bearer sk_test_x", request.Headers.Authorization!.ToString());
        Assert.Equal("refund-key-1", request.Headers.GetValues("Idempotency-Key").Single());
        var form = Form(body);
        Assert.Equal("pi_123", form["payment_intent"]);
        Assert.Equal("450", form["amount"]);
        Assert.Equal(order.ToString(), form["metadata[order_id]"]);
    }

    [Fact]
    public async Task A_refund_stripe_has_not_finished_is_not_reported_as_succeeded_and_a_stripe_error_is_a_gateway_error()
    {
        var pending = new FakeStripe(_ => (HttpStatusCode.OK, "{\"id\":\"re_78\",\"status\":\"pending\",\"amount\":100,\"currency\":\"gbp\"}"));
        Assert.False((await Gateway(pending).RefundAsync(new GatewayRefundRequest(Guid.NewGuid(), "pi_1", 1m, "GBP", "k"))).Succeeded);

        var refused = new FakeStripe(_ => (HttpStatusCode.BadRequest, "{\"error\":{\"message\":\"Charge already refunded\"}}"));
        var ex = await Assert.ThrowsAsync<GatewayException>(() => Gateway(refused).RefundAsync(new GatewayRefundRequest(Guid.NewGuid(), "pi_1", 1m, "GBP", "k")));
        Assert.DoesNotContain("already refunded", ex.Message); // Stripe's wording goes to the log, not the caller

        var noId = new FakeStripe(_ => (HttpStatusCode.OK, "{\"status\":\"succeeded\"}"));
        await Assert.ThrowsAsync<GatewayException>(() => Gateway(noId).RefundAsync(new GatewayRefundRequest(Guid.NewGuid(), "pi_1", 1m, "GBP", "k")));
    }
}
