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
public sealed class TestGateway : IPaymentGateway
{
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
            [new GatewayEvent(e.Type == "refund" ? GatewayEventKind.RefundConfirmed : GatewayEventKind.PaymentConfirmed, e.OrderId, e.Amount, e.Currency, e.Reference)];
        return Task.FromResult(events);
    }

    public static string Sign(byte[] body) => Convert.ToHexString(HMACSHA256.HashData(Encoding.UTF8.GetBytes(Secret), body));
    public sealed record WireEvent(string Type, Guid OrderId, decimal Amount, string Currency, string Reference);
}

public class GatewayTests(PiranhaFactory factory) : IClassFixture<PiranhaFactory>
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
}
