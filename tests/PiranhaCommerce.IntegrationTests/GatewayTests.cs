using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using PiranhaCommerce.Catalog;
using PiranhaCommerce.Customers;
using PiranhaCommerce.Ecommerce;

namespace PiranhaCommerce.IntegrationTests;

/// <summary>A stand-in gateway: signs webhooks with an HMAC like real ones do, so the host's handling can be tested end to end.</summary>
public sealed class TestGateway : IRefundingGateway, IDisputeEvidenceGateway
{
    /// <summary>Evidence sent through this gateway: (dispute reference, evidence). A reference starting dp_refuse is refused.</summary>
    public static readonly System.Collections.Concurrent.ConcurrentQueue<(string Reference, DisputeEvidence Evidence)> Evidence = new();

    public Task SubmitEvidenceAsync(string reference, DisputeEvidence evidence, CancellationToken ct = default)
    {
        if (reference.StartsWith("dp_refuse", StringComparison.Ordinal)) throw new GatewayException("testpay refused the evidence");
        Evidence.Enqueue((reference, evidence));
        return Task.CompletedTask;
    }

    public const string Secret = "test-gateway-secret";
    public string Name => "testpay";
    public string DisplayName => "Test gateway";

    public Task<GatewaySession> StartAsync(GatewayStartRequest request, CancellationToken ct = default)
        => Task.FromResult(new GatewaySession($"https://pay.example/session/{request.OrderId:N}?amount={request.Amount:0.00}", "sess_" + request.OrderId.ToString("N")[..8]));

    public Task<IReadOnlyList<GatewayEvent>> ReadWebhookAsync(GatewayWebhookRequest request, CancellationToken ct = default)
    {
        if (!request.Headers.TryGetValue("X-Test-Signature", out var signature) || !CryptographicOperations.FixedTimeEquals(
                Encoding.ASCII.GetBytes(signature), Encoding.ASCII.GetBytes(Sign(request.Body))))
            throw new GatewayWebhookRejectedException("bad signature");
        var e = JsonSerializer.Deserialize<WireEvent>(request.Body, new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
        IReadOnlyList<GatewayEvent> events = e.Type == "ignored" ? [] :
            [new GatewayEvent(e.Type switch
            {
                "refund" => GatewayEventKind.RefundConfirmed,
                "dispute-opened" => GatewayEventKind.DisputeOpened,
                "dispute-won" => GatewayEventKind.DisputeWon,
                "dispute-lost" => GatewayEventKind.DisputeLost,
                "refund-failed" => GatewayEventKind.RefundFailed,
                _ => GatewayEventKind.PaymentConfirmed,
            }, e.OrderId, e.Amount, e.Currency, e.Reference, e.Detail, e.Type == "dispute-opened" ? new DateTimeOffset(2030, 1, 15, 0, 0, 0, TimeSpan.Zero) : null, e.Fee)];
        return Task.FromResult(events);
    }

    /// <summary>A refund of 0.07 stays pending, 0.13 is refused; anything else succeeds. The refund id comes from the idempotency key, so a repeat is the same refund.</summary>
    public Task<GatewayRefund> RefundAsync(GatewayRefundRequest request, CancellationToken ct = default)
    {
        if (request.Amount == 0.13m) throw new GatewayException("testpay refused the refund (secret internal detail)");
        var id = "re_" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(request.IdempotencyKey)))[..12];
        var pending = request.Amount == 0.07m;
        return Task.FromResult(new GatewayRefund(id, pending ? "pending" : "succeeded", !pending, request.Amount, request.Currency));
    }

    public static string Sign(byte[] body) => Convert.ToHexString(HMACSHA256.HashData(Encoding.UTF8.GetBytes(Secret), body));
    public sealed record WireEvent(string Type, Guid OrderId, decimal Amount, string Currency, string Reference, string? Detail = null, decimal? Fee = null);
}

[Collection(OrderSettings.Name)]
public class GatewayTests(PiranhaCommerceFactory factory) : IClassFixture<PiranhaCommerceFactory>
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };
    private static string Tag() => Guid.NewGuid().ToString("N")[..8];
    private static async Task<T> Read<T>(HttpResponseMessage r) => (await r.Content.ReadFromJsonAsync<T>(Json))!;

    private static async Task<OrderDto> PlaceOrder(HttpClient staff, decimal price = 10m)
    {
        var p = await Read<ProductDto>(await staff.PostAsJsonAsync("/api/v1/products", new ProductInput { Name = "G " + Tag(), Sku = "G-" + Tag(), Price = price }));
        await staff.PutAsJsonAsync($"/api/v1/products/{p.Id}/listing", new ProductListingInput { Published = true });
        await staff.PostAsJsonAsync($"/api/v1/products/{p.Id}/stock/adjustments", new StockAdjustmentInput { Delta = 10, Reason = "Delivery" });
        var b = await Read<BasketDto>(await staff.PostAsJsonAsync("/api/v1/baskets", new BasketCreateInput()));
        await staff.PutAsJsonAsync($"/api/v1/baskets/{b.Id}/lines/{p.Id}", new BasketLineInput { Quantity = 1 });
        return await Read<OrderDto>(await staff.PostAsJsonAsync($"/api/v1/baskets/{b.Id}/checkout", new CheckoutInput
        {
            Email = $"g-{Tag()}@example.com",
            ShippingAddress = new AddressInput { Recipient = "Ada", Line1 = "1 High St", City = "Leeds", PostalCode = "LS1", CountryCode = "GB" },
        }));
    }

    private HttpClient Anonymous() => factory.CreateClient();

    private static async Task<HttpResponseMessage> Hook(HttpClient c, TestGateway.WireEvent e, string? signature = null, string provider = "testpay")
    {
        var body = JsonSerializer.SerializeToUtf8Bytes(e, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        var request = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/payments/webhooks/{provider}") { Content = new ByteArrayContent(body) };
        request.Content.Headers.ContentType = new("application/json");
        request.Headers.Add("X-Test-Signature", signature ?? TestGateway.Sign(body));
        return await c.SendAsync(request);
    }

    [Fact]
    public async Task Starting_a_payment_asks_the_gateway_for_exactly_what_is_still_owed()
    {
        var staff = await factory.CreateAuthenticatedApiClientAsync();
        var order = await PlaceOrder(staff);
        await staff.PostAsJsonAsync($"/api/v1/orders/{order.Id}/payments", new PaymentInput { Amount = 3m });

        var r = await staff.PostAsJsonAsync($"/api/v1/orders/{order.Id}/payments/start", new StartPaymentInput { Provider = "testpay" });
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        var session = await Read<PaymentSessionDto>(r);
        Assert.Equal(order.Total - 3m, session.Amount);
        Assert.Contains(order.Id.ToString("N"), session.RedirectUrl);

        Assert.Equal(HttpStatusCode.BadRequest, (await staff.PostAsJsonAsync($"/api/v1/orders/{order.Id}/payments/start", new StartPaymentInput { Provider = "manual" })).StatusCode);
        var providers = await staff.GetFromJsonAsync<List<PaymentProviderDto>>("/api/v1/payment-providers", Json);
        Assert.True(providers!.Single(p => p.Name == "testpay").IsGateway);
        Assert.False(providers!.Single(p => p.Name == "manual").IsGateway);
    }

    [Fact]
    public async Task A_signed_webhook_records_the_payment_once_however_many_times_it_is_replayed()
    {
        var staff = await factory.CreateAuthenticatedApiClientAsync();
        var order = await PlaceOrder(staff);
        var evt = new TestGateway.WireEvent("payment", order.Id, order.Total, order.Currency, "ch_" + Tag());

        var first = await Read<WebhookResultDto>(await Hook(Anonymous(), evt));
        Assert.Equal((1, 0, 0), (first.Recorded, first.AlreadyRecorded, first.Unreconciled));
        var replay = await Read<WebhookResultDto>(await Hook(Anonymous(), evt));
        Assert.Equal((0, 1, 0), (replay.Recorded, replay.AlreadyRecorded, replay.Unreconciled));

        var simultaneous = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => Hook(Anonymous(), evt)));
        Assert.All(simultaneous, r => Assert.Equal(HttpStatusCode.OK, r.StatusCode));

        var payments = await staff.GetFromJsonAsync<List<PaymentDto>>($"/api/v1/orders/{order.Id}/payments", Json);
        var recorded = Assert.Single(payments!);
        Assert.Equal(("testpay", order.Total, "gateway:testpay"), (recorded.Provider, recorded.Amount, recorded.Actor));
        Assert.Equal(PaymentStatus.Paid, (await Read<OrderDto>(await staff.GetAsync($"/api/v1/orders/{order.Id}"))).PaymentStatus);
    }

    [Fact]
    public async Task A_webhook_with_a_wrong_or_missing_signature_or_for_an_unknown_gateway_records_nothing()
    {
        var staff = await factory.CreateAuthenticatedApiClientAsync();
        var order = await PlaceOrder(staff);
        var evt = new TestGateway.WireEvent("payment", order.Id, order.Total, order.Currency, "ch_" + Tag());

        Assert.Equal(HttpStatusCode.BadRequest, (await Hook(Anonymous(), evt, signature: "00")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await Hook(Anonymous(), evt, signature: new string('0', 64))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await Hook(Anonymous(), evt, provider: "nosuchgateway")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await Hook(Anonymous(), evt, provider: "manual")).StatusCode);
        Assert.Empty((await staff.GetFromJsonAsync<List<PaymentDto>>($"/api/v1/orders/{order.Id}/payments", Json))!);
    }

    [Fact]
    public async Task Money_that_cannot_be_booked_is_counted_as_unreconciled_not_retried_and_never_over_pays_the_order()
    {
        var staff = await factory.CreateAuthenticatedApiClientAsync();
        var order = await PlaceOrder(staff);

        var over = await Read<WebhookResultDto>(await Hook(Anonymous(), new("payment", order.Id, order.Total + 5m, order.Currency, "ch_" + Tag())));
        Assert.Equal(1, over.Unreconciled);
        var wrongCurrency = await Read<WebhookResultDto>(await Hook(Anonymous(), new("payment", order.Id, order.Total, "USD", "ch_" + Tag())));
        Assert.Equal(1, wrongCurrency.Unreconciled);
        var unknownOrder = await Read<WebhookResultDto>(await Hook(Anonymous(), new("payment", Guid.NewGuid(), 5m, "GBP", "ch_" + Tag())));
        Assert.Equal(1, unknownOrder.Unreconciled);
        var ignored = await Read<WebhookResultDto>(await Hook(Anonymous(), new("ignored", order.Id, 0m, "GBP", "x")));
        Assert.Equal((0, 0, 0), (ignored.Recorded, ignored.AlreadyRecorded, ignored.Unreconciled));

        Assert.Empty((await staff.GetFromJsonAsync<List<PaymentDto>>($"/api/v1/orders/{order.Id}/payments", Json))!);
    }

    [Fact]
    public async Task A_confirmed_refund_is_recorded_and_a_cancelled_order_cannot_be_started_for_payment()
    {
        var staff = await factory.CreateAuthenticatedApiClientAsync();
        var order = await PlaceOrder(staff);
        await Hook(Anonymous(), new("payment", order.Id, order.Total, order.Currency, "ch_" + Tag()));
        var refund = await Read<WebhookResultDto>(await Hook(Anonymous(), new("refund", order.Id, 2m, order.Currency, "re_" + Tag())));
        Assert.Equal(1, refund.Recorded);
        Assert.Equal(2m, (await Read<OrderDto>(await staff.GetAsync($"/api/v1/orders/{order.Id}"))).AmountRefunded);

        var other = await PlaceOrder(staff);
        await staff.PostAsJsonAsync($"/api/v1/orders/{other.Id}/stage", new OrderStageInput { Stage = OrderStage.Cancelled }, Json);
        Assert.Equal(HttpStatusCode.Conflict, (await staff.PostAsJsonAsync($"/api/v1/orders/{other.Id}/payments/start", new StartPaymentInput { Provider = "testpay" })).StatusCode);
    }

    [Fact]
    public async Task The_webhook_refuses_an_oversized_body_and_a_shopper_can_only_start_payment_on_their_own_order()
    {
        var big = new HttpRequestMessage(HttpMethod.Post, "/api/v1/payments/webhooks/testpay") { Content = new ByteArrayContent(new byte[300 * 1024]) };
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, (await Anonymous().SendAsync(big)).StatusCode);

        var staff = await factory.CreateAuthenticatedApiClientAsync();
        var order = await PlaceOrder(staff); // a staff-made order has no owner
        var email = $"gw-{Tag()}@example.com";
        var shopper = factory.CreateClient();
        var reg = await shopper.PostAsJsonAsync("/api/v1/storefront/auth/register", new RegisterInput { Email = email, Password = "a-long-enough-password" });
        var registered = (await reg.Content.ReadFromJsonAsync<RegisteredDto>())!;
        shopper.DefaultRequestHeaders.Authorization = new("Bearer", registered.Token.AccessToken);

        Assert.Equal(HttpStatusCode.NotFound, (await shopper.PostAsJsonAsync($"/api/v1/storefront/orders/{order.Id}/pay", new StartPaymentInput { Provider = "testpay" })).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await Anonymous().PostAsJsonAsync($"/api/v1/storefront/orders/{order.Id}/pay", new StartPaymentInput { Provider = "testpay" })).StatusCode);
    }

    private static async Task<UnreconciledPaymentDto?> Listed(HttpClient staff, string reference, bool? resolved = null)
    {
        var qs = resolved is null ? "" : $"&resolved={resolved.ToString()!.ToLowerInvariant()}";
        var page = await Read<PiranhaCommerce.Core.Paging.PagedResult<UnreconciledPaymentDto>>(await staff.GetAsync($"/api/v1/unreconciled-payments?pageSize=100{qs}"));
        return page.Items.SingleOrDefault(u => u.Reference == reference);
    }

    [Fact]
    public async Task Money_that_cannot_be_booked_goes_on_the_list_once_however_often_the_gateway_repeats_it()
    {
        var staff = await factory.CreateAuthenticatedApiClientAsync();
        var order = await PlaceOrder(staff);
        var reference = "ch_" + Tag();
        var over = new TestGateway.WireEvent("payment", order.Id, order.Total + 5m, order.Currency, reference);
        var unknown = new TestGateway.WireEvent("payment", Guid.NewGuid(), 5m, "GBP", "ch_" + Tag());

        for (var i = 0; i < 3; i++) Assert.Equal(1, (await Read<WebhookResultDto>(await Hook(Anonymous(), over))).Unreconciled);
        await Hook(Anonymous(), unknown);

        var item = await Listed(staff, reference, resolved: false);
        Assert.NotNull(item);
        Assert.Equal((order.Reference, order.Total + 5m, "testpay", GatewayEventKind.PaymentConfirmed), (item!.OrderReference, item.Amount, item.Provider, item.Kind));
        Assert.Contains("more than is owed", item.Reason);
        Assert.Null(item.ResolvedUtc);

        var other = await Listed(staff, unknown.Reference, resolved: false);
        Assert.Null(other!.OrderReference); // an order that does not exist is shown as such
        Assert.True((await Read<JsonElement>(await staff.GetAsync("/api/v1/unreconciled-payments/open-count"))).GetProperty("open").GetInt32() >= 2);
    }

    [Fact]
    public async Task A_retry_books_the_money_once_the_order_can_take_it_and_closes_the_item_and_until_then_says_why_not()
    {
        var staff = await factory.CreateAuthenticatedApiClientAsync();
        var order = await PlaceOrder(staff);
        Assert.Equal(HttpStatusCode.Created, (await staff.PostAsJsonAsync($"/api/v1/orders/{order.Id}/payments", new PaymentInput { Amount = order.Total })).StatusCode);
        var reference = "ch_" + Tag();
        await Hook(Anonymous(), new("payment", order.Id, order.Total, order.Currency, reference)); // paid twice: the second cannot be booked
        var item = (await Listed(staff, reference, resolved: false))!;

        var still = await staff.PostAsync($"/api/v1/unreconciled-payments/{item.Id}/retry", null);
        Assert.Equal(HttpStatusCode.Conflict, still.StatusCode);
        Assert.Contains("Still cannot be booked", await still.Content.ReadAsStringAsync());
        Assert.Null((await Listed(staff, reference, resolved: false))!.ResolvedUtc);

        // The manual payment is refunded, so the order is owed money again and the gateway's payment now fits.
        Assert.Equal(HttpStatusCode.Created, (await staff.PostAsJsonAsync($"/api/v1/orders/{order.Id}/refunds", new PaymentInput { Amount = order.Total })).StatusCode);
        var retried = await staff.PostAsync($"/api/v1/unreconciled-payments/{item.Id}/retry", null);
        Assert.Equal(HttpStatusCode.OK, retried.StatusCode);
        Assert.NotNull((await Read<UnreconciledPaymentDto>(retried)).ResolvedUtc);

        var payments = await staff.GetFromJsonAsync<List<PaymentDto>>($"/api/v1/orders/{order.Id}/payments", Json);
        Assert.Contains(payments!, p => p.Reference == reference && p.Kind == PaymentKind.Payment);
        Assert.Equal(HttpStatusCode.Conflict, (await staff.PostAsync($"/api/v1/unreconciled-payments/{item.Id}/retry", null)).StatusCode);
    }

    [Fact]
    public async Task Resolving_needs_a_note_works_once_and_a_replay_afterwards_does_not_bring_it_back()
    {
        var staff = await factory.CreateAuthenticatedApiClientAsync();
        var order = await PlaceOrder(staff);
        var reference = "ch_" + Tag();
        var evt = new TestGateway.WireEvent("payment", order.Id, order.Total + 1m, order.Currency, reference);
        await Hook(Anonymous(), evt);
        var item = (await Listed(staff, reference, resolved: false))!;

        Assert.Equal(HttpStatusCode.BadRequest, (await staff.PostAsJsonAsync($"/api/v1/unreconciled-payments/{item.Id}/resolve", new ResolveUnreconciledInput { Resolution = "  " })).StatusCode);
        var done = await staff.PostAsJsonAsync($"/api/v1/unreconciled-payments/{item.Id}/resolve", new ResolveUnreconciledInput { Resolution = "Refunded in the gateway dashboard" });
        Assert.Equal(HttpStatusCode.OK, done.StatusCode);
        var resolved = await Read<UnreconciledPaymentDto>(done);
        Assert.Equal(("Refunded in the gateway dashboard", "admin@piranhacommerce.local"), (resolved.Resolution, resolved.ResolvedBy));
        Assert.Equal(HttpStatusCode.Conflict, (await staff.PostAsJsonAsync($"/api/v1/unreconciled-payments/{item.Id}/resolve", new ResolveUnreconciledInput { Resolution = "again" })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await staff.PostAsJsonAsync($"/api/v1/unreconciled-payments/{Guid.NewGuid()}/resolve", new ResolveUnreconciledInput { Resolution = "x" })).StatusCode);

        Assert.Null(await Listed(staff, reference, resolved: false));
        Assert.NotNull(await Listed(staff, reference, resolved: true));
        var replay = await Read<WebhookResultDto>(await Hook(Anonymous(), evt));
        Assert.Equal((0, 1, 0), (replay.Recorded, replay.AlreadyRecorded, replay.Unreconciled));
        Assert.Null(await Listed(staff, reference, resolved: false));
    }

    [Fact]
    public async Task A_read_only_user_can_see_the_list_but_not_change_it()
    {
        var staff = await factory.CreateAuthenticatedApiClientAsync();
        var order = await PlaceOrder(staff);
        var reference = "ch_" + Tag();
        await Hook(Anonymous(), new("payment", order.Id, order.Total + 1m, order.Currency, reference));
        var item = (await Listed(staff, reference, resolved: false))!;

        var (email, password) = await factory.CreateReadOnlyUserAsync();
        var reader = await factory.CreateAuthenticatedApiClientAsync(email, password);
        Assert.Equal(HttpStatusCode.OK, (await reader.GetAsync("/api/v1/unreconciled-payments")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await reader.PostAsync($"/api/v1/unreconciled-payments/{item.Id}/retry", null)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await reader.PostAsJsonAsync($"/api/v1/unreconciled-payments/{item.Id}/resolve", new ResolveUnreconciledInput { Resolution = "x" })).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await Anonymous().GetAsync("/api/v1/unreconciled-payments")).StatusCode);
    }
}
