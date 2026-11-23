using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using PiranhaCommerce.Catalog;
using PiranhaCommerce.Core.Paging;
using PiranhaCommerce.Customers;
using PiranhaCommerce.Data;
using PiranhaCommerce.Ecommerce;
using PiranhaCommerce.Webhooks;

namespace PiranhaCommerce.IntegrationTests;

/// <summary>
/// Outbound webhooks. The background sender is switched off in the test host; the tests run the sender themselves against a fake receiver, with a clock they move,
/// so nothing sleeps and nothing leaves the machine.
/// </summary>
[Collection(OrderSettings.Name)]
public class WebhookTests(PiranhaFactory factory) : IClassFixture<PiranhaFactory>, IAsyncLifetime
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };
    private static string Tag() => Guid.NewGuid().ToString("N")[..8];
    private static async Task<T> Read<T>(HttpResponseMessage r) { Assert.True(r.IsSuccessStatusCode, r.StatusCode + " " + await r.Content.ReadAsStringAsync()); return (await r.Content.ReadFromJsonAsync<T>(Json))!; }

    private sealed record Call(string Url, string Body, Dictionary<string, string> Headers)
    {
        public JsonElement Message => JsonDocument.Parse(Body).RootElement;
        public string Type => Message.GetProperty("type").GetString()!;
        public JsonElement Data => Message.GetProperty("data");
    }

    private sealed class Receiver(Func<Call, HttpStatusCode>? reply = null) : HttpMessageHandler, IHttpClientFactory
    {
        public List<Call> Calls { get; } = [];
        public Func<Call, HttpStatusCode> Reply { get; set; } = reply ?? (_ => HttpStatusCode.OK);
        public IEnumerable<Call> To(string url) { lock (Calls) return Calls.Where(c => c.Url == url).ToList(); }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var call = new Call(request.RequestUri!.ToString(), await request.Content!.ReadAsStringAsync(ct), request.Headers.ToDictionary(h => h.Key, h => string.Join(",", h.Value)));
            lock (Calls) Calls.Add(call);
            return new HttpResponseMessage(Reply(call)) { Content = new StringContent("received", Encoding.UTF8, "text/plain") };
        }

        public HttpClient CreateClient(string name) => new(this, disposeHandler: false);
    }

    private sealed class Clock : TimeProvider
    {
        private DateTimeOffset _now = DateTimeOffset.UtcNow;
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan by) => _now += by;
    }

    private HttpClient _c = default!;
    private readonly List<Guid> _endpoints = [];

    public async Task InitializeAsync() => _c = await factory.CreateAuthenticatedApiClientAsync();

    public async Task DisposeAsync() { foreach (var id in _endpoints) await _c.DeleteAsync($"/api/v1/webhooks/{id}"); }

    private async Task<WebhookEndpointDto> Endpoint(params string[] types)
    {
        var r = await _c.PostAsJsonAsync("/api/v1/webhooks", new WebhookEndpointInput { Url = $"https://hooks.example.com/{Tag()}", EventTypes = [.. types] }, Json);
        Assert.Equal(HttpStatusCode.Created, r.StatusCode);
        var e = await Read<WebhookEndpointDto>(r);
        _endpoints.Add(e.Id);
        return e;
    }

    private async Task<DispatchResult> Dispatch(Receiver receiver, Clock clock, int maxAttempts = 8)
    {
        using var scope = factory.Services.CreateScope();
        var d = new WebhookDispatcher(scope.ServiceProvider.GetRequiredService<PiranhaDbContext>(), receiver, Options.Create(new WebhookOptions { MaxAttempts = maxAttempts }), NullLogger<WebhookDispatcher>.Instance, clock);
        return await d.DispatchDueAsync();
    }

    private async Task<OrderDto> Order(int stock = 50, int quantity = 1)
    {
        var p = await Read<ProductDto>(await _c.PostAsJsonAsync("/api/v1/products", new ProductInput { Name = "Hook " + Tag(), Sku = "WH-" + Tag(), Price = 12.5m }));
        await _c.PutAsJsonAsync($"/api/v1/products/{p.Id}/listing", new ProductListingInput { Published = true });
        await _c.PostAsJsonAsync($"/api/v1/products/{p.Id}/stock/adjustments", new StockAdjustmentInput { Delta = stock, Reason = "Delivery" });
        return await Place(p, quantity);
    }

    private async Task<HttpResponseMessage> TryPlace(ProductDto p, int quantity)
    {
        var basket = await Read<BasketDto>(await _c.PostAsJsonAsync("/api/v1/baskets", new BasketCreateInput()));
        await _c.PutAsJsonAsync($"/api/v1/baskets/{basket.Id}/lines/{p.Id}", new BasketLineInput { Quantity = quantity });
        return await _c.PostAsJsonAsync($"/api/v1/baskets/{basket.Id}/checkout", new CheckoutInput
        {
            Email = $"hook-{Tag()}@example.com", ShippingAddress = new AddressInput { Line1 = "1 High St", City = "Leeds", CountryCode = "GB" },
        });
    }

    private async Task<OrderDto> Place(ProductDto p, int quantity) => await Read<OrderDto>(await TryPlace(p, quantity));

    private static bool SignatureIsValid(Call call, string secret)
    {
        var parts = call.Headers["Piranha-Signature"].Split(',').Select(x => x.Split('=', 2)).ToDictionary(x => x[0], x => x[1]);
        var expected = Convert.ToHexString(HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), Encoding.UTF8.GetBytes($"{parts["t"]}.{call.Body}"))).ToLowerInvariant();
        return parts["v1"] == expected;
    }

    private async Task<WebhookDeliveryDto> OnlyDelivery(WebhookEndpointDto e) =>
        Assert.Single((await Read<PagedResult<WebhookDeliveryDto>>(await _c.GetAsync($"/api/v1/webhooks/{e.Id}/deliveries"))).Items);

    [Fact]
    public async Task An_order_placed_is_sent_signed_with_ids_and_figures_but_no_personal_details()
    {
        var endpoint = await Endpoint();
        var receiver = new Receiver();
        var order = await Order(quantity: 2);

        var result = await Dispatch(receiver, new Clock());
        Assert.True(result.FannedOut >= 1);

        var call = Assert.Single(receiver.To(endpoint.Url));
        Assert.Equal("order.placed", call.Type);
        Assert.Equal(order.Id, call.Data.GetProperty("orderId").GetGuid());
        Assert.Equal(order.Reference, call.Data.GetProperty("reference").GetString());
        Assert.Equal(25m, call.Data.GetProperty("total").GetDecimal());
        Assert.Equal(1, call.Message.GetProperty("apiVersion").GetInt32());
        Assert.Equal(call.Message.GetProperty("id").GetString(), call.Headers["Piranha-Event-Id"]);
        Assert.Equal("order.placed", call.Headers["Piranha-Event-Type"]);
        Assert.Equal("1", call.Headers["Piranha-Delivery-Attempt"]);
        Assert.True(SignatureIsValid(call, endpoint.Secret!));
        Assert.False(SignatureIsValid(call, "whsec_wrong"));
        Assert.DoesNotContain("@example.com", call.Body);
        Assert.DoesNotContain("High St", call.Body);

        var delivery = await OnlyDelivery(endpoint);
        Assert.Equal((WebhookDeliveryStatus.Succeeded, 1, 200), (delivery.Status, delivery.Attempts, delivery.LastStatusCode));
    }

    [Fact]
    public async Task Endpoints_only_get_the_events_they_asked_for_and_only_those_that_happened_after_they_were_added()
    {
        var placed = await Endpoint("order.placed");
        var money = await Endpoint("payment.received", "payment.refunded");
        var order = await Order();
        Assert.True((await _c.PostAsJsonAsync($"/api/v1/orders/{order.Id}/payments", new PaymentInput { Amount = 12.5m }, Json)).IsSuccessStatusCode);
        Assert.True((await _c.PostAsJsonAsync($"/api/v1/orders/{order.Id}/refunds", new PaymentInput { Amount = 2m }, Json)).IsSuccessStatusCode);
        var late = await Endpoint(); // added after all of that: it must not be sent a backlog

        var receiver = new Receiver();
        await Dispatch(receiver, new Clock());

        Assert.Equal(["order.placed"], receiver.To(placed.Url).Select(c => c.Type));
        var payments = receiver.To(money.Url).ToList();
        Assert.Equal(["payment.received", "payment.refunded"], payments.Select(c => c.Type).Order());
        var received = payments.Single(c => c.Type == "payment.received").Data;
        Assert.Equal((order.Id, 12.5m, "Payment"), (received.GetProperty("orderId").GetGuid(), received.GetProperty("amount").GetDecimal(), received.GetProperty("kind").GetString()));
        Assert.Equal(2m, payments.Single(c => c.Type == "payment.refunded").Data.GetProperty("amount").GetDecimal());
        Assert.Empty(receiver.To(late.Url));
    }

    [Fact]
    public async Task A_change_of_stage_is_an_event_with_where_it_came_from()
    {
        var endpoint = await Endpoint("order.stage_changed");
        var order = await Order();
        Assert.True((await _c.PostAsJsonAsync($"/api/v1/orders/{order.Id}/stage", new OrderStageInput { Stage = OrderStage.Cancelled }, Json)).IsSuccessStatusCode);

        var receiver = new Receiver();
        await Dispatch(receiver, new Clock());

        var data = Assert.Single(receiver.To(endpoint.Url)).Data;
        Assert.Equal((order.Id, "Placed", "Cancelled"), (data.GetProperty("orderId").GetGuid(), data.GetProperty("previousStage").GetString(), data.GetProperty("stage").GetString()));
    }

    [Fact]
    public async Task A_failed_delivery_is_tried_again_after_a_wait_then_given_up_and_can_be_sent_again_by_hand()
    {
        var endpoint = await Endpoint("order.placed");
        await Order();
        var clock = new Clock();
        var receiver = new Receiver(_ => HttpStatusCode.InternalServerError);

        var first = await Dispatch(receiver, clock, maxAttempts: 3);
        Assert.Equal((1, 0), (first.Retrying, first.Delivered));
        var delivery = await OnlyDelivery(endpoint);
        Assert.Equal((WebhookDeliveryStatus.Pending, 1, 500), (delivery.Status, delivery.Attempts, delivery.LastStatusCode));
        Assert.Contains("500", delivery.LastError);

        // Not due yet: nothing is sent.
        var sentSoFar = receiver.To(endpoint.Url).Count();
        await Dispatch(receiver, clock, maxAttempts: 3);
        Assert.Equal(sentSoFar, receiver.To(endpoint.Url).Count());

        // After the wait it goes again, and says it is the second attempt.
        clock.Advance(TimeSpan.FromMinutes(2));
        await Dispatch(receiver, clock, maxAttempts: 3);
        Assert.Equal("2", receiver.To(endpoint.Url).Last().Headers["Piranha-Delivery-Attempt"]);

        clock.Advance(TimeSpan.FromMinutes(10));
        var last = await Dispatch(receiver, clock, maxAttempts: 3);
        Assert.Equal(1, last.GaveUp);
        var failed = await OnlyDelivery(endpoint);
        Assert.Equal((WebhookDeliveryStatus.Failed, 3), (failed.Status, failed.Attempts));
        Assert.NotNull(failed.CompletedUtc);

        // Sent again by hand once the receiver is back: a full set of attempts, and it succeeds.
        var again = await Read<WebhookDeliveryDto>(await _c.PostAsync($"/api/v1/webhook-deliveries/{failed.Id}/redeliver", null));
        Assert.Equal((WebhookDeliveryStatus.Pending, 0), (again.Status, again.Attempts));
        receiver.Reply = _ => HttpStatusCode.NoContent;
        await Dispatch(receiver, clock);
        Assert.Equal((WebhookDeliveryStatus.Succeeded, 1), ((await OnlyDelivery(endpoint)).Status, (await OnlyDelivery(endpoint)).Attempts));
        Assert.Equal(HttpStatusCode.NotFound, (await _c.PostAsync($"/api/v1/webhook-deliveries/{Guid.NewGuid()}/redeliver", null)).StatusCode);
    }

    [Fact]
    public async Task A_redirect_or_a_receiver_that_cannot_be_reached_is_a_failure_with_the_reason_kept()
    {
        var redirected = await Endpoint("order.placed");
        await Order();
        var clock = new Clock();
        await Dispatch(new Receiver(_ => HttpStatusCode.Redirect), clock);
        var delivery = await OnlyDelivery(redirected);
        Assert.Equal((WebhookDeliveryStatus.Pending, 302), (delivery.Status, delivery.LastStatusCode));

        var down = await Endpoint("order.placed");
        await Order();
        using var scope = factory.Services.CreateScope();
        var broken = new Receiver(_ => throw new HttpRequestException("Name or service not known"));
        var d = new WebhookDispatcher(scope.ServiceProvider.GetRequiredService<PiranhaDbContext>(), broken, Options.Create(new WebhookOptions()), NullLogger<WebhookDispatcher>.Instance, clock);
        var result = await d.DispatchDueAsync();
        Assert.True(result.Retrying >= 1);
        var unreachable = await OnlyDelivery(down);
        Assert.Null(unreachable.LastStatusCode);
        Assert.Contains("Name or service not known", unreachable.LastError);
    }

    [Fact]
    public async Task An_order_that_fails_to_be_placed_leaves_no_event_behind()
    {
        var endpoint = await Endpoint("order.placed");
        var p = await Read<ProductDto>(await _c.PostAsJsonAsync("/api/v1/products", new ProductInput { Name = "Short " + Tag(), Sku = "WS-" + Tag(), Price = 5m }));
        await _c.PutAsJsonAsync($"/api/v1/products/{p.Id}/listing", new ProductListingInput { Published = true });
        await _c.PostAsJsonAsync($"/api/v1/products/{p.Id}/stock/adjustments", new StockAdjustmentInput { Delta = 1, Reason = "Delivery" });

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PiranhaDbContext>();
        var before = await db.Set<WebhookEvent>().CountAsync(e => e.Type == "order.placed");
        Assert.Equal(HttpStatusCode.Conflict, (await TryPlace(p, 5)).StatusCode); // only one in stock
        Assert.Equal(before, await db.Set<WebhookEvent>().AsNoTracking().CountAsync(e => e.Type == "order.placed"));

        var receiver = new Receiver();
        await Dispatch(receiver, new Clock());
        Assert.Empty(receiver.To(endpoint.Url));
    }

    [Fact]
    public async Task Two_senders_running_at_once_send_each_message_only_once()
    {
        var endpoint = await Endpoint("order.placed");
        for (var i = 0; i < 5; i++) await Order();

        var receiver = new Receiver();
        var clock = new Clock();
        await Task.WhenAll(Dispatch(receiver, clock), Dispatch(receiver, clock), Dispatch(receiver, clock));

        var calls = receiver.To(endpoint.Url).ToList();
        Assert.Equal(5, calls.Count);
        Assert.Equal(5, calls.Select(c => c.Headers["Piranha-Event-Id"]).Distinct().Count());
        var deliveries = await Read<PagedResult<WebhookDeliveryDto>>(await _c.GetAsync($"/api/v1/webhooks/{endpoint.Id}/deliveries?pageSize=50"));
        Assert.All(deliveries.Items, d => Assert.Equal((WebhookDeliveryStatus.Succeeded, 1), (d.Status, d.Attempts)));
    }

    [Fact]
    public async Task A_test_is_sent_at_once_even_when_the_endpoint_is_switched_off_and_old_finished_messages_are_cleared()
    {
        var endpoint = await Endpoint();
        Assert.True((await _c.PutAsJsonAsync($"/api/v1/webhooks/{endpoint.Id}", new WebhookEndpointInput { Url = endpoint.Url, Active = false }, Json)).IsSuccessStatusCode);

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PiranhaDbContext>();
        var receiver = new Receiver();
        var clock = new Clock();
        var dispatcher = new WebhookDispatcher(db, receiver, Options.Create(new WebhookOptions { RetentionDays = 30 }), NullLogger<WebhookDispatcher>.Instance, clock);
        var sent = await dispatcher.SendTestAsync(endpoint.Id);
        Assert.NotNull(sent);
        Assert.Equal((WebhookDeliveryStatus.Succeeded, true), (sent.Status, sent.IsTest));
        var call = Assert.Single(receiver.To(endpoint.Url));
        Assert.Equal("webhook.test", call.Type);
        Assert.True(SignatureIsValid(call, endpoint.Secret!));
        Assert.Null(await dispatcher.SendTestAsync(Guid.NewGuid()));
        Assert.Equal(HttpStatusCode.NotFound, (await _c.PostAsync($"/api/v1/webhooks/{Guid.NewGuid()}/test", null)).StatusCode);

        // Nothing is cleared while it is recent; after the retention period it is.
        await dispatcher.PurgeAsync();
        Assert.True(await db.Set<WebhookDelivery>().AnyAsync(d => d.Id == sent.Id));
        clock.Advance(TimeSpan.FromDays(40));
        await dispatcher.PurgeAsync();
        db.ChangeTracker.Clear();
        Assert.False(await db.Set<WebhookDelivery>().AnyAsync(d => d.Id == sent.Id));
        Assert.False(await db.Set<WebhookEvent>().AnyAsync(e => e.Id == sent.EventId));
    }

    [Fact]
    public async Task Endpoints_are_checked_the_secret_is_shown_once_and_never_reaches_the_audit_log()
    {
        async Task<HttpStatusCode> Try(string url, params string[] types) =>
            (await _c.PostAsJsonAsync("/api/v1/webhooks", new WebhookEndpointInput { Url = url, EventTypes = [.. types] }, Json)).StatusCode;
        Assert.Equal(HttpStatusCode.BadRequest, await Try("http://hooks.example.com/x"));      // not https
        Assert.Equal(HttpStatusCode.BadRequest, await Try("ftp://hooks.example.com/x"));
        Assert.Equal(HttpStatusCode.BadRequest, await Try("https://user:pass@hooks.example.com/x"));
        Assert.Equal(HttpStatusCode.BadRequest, await Try("not a url"));
        Assert.Equal(HttpStatusCode.BadRequest, await Try("https://hooks.example.com/x", "order.exploded"));
        var local = await _c.PostAsJsonAsync("/api/v1/webhooks", new WebhookEndpointInput { Url = "http://localhost:9/hook" }, Json);
        Assert.Equal(HttpStatusCode.Created, local.StatusCode);
        _endpoints.Add((await Read<WebhookEndpointDto>(local)).Id);

        var created = await Endpoint("order.placed", "payment.received");
        Assert.StartsWith("whsec_", created.Secret);
        Assert.Equal(["order.placed", "payment.received"], created.EventTypes);

        var read = await Read<WebhookEndpointDto>(await _c.GetAsync($"/api/v1/webhooks/{created.Id}"));
        Assert.Null(read.Secret);
        Assert.EndsWith(created.Secret![^4..], read.SecretHint);
        Assert.DoesNotContain(created.Secret, await (await _c.GetAsync("/api/v1/webhooks")).Content.ReadAsStringAsync());

        var rotated = await Read<WebhookEndpointDto>(await _c.PostAsync($"/api/v1/webhooks/{created.Id}/rotate-secret", null));
        Assert.NotEqual(created.Secret, rotated.Secret);
        var updated = await Read<WebhookEndpointDto>(await _c.PutAsJsonAsync($"/api/v1/webhooks/{created.Id}", new WebhookEndpointInput { Url = created.Url, Description = "Fulfilment", EventTypes = ["*"] }, Json));
        Assert.Equal(["*"], updated.EventTypes);
        Assert.Equal(("Fulfilment", rotated.SecretHint), (updated.Description, updated.SecretHint)); // the secret survives an edit
        Assert.Contains(await Read<List<WebhookEventTypeDto>>(await _c.GetAsync("/api/v1/webhooks/event-types")), t => t.Type == "order.placed");

        var audit = await (await _c.GetAsync($"/api/v1/audit?entityType=WebhookEndpoint&entityId={created.Id}&pageSize=50")).Content.ReadAsStringAsync();
        Assert.Contains("Secret", audit);
        Assert.DoesNotContain(created.Secret, audit);
        Assert.DoesNotContain(rotated.Secret!, audit);

        Assert.Equal(HttpStatusCode.NotFound, (await _c.GetAsync($"/api/v1/webhooks/{Guid.NewGuid()}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await _c.PutAsJsonAsync($"/api/v1/webhooks/{Guid.NewGuid()}", new WebhookEndpointInput { Url = created.Url }, Json)).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await _c.DeleteAsync($"/api/v1/webhooks/{created.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await _c.DeleteAsync($"/api/v1/webhooks/{created.Id}")).StatusCode);

        // Addresses, logs and keys are for administrators, even to read.
        var (email, password) = await factory.CreateReadOnlyUserAsync();
        var reader = await factory.CreateAuthenticatedApiClientAsync(email, password);
        Assert.Equal(HttpStatusCode.Forbidden, (await reader.GetAsync("/api/v1/webhooks")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await reader.PostAsJsonAsync("/api/v1/webhooks", new WebhookEndpointInput { Url = "https://hooks.example.com/y" }, Json)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await reader.PostAsync($"/api/v1/webhook-deliveries/{Guid.NewGuid()}/redeliver", null)).StatusCode);
    }
}
