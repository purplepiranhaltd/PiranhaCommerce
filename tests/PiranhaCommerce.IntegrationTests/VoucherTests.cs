using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using PiranhaCommerce.Catalog;
using PiranhaCommerce.Customers;
using PiranhaCommerce.Ecommerce;

namespace PiranhaCommerce.IntegrationTests;

/// <summary>Gift vouchers: a way of paying after tax. Store-wide settings are touched, so these run one at a time.</summary>
[Collection(OrderSettings.Name)]
public class VoucherTests(PiranhaCommerceFactory factory) : IClassFixture<PiranhaCommerceFactory>
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };

    private static string Tag() => Guid.NewGuid().ToString("N")[..8];
    private static async Task<T> Read<T>(HttpResponseMessage r) => (await r.Content.ReadFromJsonAsync<T>(Json))!;

    private static CheckoutInput Checkout() => new()
    {
        Email = $"buyer-{Tag()}@example.com",
        ShippingAddress = new AddressInput { Recipient = "Ada", Line1 = "1 High St", City = "Leeds", PostalCode = "LS1", CountryCode = "GB" },
    };

    private async Task<ProductDto> Product(HttpClient staff, decimal price)
    {
        var p = await Read<ProductDto>(await staff.PostAsJsonAsync("/api/v1/products", new ProductInput { Name = "V " + Tag(), Sku = "V-" + Tag(), Price = price }));
        Assert.Equal(HttpStatusCode.OK, (await staff.PutAsJsonAsync($"/api/v1/products/{p.Id}/listing", new ProductListingInput { Published = true })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await staff.PostAsJsonAsync($"/api/v1/products/{p.Id}/stock/adjustments", new StockAdjustmentInput { Delta = 50, Reason = "Delivery" })).StatusCode);
        return p;
    }

    private static async Task<BasketDto> Basket(HttpClient staff, ProductDto p, int qty = 1)
    {
        var b = await Read<BasketDto>(await staff.PostAsJsonAsync("/api/v1/baskets", new BasketCreateInput()));
        Assert.Equal(HttpStatusCode.OK, (await staff.PutAsJsonAsync($"/api/v1/baskets/{b.Id}/lines/{p.Id}", new BasketLineInput { Quantity = qty })).StatusCode);
        return b;
    }

    private static async Task<VoucherIssuedDto> Issue(HttpClient staff, decimal value)
    {
        var r = await staff.PostAsJsonAsync("/api/v1/vouchers", new VoucherIssueInput { Value = value });
        Assert.Equal(HttpStatusCode.Created, r.StatusCode);
        return await Read<VoucherIssuedDto>(r);
    }

    private static async Task<VoucherDetailDto> Detail(HttpClient staff, Guid id) => await Read<VoucherDetailDto>(await staff.GetAsync($"/api/v1/vouchers/{id}"));

    [Fact]
    public async Task A_voucher_pays_part_of_an_order_after_tax_and_the_ledger_matches_the_balance()
    {
        var staff = await factory.CreateAuthenticatedApiClientAsync();
        var product = await Product(staff, 10m);
        var voucher = await Issue(staff, 4m);
        var basket = await Basket(staff, product);
        Assert.Equal(HttpStatusCode.OK, (await staff.PostAsJsonAsync($"/api/v1/baskets/{basket.Id}/voucher", new CodeInput { Code = voucher.Code.ToLowerInvariant().Replace("-", " ") })).StatusCode);

        var quote = await Read<QuoteDto>(await staff.PostAsJsonAsync($"/api/v1/baskets/{basket.Id}/quote", new QuoteInput { CountryCode = "GB" }));
        Assert.Equal((4m, quote.Total - 4m), (quote.VoucherApplied, quote.AmountDue));

        var order = await Read<OrderDto>(await staff.PostAsJsonAsync($"/api/v1/baskets/{basket.Id}/checkout", Checkout()));
        Assert.Equal(quote.Total, order.Total); // the voucher changes neither price nor tax
        Assert.Equal(quote.TaxTotal, order.TaxTotal);
        Assert.Equal(4m, order.AmountPaid);

        var detail = await Detail(staff, voucher.Voucher.Id);
        Assert.Equal(0m, detail.Voucher.Balance);
        Assert.Equal(detail.Voucher.Balance, detail.Entries.Sum(e => e.Amount));
        Assert.Contains(detail.Entries, e => e.Kind == VoucherEntryKinds.Redeemed && e.OrderId == order.Id && e.Amount == -4m);
    }

    [Fact]
    public async Task A_voucher_worth_more_than_the_order_pays_it_in_full_and_keeps_the_rest()
    {
        var staff = await factory.CreateAuthenticatedApiClientAsync();
        var product = await Product(staff, 10m);
        var voucher = await Issue(staff, 1000m);
        var basket = await Basket(staff, product);
        await staff.PostAsJsonAsync($"/api/v1/baskets/{basket.Id}/voucher", new CodeInput { Code = voucher.Code });

        var order = await Read<OrderDto>(await staff.PostAsJsonAsync($"/api/v1/baskets/{basket.Id}/checkout", Checkout()));
        Assert.Equal(order.Total, order.AmountPaid);
        Assert.Equal(PaymentStatus.Paid, order.PaymentStatus);
        Assert.Equal(1000m - order.Total, (await Detail(staff, voucher.Voucher.Id)).Voucher.Balance);
    }

    [Fact]
    public async Task One_voucher_spent_by_simultaneous_orders_never_goes_below_zero()
    {
        var staff = await factory.CreateAuthenticatedApiClientAsync();
        var product = await Product(staff, 10m);
        var voucher = await Issue(staff, 25m);
        var baskets = new List<BasketDto>();
        for (var i = 0; i < 4; i++)
        {
            var b = await Basket(staff, product);
            Assert.Equal(HttpStatusCode.OK, (await staff.PostAsJsonAsync($"/api/v1/baskets/{b.Id}/voucher", new CodeInput { Code = voucher.Code })).StatusCode);
            baskets.Add(b);
        }

        var results = await Task.WhenAll(baskets.Select(b => staff.PostAsJsonAsync($"/api/v1/baskets/{b.Id}/checkout", Checkout())));
        var detail = await Detail(staff, voucher.Voucher.Id);

        Assert.True(detail.Voucher.Balance >= 0m);
        Assert.Equal(detail.Voucher.Balance, detail.Entries.Sum(e => e.Amount));
        // Whatever succeeded together spent exactly what left the voucher; the rest were refused whole.
        var paid = 0m;
        foreach (var ok in results.Where(r => r.StatusCode == HttpStatusCode.OK)) paid += (await Read<OrderDto>(ok)).AmountPaid;
        Assert.Equal(25m - detail.Voucher.Balance, paid);
        Assert.All(results, r => Assert.True(r.StatusCode is HttpStatusCode.OK or HttpStatusCode.Conflict));
    }

    [Fact]
    public async Task Every_bad_code_gets_the_same_answer_and_staff_cannot_record_a_voucher_payment_by_hand()
    {
        var staff = await factory.CreateAuthenticatedApiClientAsync();
        var product = await Product(staff, 10m);
        var basket = await Basket(staff, product);

        var off = await Issue(staff, 5m);
        Assert.Equal(HttpStatusCode.OK, (await staff.PutAsJsonAsync($"/api/v1/vouchers/{off.Voucher.Id}", new VoucherUpdateInput { Active = false })).StatusCode);
        var empty = await Issue(staff, 5m);
        Assert.Equal(HttpStatusCode.OK, (await staff.PostAsJsonAsync($"/api/v1/vouchers/{empty.Voucher.Id}/adjustments", new VoucherAdjustInput { Amount = -5m, Reason = "Test" })).StatusCode);

        var answers = new List<string>();
        foreach (var code in new[] { "AAAA-BBBB-CCCC-DDDD", off.Code, empty.Code })
        {
            var r = await staff.PostAsJsonAsync($"/api/v1/baskets/{basket.Id}/voucher", new CodeInput { Code = code });
            Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);
            answers.Add(await r.Content.ReadAsStringAsync());
        }
        Assert.Single(answers.Distinct());

        var order = await Read<OrderDto>(await staff.PostAsJsonAsync($"/api/v1/baskets/{basket.Id}/checkout", Checkout()));
        var pay = await staff.PostAsJsonAsync($"/api/v1/orders/{order.Id}/payments", new PaymentInput { Amount = 1m, Provider = "voucher" });
        Assert.Equal(HttpStatusCode.BadRequest, pay.StatusCode);
        var refund = await staff.PostAsJsonAsync($"/api/v1/orders/{order.Id}/refunds", new PaymentInput { Amount = 1m, Provider = "voucher" });
        Assert.Equal(HttpStatusCode.BadRequest, refund.StatusCode);
    }

    [Fact]
    public async Task The_code_is_shown_once_only_a_hash_is_kept_and_the_balance_cannot_be_adjusted_below_zero()
    {
        var staff = await factory.CreateAuthenticatedApiClientAsync();
        var voucher = await Issue(staff, 10m);
        Assert.Matches("^[A-Z2-9]{4}(-[A-Z2-9]{4}){3}$", voucher.Code);
        Assert.Equal(voucher.Code.Replace("-", "")[^4..], voucher.Voucher.CodeLast4);

        var body = await (await staff.GetAsync($"/api/v1/vouchers/{voucher.Voucher.Id}")).Content.ReadAsStringAsync();
        Assert.DoesNotContain(voucher.Code, body);
        Assert.DoesNotContain(voucher.Code.Replace("-", ""), body);

        Assert.Equal(HttpStatusCode.Conflict, (await staff.PostAsJsonAsync($"/api/v1/vouchers/{voucher.Voucher.Id}/adjustments", new VoucherAdjustInput { Amount = -10.01m, Reason = "Too much" })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await staff.PostAsJsonAsync("/api/v1/vouchers/lookup", new CodeInput { Code = voucher.Code })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await staff.PostAsJsonAsync("/api/v1/vouchers/lookup", new CodeInput { Code = "ZZZZ-ZZZZ-ZZZZ-ZZZZ" })).StatusCode);

        var (email, password) = await factory.CreateReadOnlyUserAsync();
        var reader = await factory.CreateAuthenticatedApiClientAsync(email, password);
        Assert.Equal(HttpStatusCode.Forbidden, (await reader.PostAsJsonAsync("/api/v1/vouchers", new VoucherIssueInput { Value = 5m })).StatusCode);
    }

    [Fact]
    public async Task Cancelling_an_order_returns_the_voucher_money_once_and_the_ledger_still_adds_up()
    {
        var staff = await factory.CreateAuthenticatedApiClientAsync();
        var product = await Product(staff, 10m);
        var voucher = await Issue(staff, 8m);
        var basket = await Basket(staff, product);
        await staff.PostAsJsonAsync($"/api/v1/baskets/{basket.Id}/voucher", new CodeInput { Code = voucher.Code });
        var order = await Read<OrderDto>(await staff.PostAsJsonAsync($"/api/v1/baskets/{basket.Id}/checkout", Checkout()));
        Assert.Equal(0m, (await Detail(staff, voucher.Voucher.Id)).Voucher.Balance);

        var cancel = new OrderStageInput { Stage = OrderStage.Cancelled };
        Assert.Equal(HttpStatusCode.OK, (await staff.PostAsJsonAsync($"/api/v1/orders/{order.Id}/stage", cancel, Json)).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await staff.PostAsJsonAsync($"/api/v1/orders/{order.Id}/stage", cancel, Json)).StatusCode);

        var detail = await Detail(staff, voucher.Voucher.Id);
        Assert.Equal(8m, detail.Voucher.Balance);
        Assert.Equal(detail.Voucher.Balance, detail.Entries.Sum(e => e.Amount));
        Assert.Contains(detail.Entries, e => e.Kind == VoucherEntryKinds.Returned && e.Amount == 8m);
    }

    private static Task<HttpResponseMessage> ToVoucher(HttpClient c, Guid orderId, decimal amount, string? note = null)
        => c.PostAsJsonAsync($"/api/v1/orders/{orderId}/refunds/voucher", new RefundToVoucherInput { Amount = amount, Note = note });

    [Fact]
    public async Task Staff_can_return_part_of_a_voucher_payment_to_the_voucher_never_more_than_it_paid_and_a_later_cancel_returns_only_the_rest()
    {
        var staff = await factory.CreateAuthenticatedApiClientAsync();
        var product = await Product(staff, 10m);
        var voucher = await Issue(staff, 8m);
        var basket = await Basket(staff, product);
        await staff.PostAsJsonAsync($"/api/v1/baskets/{basket.Id}/voucher", new CodeInput { Code = voucher.Code });
        var order = await Read<OrderDto>(await staff.PostAsJsonAsync($"/api/v1/baskets/{basket.Id}/checkout", Checkout()));
        Assert.Equal(8m, order.AmountPaid); // the voucher paid 8 of the total

        var emailsBefore = factory.Email.To(order.Email).Count;
        var first = await ToVoucher(staff, order.Id, 3m, "Goods came back");
        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        var payment = await Read<PaymentDto>(first);
        Assert.Equal((PaymentKind.Refund, "voucher", 3m), (payment.Kind, payment.Provider, payment.Amount));
        Assert.Contains("Goods came back", payment.Note);
        Assert.Equal(3m, (await Detail(staff, voucher.Voucher.Id)).Voucher.Balance);
        Assert.Equal(3m, (await Read<OrderDto>(await staff.GetAsync($"/api/v1/orders/{order.Id}"))).AmountRefunded);

        // Only 5 of the voucher's 8 is left to return.
        Assert.Equal(HttpStatusCode.Conflict, (await ToVoucher(staff, order.Id, 5.01m)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await ToVoucher(staff, order.Id, 0m)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await ToVoucher(staff, order.Id, 1.005m)).StatusCode);
        Assert.Equal(3m, (await Detail(staff, voucher.Voucher.Id)).Voucher.Balance);

        // Cancelling now returns only what the voucher has not already had back.
        Assert.Equal(HttpStatusCode.OK, (await staff.PostAsJsonAsync($"/api/v1/orders/{order.Id}/stage", new OrderStageInput { Stage = OrderStage.Cancelled }, Json)).StatusCode);
        var detail = await Detail(staff, voucher.Voucher.Id);
        Assert.Equal(8m, detail.Voucher.Balance);
        Assert.Equal(detail.Voucher.Balance, detail.Entries.Sum(e => e.Amount));
        Assert.Equal(2, detail.Entries.Count(e => e.Kind == VoucherEntryKinds.Returned));
        Assert.Equal(HttpStatusCode.Conflict, (await ToVoucher(staff, order.Id, 0.01m)).StatusCode); // nothing left to return

        Assert.DoesNotContain(factory.Email.To(order.Email).Skip(emailsBefore), m => m.TemplateKey == "order.refunded"); // credit on a voucher is not announced as a refund
    }

    [Fact]
    public async Task Returning_to_a_voucher_needs_an_order_that_was_paid_with_one_and_a_writer_and_runs_one_at_a_time()
    {
        var staff = await factory.CreateAuthenticatedApiClientAsync();
        var product = await Product(staff, 10m);

        var plain = await Read<OrderDto>(await staff.PostAsJsonAsync($"/api/v1/baskets/{(await Basket(staff, product)).Id}/checkout", Checkout()));
        await staff.PostAsJsonAsync($"/api/v1/orders/{plain.Id}/payments", new PaymentInput { Amount = plain.Total, Provider = "manual", Reference = "p-" + Tag() });
        var notPaidByVoucher = await ToVoucher(staff, plain.Id, 1m);
        Assert.Equal(HttpStatusCode.Conflict, notPaidByVoucher.StatusCode);
        Assert.Contains("not paid with a gift voucher", await notPaidByVoucher.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.NotFound, (await ToVoucher(staff, Guid.NewGuid(), 1m)).StatusCode);

        var voucher = await Issue(staff, 8m);
        var basket = await Basket(staff, product);
        await staff.PostAsJsonAsync($"/api/v1/baskets/{basket.Id}/voucher", new CodeInput { Code = voucher.Code });
        var order = await Read<OrderDto>(await staff.PostAsJsonAsync($"/api/v1/baskets/{basket.Id}/checkout", Checkout()));

        var (email, password) = await factory.CreateReadOnlyUserAsync();
        var reader = await factory.CreateAuthenticatedApiClientAsync(email, password);
        Assert.Equal(HttpStatusCode.Forbidden, (await ToVoucher(reader, order.Id, 1m)).StatusCode);

        // Five at once for 2 each: room for four of them (8), so exactly four succeed and the ledger still adds up.
        var results = await Task.WhenAll(Enumerable.Range(0, 5).Select(_ => ToVoucher(staff, order.Id, 2m)));
        Assert.Equal(4, results.Count(r => r.StatusCode == HttpStatusCode.Created));
        Assert.Equal(1, results.Count(r => r.StatusCode == HttpStatusCode.Conflict));
        var detail = await Detail(staff, voucher.Voucher.Id);
        Assert.Equal(8m, detail.Voucher.Balance);
        Assert.Equal(detail.Voucher.Balance, detail.Entries.Sum(e => e.Amount));
    }
}
