using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using PiranhaCommerce.Api.Auth;
using PiranhaCommerce.Catalog;
using PiranhaCommerce.Core.Paging;
using PiranhaCommerce.Customers;
using PiranhaCommerce.Ecommerce;

namespace PiranhaCommerce.IntegrationTests;

[Collection(OrderSettings.Name)]
public class PaymentTests(PiranhaCommerceFactory factory) : IClassFixture<PiranhaCommerceFactory>
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };

    private static string Tag() => Guid.NewGuid().ToString("N")[..8];

    private static async Task<T> Read<T>(HttpResponseMessage r) => (await r.Content.ReadFromJsonAsync<T>(Json))!;

    /// <summary>An order of exactly <paramref name="total"/> (one unit, no shipping or tax configured).</summary>
    private static async Task<OrderDto> NewOrder(HttpClient c, decimal total = 100m)
    {
        var p = await Read<ProductDto>(await c.PostAsJsonAsync("/api/v1/products", new ProductInput { Name = "Pay " + Tag(), Price = total }));
        await c.PutAsJsonAsync($"/api/v1/products/{p.Id}/listing", new ProductListingInput { Published = true });
        await c.PostAsJsonAsync($"/api/v1/products/{p.Id}/stock/adjustments", new StockAdjustmentInput { Delta = 5, Reason = "Delivery" });
        var basket = await Read<BasketDto>(await c.PostAsJsonAsync("/api/v1/baskets", new BasketCreateInput()));
        await c.PutAsJsonAsync($"/api/v1/baskets/{basket.Id}/lines/{p.Id}", new BasketLineInput { Quantity = 1 });
        var r = await c.PostAsJsonAsync($"/api/v1/baskets/{basket.Id}/checkout", new CheckoutInput
        {
            Email = $"pay-{Tag()}@example.com",
            ShippingAddress = new AddressInput { Line1 = "1 High St", City = "Leeds", CountryCode = "GB" },
        });
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        return await Read<OrderDto>(r);
    }

    private static Task<HttpResponseMessage> Pay(HttpClient c, Guid orderId, decimal amount, string? reference = null, string? provider = null) =>
        c.PostAsJsonAsync($"/api/v1/orders/{orderId}/payments", new PaymentInput { Amount = amount, Reference = reference, Provider = provider });

    private static Task<HttpResponseMessage> Refund(HttpClient c, Guid orderId, decimal amount) =>
        c.PostAsJsonAsync($"/api/v1/orders/{orderId}/refunds", new PaymentInput { Amount = amount });

    private static async Task<OrderDto> Get(HttpClient c, Guid id) => await Read<OrderDto>(await c.GetAsync($"/api/v1/orders/{id}"));

    [Fact]
    public async Task Part_payments_move_the_order_from_unpaid_to_paid_and_are_listed()
    {
        var c = await factory.CreateAuthenticatedApiClientAsync();
        var order = await NewOrder(c, 100m);
        Assert.Equal(PaymentStatus.Unpaid, order.PaymentStatus);

        var first = await Pay(c, order.Id, 40m, "RCPT-" + Tag());
        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        var dto = await Read<PaymentDto>(first);
        Assert.Equal((PaymentKind.Payment, "manual", "GBP", PiranhaCommerceFactory.AdminEmail), (dto.Kind, dto.Provider, dto.Currency, dto.Actor));
        Assert.Equal(PaymentStatus.PartlyPaid, (await Get(c, order.Id)).PaymentStatus);

        Assert.Equal(HttpStatusCode.Created, (await Pay(c, order.Id, 60m)).StatusCode);
        var paid = await Get(c, order.Id);
        Assert.Equal((PaymentStatus.Paid, 100m, 0m), (paid.PaymentStatus, paid.AmountPaid, paid.AmountRefunded));

        var list = await Read<List<PaymentDto>>(await c.GetAsync($"/api/v1/orders/{order.Id}/payments"));
        Assert.Equal([40m, 60m], list.Select(p => p.Amount));

        var summary = (await Read<PagedResult<OrderSummaryDto>>(await c.GetAsync($"/api/v1/orders?search={order.Number}"))).Items.Single();
        Assert.Equal(PaymentStatus.Paid, summary.PaymentStatus);
    }

    [Fact]
    public async Task Paying_more_than_is_owed_is_refused_and_bad_input_is_rejected()
    {
        var c = await factory.CreateAuthenticatedApiClientAsync();
        var order = await NewOrder(c, 50m);

        Assert.Equal(HttpStatusCode.Conflict, (await Pay(c, order.Id, 50.01m)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await Pay(c, order.Id, 0m)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await Pay(c, order.Id, -5m)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await Pay(c, order.Id, 10.005m)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await Pay(c, order.Id, 10m, provider: "nonsense")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await Pay(c, Guid.NewGuid(), 10m)).StatusCode);
        Assert.Equal(PaymentStatus.Unpaid, (await Get(c, order.Id)).PaymentStatus);

        var providers = await c.GetFromJsonAsync<List<PaymentProviderDto>>("/api/v1/payment-providers");
        Assert.Contains(providers!, p => p.Name == "manual");
    }

    [Fact]
    public async Task Simultaneous_payments_cannot_take_an_order_past_its_total()
    {
        var c = await factory.CreateAuthenticatedApiClientAsync();
        var order = await NewOrder(c, 100m);

        var results = await Task.WhenAll(Enumerable.Range(0, 6).Select(_ => Pay(c, order.Id, 100m)));
        Assert.Equal(1, results.Count(r => r.StatusCode == HttpStatusCode.Created));
        Assert.Equal(5, results.Count(r => r.StatusCode == HttpStatusCode.Conflict));
        Assert.Equal(100m, (await Get(c, order.Id)).AmountPaid);

        var parts = await NewOrder(c, 100m);
        var mixed = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => Pay(c, parts.Id, 30m)));
        Assert.Equal(3, mixed.Count(r => r.StatusCode == HttpStatusCode.Created)); // 30 + 30 + 30 fits, a fourth would be 120
        Assert.Equal(90m, (await Get(c, parts.Id)).AmountPaid);
    }

    [Fact]
    public async Task A_provider_reference_can_only_be_recorded_once()
    {
        var c = await factory.CreateAuthenticatedApiClientAsync();
        var order = await NewOrder(c, 100m);
        var reference = "TXN-" + Tag();

        Assert.Equal(HttpStatusCode.Created, (await Pay(c, order.Id, 10m, reference)).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await Pay(c, order.Id, 10m, reference)).StatusCode); // a retried webhook or double click
        Assert.Equal(10m, (await Get(c, order.Id)).AmountPaid);

        var simultaneous = await Task.WhenAll(Enumerable.Range(0, 5).Select(_ => Pay(c, order.Id, 5m, "SAME-" + order.Number)));
        Assert.Equal(1, simultaneous.Count(r => r.StatusCode == HttpStatusCode.Created));
        Assert.Equal(15m, (await Get(c, order.Id)).AmountPaid);

        // payments without a reference never collide with each other
        Assert.Equal(HttpStatusCode.Created, (await Pay(c, order.Id, 1m)).StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await Pay(c, order.Id, 1m)).StatusCode);
    }

    [Fact]
    public async Task Refunds_are_limited_to_what_was_received_and_change_the_status()
    {
        var c = await factory.CreateAuthenticatedApiClientAsync();
        var order = await NewOrder(c, 100m);
        Assert.Equal(HttpStatusCode.Conflict, (await Refund(c, order.Id, 1m)).StatusCode); // nothing received yet

        await Pay(c, order.Id, 100m);
        Assert.Equal(HttpStatusCode.Created, (await Refund(c, order.Id, 30m)).StatusCode);
        var partly = await Get(c, order.Id);
        Assert.Equal((PaymentStatus.PartlyPaid, 100m, 30m), (partly.PaymentStatus, partly.AmountPaid, partly.AmountRefunded));
        Assert.Equal(HttpStatusCode.Conflict, (await Refund(c, order.Id, 70.01m)).StatusCode);

        var results = await Task.WhenAll(Enumerable.Range(0, 5).Select(_ => Refund(c, order.Id, 70m)));
        Assert.Equal(1, results.Count(r => r.StatusCode == HttpStatusCode.Created)); // never returns more than was received
        var done = await Get(c, order.Id);
        Assert.Equal((PaymentStatus.Refunded, 100m, 100m), (done.PaymentStatus, done.AmountPaid, done.AmountRefunded));

        // the money can be taken again after a full refund
        Assert.Equal(HttpStatusCode.Created, (await Pay(c, order.Id, 100m)).StatusCode);
    }

    [Fact]
    public async Task A_cancelled_order_takes_no_payment_but_can_be_refunded()
    {
        var c = await factory.CreateAuthenticatedApiClientAsync();
        var order = await NewOrder(c, 80m);
        await Pay(c, order.Id, 80m);

        var cancel = await c.PostAsJsonAsync($"/api/v1/orders/{order.Id}/stage", new OrderStageInput { Stage = OrderStage.Cancelled }, Json);
        Assert.Equal(HttpStatusCode.OK, cancel.StatusCode);
        var cancelled = await Read<OrderDto>(cancel);
        Assert.Equal(80m, cancelled.AmountPaid); // money is still on record: a refund is owed

        Assert.Equal(HttpStatusCode.Conflict, (await Pay(c, order.Id, 1m)).StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await Refund(c, order.Id, 80m)).StatusCode);
        Assert.Equal(PaymentStatus.Refunded, (await Get(c, order.Id)).PaymentStatus);
    }

    [Fact]
    public async Task Payment_can_be_required_before_an_order_is_processed()
    {
        using var strict = factory.WithWebHostBuilder(b => b.UseSetting("PiranhaCommerce:Ecommerce:RequirePaymentBeforeProcessing", "true"));
        var token = await (await strict.CreateClient().PostAsJsonAsync("/api/v1/auth/token", new TokenRequest(PiranhaCommerceFactory.AdminEmail, PiranhaCommerceFactory.AdminPassword)))
            .Content.ReadFromJsonAsync<TokenResponse>();
        var c = strict.CreateClient();
        c.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token!.AccessToken);
        var order = await NewOrder(c, 20m);

        Task<HttpResponseMessage> Process() => c.PostAsJsonAsync($"/api/v1/orders/{order.Id}/stage", new OrderStageInput { Stage = OrderStage.Processing }, Json);
        Assert.Equal(HttpStatusCode.Conflict, (await Process()).StatusCode);
        await Pay(c, order.Id, 10m);
        Assert.Equal(HttpStatusCode.Conflict, (await Process()).StatusCode); // part payment is not enough
        await Pay(c, order.Id, 10m);
        Assert.Equal(HttpStatusCode.OK, (await Process()).StatusCode);
    }

    [Fact]
    public async Task Without_the_requirement_an_unpaid_order_can_still_be_processed()
    {
        var c = await factory.CreateAuthenticatedApiClientAsync();
        var order = await NewOrder(c, 20m);
        Assert.Equal(HttpStatusCode.OK, (await c.PostAsJsonAsync($"/api/v1/orders/{order.Id}/stage", new OrderStageInput { Stage = OrderStage.Processing }, Json)).StatusCode);
    }

    [Fact]
    public async Task Read_only_users_can_see_payments_but_not_record_them()
    {
        var admin = await factory.CreateAuthenticatedApiClientAsync();
        var order = await NewOrder(admin, 10m);
        var (email, password) = await factory.CreateReadOnlyUserAsync();
        var reader = await factory.CreateAuthenticatedApiClientAsync(email, password);

        Assert.Equal(HttpStatusCode.OK, (await reader.GetAsync($"/api/v1/orders/{order.Id}/payments")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await Pay(reader, order.Id, 5m)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await Refund(reader, order.Id, 5m)).StatusCode);
    }
}
