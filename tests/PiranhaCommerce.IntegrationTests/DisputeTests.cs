using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using PiranhaCommerce.Catalog;
using PiranhaCommerce.Core.Paging;
using PiranhaCommerce.Customers;
using PiranhaCommerce.Ecommerce;

namespace PiranhaCommerce.IntegrationTests;

/// <summary>Bank disputes and refunds that fail: recorded once, the shop told about the deadline, and a lost dispute booked as money gone back.</summary>
[Collection(OrderSettings.Name)]
public class DisputeTests(AlertFactory factory) : IClassFixture<AlertFactory>
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };
    private static string Tag() => Guid.NewGuid().ToString("N")[..8];
    private static async Task<T> Read<T>(HttpResponseMessage r) => (await r.Content.ReadFromJsonAsync<T>(Json))!;

    private static async Task<OrderDto> PaidOrder(HttpClient staff)
    {
        var p = await Read<ProductDto>(await staff.PostAsJsonAsync("/api/v1/products", new ProductInput { Name = "D " + Tag(), Sku = "D-" + Tag(), Price = 10m }));
        await staff.PutAsJsonAsync($"/api/v1/products/{p.Id}/listing", new ProductListingInput { Published = true });
        await staff.PostAsJsonAsync($"/api/v1/products/{p.Id}/stock/adjustments", new StockAdjustmentInput { Delta = 10, Reason = "Delivery" });
        var b = await Read<BasketDto>(await staff.PostAsJsonAsync("/api/v1/baskets", new BasketCreateInput()));
        await staff.PutAsJsonAsync($"/api/v1/baskets/{b.Id}/lines/{p.Id}", new BasketLineInput { Quantity = 1 });
        var order = await Read<OrderDto>(await staff.PostAsJsonAsync($"/api/v1/baskets/{b.Id}/checkout", new CheckoutInput
        {
            Email = $"d-{Tag()}@example.com",
            ShippingAddress = new AddressInput { Recipient = "Ada", Line1 = "1 High St", City = "Leeds", PostalCode = "LS1", CountryCode = "GB" },
        }));
        await staff.PostAsJsonAsync($"/api/v1/orders/{order.Id}/payments", new PaymentInput { Amount = order.Total, Provider = "testpay", Reference = "ch_" + Tag() });
        return order;
    }

    private async Task<WebhookResultDto> Hook(TestGateway.WireEvent e)
    {
        var body = JsonSerializer.SerializeToUtf8Bytes(e, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/payments/webhooks/testpay") { Content = new ByteArrayContent(body) };
        request.Content.Headers.ContentType = new("application/json");
        request.Headers.Add("X-Test-Signature", TestGateway.Sign(body));
        var response = await factory.CreateClient().SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await Read<WebhookResultDto>(response);
    }

    private static async Task<List<PaymentDisputeDto>> DisputesOf(HttpClient staff, OrderDto o) => await Read<List<PaymentDisputeDto>>(await staff.GetAsync($"/api/v1/orders/{o.Id}/disputes"));

    [Fact]
    public async Task An_opened_dispute_is_recorded_once_and_tells_the_shop_about_the_deadline()
    {
        var staff = await factory.CreateAuthenticatedApiClientAsync();
        var order = await PaidOrder(staff);
        var before = factory.Email.To("alerts@example.com").Count;
        var open = new TestGateway.WireEvent("dispute-opened", order.Id, order.Total, order.Currency, "dp_" + Tag(), "product_not_received");

        Assert.Equal((1, 0, 0), Counts(await Hook(open)));
        Assert.Equal((0, 1, 0), Counts(await Hook(open)));
        var simultaneous = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => Hook(open)));
        Assert.Equal(0, simultaneous.Sum(r => r.Recorded));

        var dispute = Assert.Single(await DisputesOf(staff, order));
        Assert.Equal((DisputeStatus.Open, "product_not_received", order.Total), (dispute.Status, dispute.Reason, dispute.Amount));
        Assert.Equal(new DateTimeOffset(2030, 1, 15, 0, 0, 0, TimeSpan.Zero), dispute.RespondByUtc);
        Assert.Equal(order.Reference, dispute.OrderReference);

        var mails = factory.Email.To("alerts@example.com").Skip(before).Where(m => m.TemplateKey == "alert.dispute-opened").ToList();
        Assert.Single(mails); // not once per replay
        Assert.Equal("2030-01-15", mails[0].Variables["respondBy"]);
        EmailContract.AssertMatchesDescriptor(factory, mails[0]);

        // Nothing moved: the order still shows what it was paid.
        Assert.Equal(order.Total, (await Read<OrderDto>(await staff.GetAsync($"/api/v1/orders/{order.Id}"))).AmountPaid);
        var all = await Read<PagedResult<PaymentDisputeDto>>(await staff.GetAsync("/api/v1/disputes?status=Open&pageSize=100"));
        Assert.Contains(all.Items, d => d.Id == dispute.Id);
    }

    [Fact]
    public async Task A_won_dispute_closes_without_moving_money_and_never_reopens()
    {
        var staff = await factory.CreateAuthenticatedApiClientAsync();
        var order = await PaidOrder(staff);
        var id = "dp_" + Tag();
        await Hook(new("dispute-opened", order.Id, order.Total, order.Currency, id));
        Assert.Equal((1, 0, 0), Counts(await Hook(new("dispute-won", order.Id, order.Total, order.Currency, id))));
        Assert.Equal((0, 1, 0), Counts(await Hook(new("dispute-won", order.Id, order.Total, order.Currency, id))));
        Assert.Equal((0, 1, 0), Counts(await Hook(new("dispute-opened", order.Id, order.Total, order.Currency, id)))); // a late replay of the opening

        var dispute = Assert.Single(await DisputesOf(staff, order));
        Assert.Equal(DisputeStatus.Won, dispute.Status);
        Assert.NotNull(dispute.ClosedUtc);
        var after = await Read<OrderDto>(await staff.GetAsync($"/api/v1/orders/{order.Id}"));
        Assert.Equal((order.Total, 0m), (after.AmountPaid, after.AmountRefunded));
    }

    [Fact]
    public async Task A_lost_dispute_is_booked_as_a_refund_once_without_emailing_the_buyer()
    {
        var staff = await factory.CreateAuthenticatedApiClientAsync();
        var order = await PaidOrder(staff);
        var id = "dp_" + Tag();
        await Hook(new("dispute-opened", order.Id, order.Total, order.Currency, id));
        var buyerEmails = factory.Email.To(order.Email).Count;

        Assert.Equal((1, 0, 0), Counts(await Hook(new("dispute-lost", order.Id, order.Total, order.Currency, id))));
        Assert.Equal((0, 1, 0), Counts(await Hook(new("dispute-lost", order.Id, order.Total, order.Currency, id))));

        Assert.Equal(DisputeStatus.Lost, Assert.Single(await DisputesOf(staff, order)).Status);
        var after = await Read<OrderDto>(await staff.GetAsync($"/api/v1/orders/{order.Id}"));
        Assert.Equal((order.Total, order.Total), (after.AmountPaid, after.AmountRefunded));
        var payments = await Read<List<PaymentDto>>(await staff.GetAsync($"/api/v1/orders/{order.Id}/payments"));
        var refund = payments.Single(p => p.Kind == PaymentKind.Refund);
        Assert.Equal(id, refund.Reference);
        Assert.Contains("Lost dispute", refund.Note);
        Assert.DoesNotContain(factory.Email.To(order.Email).Skip(buyerEmails), m => m.TemplateKey == "order.refunded");
    }

    [Fact]
    public async Task A_dispute_lost_that_cannot_be_booked_still_closes_and_waits_on_the_unreconciled_list()
    {
        var staff = await factory.CreateAuthenticatedApiClientAsync();
        var order = await PaidOrder(staff);
        // The shop already refunded the buyer in full, so the chargeback has nothing left to take back here.
        await staff.PostAsJsonAsync($"/api/v1/orders/{order.Id}/refunds", new PaymentInput { Amount = order.Total, Reference = "man_" + Tag() });
        var id = "dp_" + Tag();

        Assert.Equal((0, 0, 1), Counts(await Hook(new("dispute-lost", order.Id, order.Total, order.Currency, id))));
        Assert.Equal(DisputeStatus.Lost, Assert.Single(await DisputesOf(staff, order)).Status);
        var listed = await Read<PagedResult<UnreconciledPaymentDto>>(await staff.GetAsync("/api/v1/unreconciled-payments?resolved=false&pageSize=100"));
        var row = Assert.Single(listed.Items, u => u.Reference == id);
        Assert.Equal(GatewayEventKind.DisputeLost, row.Kind);
    }

    [Fact]
    public async Task A_failed_refund_is_listed_once_for_a_person_and_is_not_money_to_book()
    {
        var staff = await factory.CreateAuthenticatedApiClientAsync();
        var order = await PaidOrder(staff);
        var id = "re_" + Tag();
        var before = factory.Email.To("alerts@example.com").Count;

        Assert.Equal((0, 0, 1), Counts(await Hook(new("refund-failed", order.Id, 5m, order.Currency, id))));
        Assert.Equal((0, 1, 0), Counts(await Hook(new("refund-failed", order.Id, 5m, order.Currency, id))));
        Assert.Single(factory.Email.To("alerts@example.com").Skip(before), m => m.TemplateKey == "alert.unreconciled-payment");

        var listed = await Read<PagedResult<UnreconciledPaymentDto>>(await staff.GetAsync("/api/v1/unreconciled-payments?resolved=false&pageSize=100"));
        var row = Assert.Single(listed.Items, u => u.Reference == id);
        Assert.Equal(GatewayEventKind.RefundFailed, row.Kind);
        Assert.Equal(order.Total, (await Read<OrderDto>(await staff.GetAsync($"/api/v1/orders/{order.Id}"))).AmountPaid); // nothing was booked

        // Retrying must not turn a failed refund into a booked one; it is dealt with at the provider and then resolved.
        Assert.Equal(HttpStatusCode.Conflict, (await staff.PostAsync($"/api/v1/unreconciled-payments/{row.Id}/retry", null)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await staff.PostAsJsonAsync($"/api/v1/unreconciled-payments/{row.Id}/resolve", new ResolveUnreconciledInput { Resolution = "Refunded by bank transfer" })).StatusCode);
        Assert.Equal(0m, (await Read<OrderDto>(await staff.GetAsync($"/api/v1/orders/{order.Id}"))).AmountRefunded);
    }

    [Fact]
    public async Task A_dispute_for_an_unknown_order_is_listed_and_only_readers_can_see_disputes()
    {
        var staff = await factory.CreateAuthenticatedApiClientAsync();
        var id = "dp_" + Tag();
        Assert.Equal((0, 0, 1), Counts(await Hook(new("dispute-opened", Guid.NewGuid(), 5m, "GBP", id))));
        var listed = await Read<PagedResult<UnreconciledPaymentDto>>(await staff.GetAsync("/api/v1/unreconciled-payments?resolved=false&pageSize=100"));
        Assert.Contains(listed.Items, u => u.Reference == id && u.Kind == GatewayEventKind.DisputeOpened);

        Assert.Equal(HttpStatusCode.NotFound, (await staff.GetAsync($"/api/v1/orders/{Guid.NewGuid()}/disputes")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await factory.CreateClient().GetAsync("/api/v1/disputes")).StatusCode);
        var (email, password) = await factory.CreateReadOnlyUserAsync();
        var reader = await factory.CreateAuthenticatedApiClientAsync(email, password);
        Assert.Equal(HttpStatusCode.OK, (await reader.GetAsync("/api/v1/disputes")).StatusCode);
    }

    private static (int, int, int) Counts(WebhookResultDto r) => (r.Recorded, r.AlreadyRecorded, r.Unreconciled);
}
