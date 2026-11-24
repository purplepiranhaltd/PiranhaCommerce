using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.DependencyInjection;
using PiranhaCommerce.Catalog;
using PiranhaCommerce.Core.Email;
using PiranhaCommerce.Customers;
using PiranhaCommerce.Ecommerce;
using PiranhaCommerce.Email.Smtp;

namespace PiranhaCommerce.IntegrationTests;

/// <summary>The emails that follow a change to an order: a parcel sent, a cancel, a refund.</summary>
[Collection(OrderSettings.Name)]
public class OrderUpdateEmailTests(PiranhaCommerceFactory factory) : IClassFixture<PiranhaCommerceFactory>
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };
    private static string Tag() => Guid.NewGuid().ToString("N")[..10];
    private static async Task<T> Read<T>(HttpResponseMessage r) => (await r.Content.ReadFromJsonAsync<T>(Json))!;

    private async Task<(HttpClient Staff, ProductDto Product, OrderDto Order)> NewOrder(int quantity, decimal price = 10m)
    {
        var staff = await factory.CreateAuthenticatedApiClientAsync();
        var product = await Read<ProductDto>(await staff.PostAsJsonAsync("/api/v1/products", new ProductInput { Name = "Upd " + Tag(), Sku = "UPD-" + Tag(), Price = price }));
        await staff.PutAsJsonAsync($"/api/v1/products/{product.Id}/listing", new ProductListingInput { Published = true });
        await staff.PostAsJsonAsync($"/api/v1/products/{product.Id}/stock/adjustments", new StockAdjustmentInput { Delta = 20, Reason = "Delivery" });
        var basket = await Read<BasketDto>(await staff.PostAsJsonAsync("/api/v1/baskets", new BasketCreateInput()));
        await staff.PutAsJsonAsync($"/api/v1/baskets/{basket.Id}/lines/{product.Id}", new BasketLineInput { Quantity = quantity });
        var order = await Read<OrderDto>(await staff.PostAsJsonAsync($"/api/v1/baskets/{basket.Id}/checkout", new CheckoutInput
        {
            Email = $"upd-{Tag()}@example.com",
            ShippingAddress = new AddressInput { Recipient = "Ada Buyer", Line1 = "1 High St", City = "Leeds", PostalCode = "LS1 1AA", CountryCode = "GB" },
        }));
        return (staff, product, order);
    }

    private IReadOnlyList<EmailMessage> Sent(OrderDto o, string key) => factory.Email.To(o.Email).Where(m => m.TemplateKey == key).ToList();
    private static Task<HttpResponseMessage> Stage(HttpClient c, OrderDto o, OrderStage to) => c.PostAsJsonAsync($"/api/v1/orders/{o.Id}/stage", new OrderStageInput { Stage = to }, Json);
    private static Task<HttpResponseMessage> Ship(HttpClient c, OrderDto o, ShipmentInput input) => c.PostAsJsonAsync($"/api/v1/orders/{o.Id}/shipments", input, Json);
    private static Task<HttpResponseMessage> Refund(HttpClient c, OrderDto o, decimal amount) =>
        c.PostAsJsonAsync($"/api/v1/orders/{o.Id}/refunds", new PaymentInput { Amount = amount, Provider = "manual", Reference = "ref-" + Tag() });
    private static Task<HttpResponseMessage> Pay(HttpClient c, OrderDto o, decimal amount, string provider = "manual") =>
        c.PostAsJsonAsync($"/api/v1/orders/{o.Id}/payments", new PaymentInput { Amount = amount, Provider = provider, Reference = "pay-" + Tag() });

    [Fact]
    public async Task Each_parcel_emails_the_buyer_and_the_last_one_says_the_order_is_complete()
    {
        var (staff, _, order) = await NewOrder(3);
        await Stage(staff, order, OrderStage.Processing);
        order = await Read<OrderDto>(await staff.GetAsync($"/api/v1/orders/{order.Id}"));
        Assert.Empty(Sent(order, EcommerceEmailTemplates.OrderShipped)); // nothing yet

        await Ship(staff, order, new ShipmentInput
        {
            Carrier = "DPD", TrackingNumber = "DPD-1", TrackingUrl = "https://track.example/DPD-1",
            Lines = [new FulfilmentLineInput { OrderLineId = order.Lines[0].Id, Quantity = 1 }],
        });
        var first = Assert.Single(Sent(order, EcommerceEmailTemplates.OrderShipped));
        EmailContract.AssertMatchesDescriptor(factory, first);
        Assert.Equal(order.Reference, first.Variables["reference"]);
        Assert.Equal(("DPD", "DPD-1", "https://track.example/DPD-1", false), (first.Variables["carrier"], first.Variables["trackingNumber"], first.Variables["trackingUrl"], first.Variables["complete"]));
        var inParcel = Assert.Single((IEnumerable<object?>)first.Variables["lines"]!);
        Assert.Equal(1, ((Dictionary<string, object?>)inParcel!)["quantity"]);
        var left = Assert.Single((IEnumerable<object?>)first.Variables["stillToSend"]!);
        Assert.Equal(2, ((Dictionary<string, object?>)left!)["quantity"]);

        await Ship(staff, order, new ShipmentInput());
        var all = Sent(order, EcommerceEmailTemplates.OrderShipped);
        Assert.Equal(2, all.Count);
        Assert.Equal(true, all[1].Variables["complete"]);
        Assert.Empty((IEnumerable<object?>)all[1].Variables["stillToSend"]!);
    }

    [Fact]
    public async Task A_parcel_that_is_refused_or_an_order_completed_by_hand_sends_no_email()
    {
        var (staff, _, order) = await NewOrder(2);
        Assert.Equal(HttpStatusCode.Conflict, (await Ship(staff, order, new ShipmentInput())).StatusCode); // still Placed
        await Stage(staff, order, OrderStage.Processing);
        await Stage(staff, order, OrderStage.Completed); // "all sent", recorded after the fact, with no tracking to tell anyone
        Assert.Empty(Sent(order, EcommerceEmailTemplates.OrderShipped));
    }

    [Fact]
    public async Task Cancelling_emails_the_buyer_and_says_what_is_still_to_be_refunded_only_when_something_was_paid()
    {
        var (staff, _, unpaid) = await NewOrder(1);
        Assert.Equal(HttpStatusCode.OK, (await Stage(staff, unpaid, OrderStage.Cancelled)).StatusCode);
        var plain = Assert.Single(Sent(unpaid, EcommerceEmailTemplates.OrderCancelled));
        EmailContract.AssertMatchesDescriptor(factory, plain);
        Assert.Null(plain.Variables["stillToRefund"]);

        var (_, _, paid) = await NewOrder(1);
        await Pay(staff, paid, paid.Total);
        await Stage(staff, paid, OrderStage.Cancelled);
        var owed = Assert.Single(Sent(paid, EcommerceEmailTemplates.OrderCancelled));
        Assert.Equal(paid.Total.ToString("N2", System.Globalization.CultureInfo.InvariantCulture), owed.Variables["stillToRefund"]);
    }

    [Fact]
    public async Task A_refused_or_repeated_cancel_sends_one_email_at_most()
    {
        var (staff, _, order) = await NewOrder(1);
        var results = await Task.WhenAll(Enumerable.Range(0, 5).Select(_ => Stage(staff, order, OrderStage.Cancelled)));
        Assert.Equal(1, results.Count(r => r.StatusCode == HttpStatusCode.OK));
        Assert.Single(Sent(order, EcommerceEmailTemplates.OrderCancelled));
    }

    [Fact]
    public async Task A_refund_emails_the_buyer_with_the_amount_and_the_total_so_far()
    {
        var (staff, _, order) = await NewOrder(2, 20m);
        await Pay(staff, order, order.Total);

        Assert.Equal(HttpStatusCode.Created, (await Refund(staff, order, 5m)).StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await Refund(staff, order, 2.50m)).StatusCode);
        var mails = Sent(order, EcommerceEmailTemplates.OrderRefunded);
        Assert.Equal(2, mails.Count);
        EmailContract.AssertMatchesDescriptor(factory, mails[0]);
        Assert.Equal(("5.00", "5.00"), (mails[0].Variables["amount"], mails[0].Variables["totalRefunded"]));
        Assert.Equal(("2.50", "7.50", order.Total.ToString("N2", System.Globalization.CultureInfo.InvariantCulture)), (mails[1].Variables["amount"], mails[1].Variables["totalRefunded"], mails[1].Variables["orderTotal"]));

        // A refused refund (more than was paid) tells no one anything.
        Assert.Equal(HttpStatusCode.Conflict, (await Refund(staff, order, 10_000m)).StatusCode);
        Assert.Equal(2, Sent(order, EcommerceEmailTemplates.OrderRefunded).Count);
    }

    [Fact]
    public async Task A_refund_made_through_the_provider_is_emailed_once_and_its_webhook_replay_does_not_email_again()
    {
        var (staff, _, order) = await NewOrder(1, 20m);
        var payment = await Read<PaymentDto>(await Pay(staff, order, order.Total, "testpay"));

        var refund = await Read<RefundResultDto>(await staff.PostAsJsonAsync($"/api/v1/orders/{order.Id}/payments/{payment.Id}/refund", new RefundStartInput { Amount = 4m, IdempotencyKey = Tag() }, Json));
        Assert.Single(Sent(order, EcommerceEmailTemplates.OrderRefunded));

        var hook = JsonSerializer.SerializeToUtf8Bytes(new TestGateway.WireEvent("refund", order.Id, 4m, order.Currency, refund.Reference), new JsonSerializerOptions(JsonSerializerDefaults.Web));
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/payments/webhooks/testpay") { Content = new ByteArrayContent(hook) };
        request.Content.Headers.ContentType = new("application/json");
        request.Headers.Add("X-Test-Signature", TestGateway.Sign(hook));
        Assert.Equal(1, (await Read<WebhookResultDto>(await factory.CreateClient().SendAsync(request))).AlreadyRecorded);
        Assert.Single(Sent(order, EcommerceEmailTemplates.OrderRefunded));
    }

    [Fact]
    public async Task Staff_cannot_record_a_voucher_refund_so_no_card_refund_email_can_come_from_one()
    {
        var (staff, _, order) = await NewOrder(1, 20m);
        await Pay(staff, order, order.Total);
        // "voucher" is reserved for the system (cancelling credits it directly, bypassing the email), so staff cannot record it.
        Assert.Equal(HttpStatusCode.BadRequest, (await staff.PostAsJsonAsync($"/api/v1/orders/{order.Id}/refunds", new PaymentInput { Amount = 1m, Provider = "voucher", Reference = "x" })).StatusCode);
        Assert.Empty(Sent(order, EcommerceEmailTemplates.OrderRefunded));
    }

    [Fact]
    public async Task A_broken_mail_server_never_fails_or_undoes_the_change()
    {
        var (staff, _, order) = await NewOrder(2, 20m);
        factory.Email.Fail = true;
        try
        {
            Assert.Equal(HttpStatusCode.OK, (await Stage(staff, order, OrderStage.Processing)).StatusCode);
            Assert.Equal(HttpStatusCode.Created, (await Pay(staff, order, order.Total)).StatusCode);
            Assert.Equal(HttpStatusCode.Created, (await Ship(staff, order, new ShipmentInput { Lines = [new FulfilmentLineInput { OrderLineId = (await Read<OrderDto>(await staff.GetAsync($"/api/v1/orders/{order.Id}"))).Lines[0].Id, Quantity = 1 }] })).StatusCode);
            Assert.Equal(HttpStatusCode.Created, (await Refund(staff, order, 3m)).StatusCode);
        }
        finally { factory.Email.Fail = false; }

        var after = await Read<OrderDto>(await staff.GetAsync($"/api/v1/orders/{order.Id}"));
        Assert.Equal((1, 3m), (after.Lines[0].QuantityShipped, after.AmountRefunded)); // all of it happened

        var (_, _, other) = await NewOrder(1);
        factory.Email.Fail = true;
        try { Assert.Equal(HttpStatusCode.OK, (await Stage(staff, other, OrderStage.Cancelled)).StatusCode); }
        finally { factory.Email.Fail = false; }
        Assert.Equal(OrderStage.Cancelled, (await Read<OrderDto>(await staff.GetAsync($"/api/v1/orders/{other.Id}"))).Stage);
    }

    [Fact]
    public async Task The_shipped_cancelled_and_refunded_wording_renders_with_its_own_example_data()
    {
        var catalog = factory.Services.GetRequiredService<IEmailTemplateCatalog>();

        var shipped = catalog.Find("order.shipped")!;
        var d = SmtpTemplateDefaults.Find("order.shipped")!;
        Assert.Equal("Part of your order ORD-1001 is on its way", await SmtpTemplateRenderer.RenderAsync(d.Subject, shipped.Samples, html: false));
        var text = await SmtpTemplateRenderer.RenderAsync(d.TextBody, shipped.Samples, html: false);
        Assert.Contains("Part of your order ORD-1001 has been sent.", text);
        Assert.Contains("1 x Blue mug (MUG-BLU)", text);
        Assert.Contains("Carrier: Royal Mail", text);
        Assert.Contains("Tracking number: RM123456789GB", text);
        Assert.Contains("Track it here: https://track.example/RM123456789GB", text);
        Assert.Contains("Still to come:", text);
        Assert.Contains("2 x Teapot (POT-1)", text);

        var done = shipped.Samples.ToDictionary(kv => kv.Key, kv => kv.Value);
        done["complete"] = true; done["stillToSend"] = new List<object?>(); done["carrier"] = null; done["trackingNumber"] = null; done["trackingUrl"] = null;
        Assert.Equal("Your order ORD-1001 is on its way", await SmtpTemplateRenderer.RenderAsync(d.Subject, done, html: false));
        var finished = await SmtpTemplateRenderer.RenderAsync(d.TextBody, done, html: false);
        Assert.Contains("Your order ORD-1001 has been sent.", finished);
        Assert.DoesNotContain("Still to come", finished);
        Assert.DoesNotContain("Carrier", finished);
        Assert.DoesNotContain("Track it here", finished);

        var cancelled = catalog.Find("order.cancelled")!;
        var c = SmtpTemplateDefaults.Find("order.cancelled")!;
        var withRefund = await SmtpTemplateRenderer.RenderAsync(c.TextBody, cancelled.Samples, html: false);
        Assert.Contains("35.70 GBP that has not been returned yet", withRefund);
        var noRefund = cancelled.Samples.ToDictionary(kv => kv.Key, kv => kv.Value); noRefund["stillToRefund"] = null;
        Assert.DoesNotContain("not been returned", await SmtpTemplateRenderer.RenderAsync(c.TextBody, noRefund, html: false));

        var refunded = catalog.Find("order.refunded")!;
        var r = SmtpTemplateDefaults.Find("order.refunded")!;
        Assert.Contains("We have refunded 10.00 GBP on your order ORD-1001.", await SmtpTemplateRenderer.RenderAsync(r.TextBody, refunded.Samples, html: false));
    }
}
