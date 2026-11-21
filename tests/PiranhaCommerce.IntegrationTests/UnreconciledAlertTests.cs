using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using PiranhaCommerce.Catalog;
using PiranhaCommerce.Customers;
using PiranhaCommerce.Ecommerce;

namespace PiranhaCommerce.IntegrationTests;

public sealed class AlertFactory : PiranhaFactory
{
    protected override string? AlertEmail => "alerts@example.com, Second@Example.com ,alerts@example.com";
}

/// <summary>The shop is emailed when money cannot be booked, once per item, and never at the cost of the webhook.</summary>
[Collection(OrderSettings.Name)]
public class UnreconciledAlertTests(AlertFactory factory) : IClassFixture<AlertFactory>
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };
    private static string Tag() => Guid.NewGuid().ToString("N")[..8];

    private static async Task<OrderDto> PlaceOrder(HttpClient staff)
    {
        var p = (await (await staff.PostAsJsonAsync("/api/v1/products", new ProductInput { Name = "A " + Tag(), Sku = "A-" + Tag(), Price = 10m })).Content.ReadFromJsonAsync<ProductDto>(Json))!;
        await staff.PutAsJsonAsync($"/api/v1/products/{p.Id}/listing", new ProductListingInput { Published = true });
        await staff.PostAsJsonAsync($"/api/v1/products/{p.Id}/stock/adjustments", new StockAdjustmentInput { Delta = 10, Reason = "Delivery" });
        var b = (await (await staff.PostAsJsonAsync("/api/v1/baskets", new BasketCreateInput())).Content.ReadFromJsonAsync<BasketDto>(Json))!;
        await staff.PutAsJsonAsync($"/api/v1/baskets/{b.Id}/lines/{p.Id}", new BasketLineInput { Quantity = 1 });
        return (await (await staff.PostAsJsonAsync($"/api/v1/baskets/{b.Id}/checkout", new CheckoutInput
        {
            Email = $"a-{Tag()}@example.com",
            ShippingAddress = new AddressInput { Recipient = "Ada", Line1 = "1 High St", City = "Leeds", PostalCode = "LS1", CountryCode = "GB" },
        })).Content.ReadFromJsonAsync<OrderDto>(Json))!;
    }

    private async Task<HttpResponseMessage> Hook(TestGateway.WireEvent e)
    {
        var body = JsonSerializer.SerializeToUtf8Bytes(e, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/payments/webhooks/testpay") { Content = new ByteArrayContent(body) };
        request.Content.Headers.ContentType = new("application/json");
        request.Headers.Add("X-Test-Signature", TestGateway.Sign(body));
        return await factory.CreateClient().SendAsync(request);
    }

    private static string Alert(PiranhaCommerce.Core.Email.EmailMessage m, string key) => m.Variables[key]?.ToString() ?? "";

    [Fact]
    public async Task Money_that_cannot_be_booked_emails_each_alert_address_once_and_repeats_do_not_email_again()
    {
        var staff = await factory.CreateAuthenticatedApiClientAsync();
        var order = await PlaceOrder(staff);
        var reference = "ch_" + Tag();
        var over = new TestGateway.WireEvent("payment", order.Id, order.Total + 5m, order.Currency, reference);

        for (var i = 0; i < 3; i++) Assert.Equal(System.Net.HttpStatusCode.OK, (await Hook(over)).StatusCode); // the provider retries

        var first = Assert.Single(factory.Email.To("alerts@example.com"), m => Alert(m, "providerReference") == reference);
        Assert.Single(factory.Email.To("second@example.com"), m => Alert(m, "providerReference") == reference); // trimmed, any case, listed once
        Assert.Equal(("alert.unreconciled-payment", "testpay", "PaymentConfirmed", order.Reference, "15.00", "GBP"),
            (first.TemplateKey, Alert(first, "provider"), Alert(first, "kind"), Alert(first, "orderReference"), Alert(first, "amount"), Alert(first, "currency")));
        Assert.Contains("more than is owed", Alert(first, "reason"));
        EmailContract.AssertMatchesDescriptor(factory, first);
    }

    [Fact]
    public async Task An_order_that_does_not_exist_is_still_alerted_with_no_order_reference_and_a_mail_failure_never_fails_the_webhook()
    {
        var reference = "ch_" + Tag();
        var unknown = new TestGateway.WireEvent("payment", Guid.NewGuid(), 5m, "GBP", reference);
        Assert.Equal(System.Net.HttpStatusCode.OK, (await Hook(unknown)).StatusCode);
        var m = Assert.Single(factory.Email.To("alerts@example.com"), x => Alert(x, "providerReference") == reference);
        Assert.Equal("", Alert(m, "orderReference"));
        EmailContract.AssertMatchesDescriptor(factory, m);

        var other = "ch_" + Tag();
        factory.Email.Fail = true;
        try
        {
            var response = await Hook(new TestGateway.WireEvent("payment", Guid.NewGuid(), 5m, "GBP", other));
            Assert.Equal(System.Net.HttpStatusCode.OK, response.StatusCode); // answered normally, so the provider does not retry for days
        }
        finally { factory.Email.Fail = false; }
        Assert.DoesNotContain(factory.Email.To("alerts@example.com"), x => Alert(x, "providerReference") == other);
        var staff = await factory.CreateAuthenticatedApiClientAsync();
        var list = await staff.GetFromJsonAsync<JsonElement>("/api/v1/unreconciled-payments?pageSize=100&resolved=false");
        Assert.Contains(list.GetProperty("items").EnumerateArray(), i => i.GetProperty("reference").GetString() == other); // still on the list
    }
}
