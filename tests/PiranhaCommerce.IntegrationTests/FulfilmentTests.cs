using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using PiranhaCommerce.Catalog;
using PiranhaCommerce.Core.Paging;
using PiranhaCommerce.Customers;
using PiranhaCommerce.Ecommerce;

namespace PiranhaCommerce.IntegrationTests;

/// <summary>Sending parcels, taking returns back, and refunding through the payment provider.</summary>
[Collection(OrderSettings.Name)]
public class FulfilmentTests(PiranhaCommerceFactory factory) : IClassFixture<PiranhaCommerceFactory>
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };
    private static string Tag() => Guid.NewGuid().ToString("N")[..10];
    private static async Task<T> Read<T>(HttpResponseMessage r) => (await r.Content.ReadFromJsonAsync<T>(Json))!;

    private static async Task<ProductDto> NewProduct(HttpClient c, decimal price = 10m, int stock = 20)
    {
        var p = await Read<ProductDto>(await c.PostAsJsonAsync("/api/v1/products", new ProductInput { Name = "Ful " + Tag(), Sku = "FUL-" + Tag(), Price = price }));
        Assert.Equal(HttpStatusCode.OK, (await c.PutAsJsonAsync($"/api/v1/products/{p.Id}/listing", new ProductListingInput { Published = true })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await c.PostAsJsonAsync($"/api/v1/products/{p.Id}/stock/adjustments", new StockAdjustmentInput { Delta = stock, Reason = "Delivery" })).StatusCode);
        return p;
    }

    private static async Task<OrderDto> Place(HttpClient c, params (ProductDto Product, int Quantity)[] lines)
    {
        var basket = await Read<BasketDto>(await c.PostAsJsonAsync("/api/v1/baskets", new BasketCreateInput()));
        foreach (var (product, quantity) in lines)
            Assert.Equal(HttpStatusCode.OK, (await c.PutAsJsonAsync($"/api/v1/baskets/{basket.Id}/lines/{product.Id}", new BasketLineInput { Quantity = quantity })).StatusCode);
        var r = await c.PostAsJsonAsync($"/api/v1/baskets/{basket.Id}/checkout", new CheckoutInput
        {
            Email = $"ful-{Tag()}@example.com",
            ShippingAddress = new AddressInput { Recipient = "Ada Buyer", Line1 = "1 High St", City = "Leeds", PostalCode = "LS1 1AA", CountryCode = "GB" },
        });
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        return await Read<OrderDto>(r);
    }

    private static Task<HttpResponseMessage> Stage(HttpClient c, OrderDto o, OrderStage to) => c.PostAsJsonAsync($"/api/v1/orders/{o.Id}/stage", new OrderStageInput { Stage = to }, Json);
    private static async Task<OrderDto> Get(HttpClient c, OrderDto o) => await Read<OrderDto>(await c.GetAsync($"/api/v1/orders/{o.Id}"));
    private static async Task<decimal> Stock(HttpClient c, ProductDto p) => (await c.GetFromJsonAsync<StockLevelDto>($"/api/v1/products/{p.Id}/stock"))!.Quantity;

    private static Task<HttpResponseMessage> Ship(HttpClient c, OrderDto o, ShipmentInput? input = null) => c.PostAsJsonAsync($"/api/v1/orders/{o.Id}/shipments", input ?? new ShipmentInput(), Json);
    private static Task<HttpResponseMessage> Return(HttpClient c, OrderDto o, ReturnInput input) => c.PostAsJsonAsync($"/api/v1/orders/{o.Id}/returns", input, Json);

    private static ReturnInput ReturnOf(OrderDto o, int quantity, bool restock = true, bool shipping = false, string reason = "Not wanted") => new()
    {
        Reason = reason, IncludeShipping = shipping, Lines = [new ReturnLineInput { OrderLineId = o.Lines[0].Id, Quantity = quantity, Restock = restock }],
    };

    private static ShipmentInput ShipOf(OrderDto o, int quantity, string? carrier = null, string? tracking = null, string? url = null) => new()
    {
        Carrier = carrier, TrackingNumber = tracking, TrackingUrl = url, Lines = [new FulfilmentLineInput { OrderLineId = o.Lines[0].Id, Quantity = quantity }],
    };

    private async Task<(HttpClient Staff, ProductDto Product, OrderDto Order)> ProcessingOrder(int quantity, decimal price = 10m, int stock = 20)
    {
        var staff = await factory.CreateAuthenticatedApiClientAsync();
        var product = await NewProduct(staff, price, stock);
        var order = await Place(staff, (product, quantity));
        Assert.Equal(HttpStatusCode.OK, (await Stage(staff, order, OrderStage.Processing)).StatusCode);
        return (staff, product, await Get(staff, order));
    }

    // ---- shipping ---------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Parcels_are_recorded_and_the_order_completes_when_the_last_item_has_gone_without_touching_stock()
    {
        var (staff, product, order) = await ProcessingOrder(4);
        var before = await Stock(staff, product);

        var first = await Ship(staff, order, ShipOf(order, 1, "Royal Mail", "RM123", "https://track.example/RM123"));
        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        var shipment = await Read<ShipmentDto>(first);
        Assert.Equal(("Royal Mail", "RM123", "admin@piranha.local"), (shipment.Carrier, shipment.TrackingNumber, shipment.Actor));
        Assert.Equal(1, Assert.Single(shipment.Lines).Quantity);

        var mid = await Get(staff, order);
        Assert.Equal(OrderStage.Processing, mid.Stage);
        Assert.Equal(1, mid.Lines[0].QuantityShipped);
        Assert.DoesNotContain(OrderStage.Cancelled, mid.NextStages); // something has gone: it can no longer be cancelled

        var last = await Ship(staff, order, ShipOf(order, 3));
        Assert.Equal(HttpStatusCode.Created, last.StatusCode);
        var done = await Get(staff, order);
        Assert.Equal(OrderStage.Completed, done.Stage);
        Assert.Equal(4, done.Lines[0].QuantityShipped);
        Assert.Equal(2, done.Shipments!.Count);
        Assert.Equal(before, await Stock(staff, product)); // stock was taken when the order was placed, and is not taken again

        var listed = await Read<List<ShipmentDto>>(await staff.GetAsync($"/api/v1/orders/{order.Id}/shipments"));
        Assert.Equal([1, 3], listed.Select(s => s.Lines.Single().Quantity));
    }

    [Fact]
    public async Task Leaving_the_lines_out_sends_everything_that_is_left()
    {
        var staff = await factory.CreateAuthenticatedApiClientAsync();
        var a = await NewProduct(staff);
        var b = await NewProduct(staff);
        var order = await Place(staff, (a, 2), (b, 3));
        await Stage(staff, order, OrderStage.Processing);
        order = await Get(staff, order);
        var twoLine = order.Lines.Single(l => l.Quantity == 2); // lines are listed by product name, which is random here, so find it by quantity
        await Ship(staff, order, new ShipmentInput { Lines = [new FulfilmentLineInput { OrderLineId = twoLine.Id, Quantity = 1 }] }); // one of it has gone already

        var rest = await Read<ShipmentDto>(await Ship(staff, order));
        Assert.Equal([1, 3], rest.Lines.Select(l => l.Quantity).Order());
        Assert.Equal(OrderStage.Completed, (await Get(staff, order)).Stage);
        Assert.Equal(HttpStatusCode.Conflict, (await Ship(staff, order)).StatusCode); // nothing left, and no longer processing
    }

    [Fact]
    public async Task Only_a_processing_order_can_ship_and_more_than_was_bought_or_a_wrong_line_is_refused()
    {
        var staff = await factory.CreateAuthenticatedApiClientAsync();
        var product = await NewProduct(staff);
        var order = await Place(staff, (product, 2));
        Assert.Equal(HttpStatusCode.Conflict, (await Ship(staff, order)).StatusCode); // still Placed

        await Stage(staff, order, OrderStage.Processing);
        Assert.Equal(HttpStatusCode.Conflict, (await Ship(staff, order, ShipOf(order, 3))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await Ship(staff, order, new ShipmentInput { Lines = [new FulfilmentLineInput { OrderLineId = Guid.NewGuid(), Quantity = 1 }] })).StatusCode);
        foreach (var q in new[] { 0, -1, int.MinValue }) // a negative quantity would un-send goods
            Assert.Equal(HttpStatusCode.BadRequest, (await Ship(staff, order, new ShipmentInput { Lines = [new FulfilmentLineInput { OrderLineId = order.Lines[0].Id, Quantity = q }] })).StatusCode);
        var twice = new ShipmentInput { Lines = [new FulfilmentLineInput { OrderLineId = order.Lines[0].Id, Quantity = 1 }, new FulfilmentLineInput { OrderLineId = order.Lines[0].Id, Quantity = 1 }] };
        Assert.Equal(HttpStatusCode.BadRequest, (await Ship(staff, order, twice)).StatusCode);
        foreach (var bad in new[] { "javascript:alert(1)", "ftp://x.example/1", "not a link", "data:text/html,<script>" })
            Assert.Equal(HttpStatusCode.BadRequest, (await Ship(staff, order, ShipOf(order, 1, url: bad))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await staff.PostAsJsonAsync($"/api/v1/orders/{Guid.NewGuid()}/shipments", new ShipmentInput(), Json)).StatusCode);

        Assert.Empty((await Get(staff, order)).Shipments!); // none of the refused ones were recorded
        Assert.Equal(0, (await Get(staff, order)).Lines[0].QuantityShipped);

        await Stage(staff, order, OrderStage.Cancelled);
        Assert.Equal(HttpStatusCode.Conflict, (await Ship(staff, order)).StatusCode); // cancelled
    }

    [Fact]
    public async Task Simultaneous_parcels_can_never_send_more_than_was_bought()
    {
        var (staff, product, order) = await ProcessingOrder(3);

        var results = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => Ship(staff, order, ShipOf(order, 1))));
        Assert.Equal(3, results.Count(r => r.StatusCode == HttpStatusCode.Created));
        Assert.Equal(5, results.Count(r => r.StatusCode == HttpStatusCode.Conflict));

        var done = await Get(staff, order);
        Assert.Equal(3, done.Lines[0].QuantityShipped);
        Assert.Equal(3, done.Shipments!.Count);
        Assert.Equal(OrderStage.Completed, done.Stage);
    }

    [Fact]
    public async Task Cancelling_is_refused_once_anything_has_been_sent_and_a_cancel_racing_a_parcel_leaves_one_consistent_result()
    {
        var (staff, product, order) = await ProcessingOrder(2);
        await Ship(staff, order, ShipOf(order, 1));
        var refused = await Stage(staff, order, OrderStage.Cancelled);
        Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
        Assert.Contains("already been sent", await refused.Content.ReadAsStringAsync());
        Assert.Equal(OrderStage.Processing, (await Get(staff, order)).Stage);

        // A cancel and a parcel at the same moment: either the cancel wins (nothing sent, stock back) or the parcel does (cancel refused).
        for (var i = 0; i < 4; i++)
        {
            var (_, p, o) = await ProcessingOrder(2);
            var shelf = await Stock(staff, p); // after the order took its stock
            var results = await Task.WhenAll(Stage(staff, o, OrderStage.Cancelled), Ship(staff, o, ShipOf(o, 1)));
            var after = await Get(staff, o);
            if (after.Stage == OrderStage.Cancelled)
            {
                Assert.Empty(after.Shipments!);
                Assert.Equal(shelf + 2, await Stock(staff, p));
            }
            else
            {
                Assert.Single(after.Shipments!);
                Assert.Equal(shelf, await Stock(staff, p));
                Assert.Equal(HttpStatusCode.Conflict, results[0].StatusCode);
            }
        }
    }

    [Fact]
    public async Task Completing_by_hand_records_what_was_not_yet_sent_as_one_shipment_so_the_order_can_take_returns()
    {
        var (staff, product, order) = await ProcessingOrder(3);
        await Ship(staff, order, ShipOf(order, 1));

        var done = await Read<OrderDto>(await Stage(staff, order, OrderStage.Completed));
        Assert.Equal(3, done.Lines[0].QuantityShipped);
        Assert.Equal([1, 2], done.Shipments!.Select(s => s.Lines.Single().Quantity));

        Assert.Equal(HttpStatusCode.Created, (await Return(staff, done, ReturnOf(done, 2))).StatusCode);
    }

    // ---- returns ----------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task A_return_puts_chosen_goods_back_on_the_shelf_once_and_suggests_what_to_refund()
    {
        var (staff, product, order) = await ProcessingOrder(3);
        await Ship(staff, order); // everything gone: the order is completed, and still accepts returns
        order = await Get(staff, order);
        var shelf = await Stock(staff, product);

        var first = await Return(staff, order, ReturnOf(order, 1, shipping: true, reason: "Too big"));
        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        var r1 = await Read<ReturnDto>(first);
        var l = order.Lines[0];
        Assert.Equal(RefundSuggestion.ForLine(l.LineTotal, l.DiscountAmount, l.TaxAmount, l.Quantity, 0, 1) + order.ShippingTotal + order.ShippingTax, r1.SuggestedRefund);
        Assert.True(r1.ShippingIncluded);
        Assert.Equal((true, "Too big", "admin@piranha.local"), (r1.Lines.Single().Restocked, r1.Reason, r1.Actor));
        Assert.Equal(shelf + 1, await Stock(staff, product));

        // A damaged one is recorded but not put back on the shelf.
        var second = await Read<ReturnDto>(await Return(staff, order, ReturnOf(order, 1, restock: false, reason: "Broken")));
        Assert.False(second.Lines.Single().Restocked);
        Assert.False(second.ShippingIncluded);
        Assert.Equal(RefundSuggestion.ForLine(l.LineTotal, l.DiscountAmount, l.TaxAmount, l.Quantity, 1, 1), second.SuggestedRefund);
        Assert.Equal(shelf + 1, await Stock(staff, product));

        var moves = await Read<PagedResult<StockMovementDto>>(await staff.GetAsync($"/api/v1/products/{product.Id}/stock/movements"));
        Assert.Equal(1, moves.Items.Count(m => m.Reason == "Returned" && m.Reference == order.Reference));
        Assert.Equal(2, (await Get(staff, order)).Lines[0].QuantityReturned);

        var listed = await Read<List<ReturnDto>>(await staff.GetAsync($"/api/v1/orders/{order.Id}/returns"));
        Assert.Equal(["Too big", "Broken"], listed.Select(r => r.Reason));
    }

    [Fact]
    public async Task Only_what_was_sent_and_not_already_returned_can_come_back()
    {
        var (staff, product, order) = await ProcessingOrder(3);
        var shelf = await Stock(staff, product);
        Assert.Equal(HttpStatusCode.Conflict, (await Return(staff, order, ReturnOf(order, 1))).StatusCode); // nothing has been sent yet

        await Ship(staff, order, ShipOf(order, 2));
        Assert.Equal(HttpStatusCode.Conflict, (await Return(staff, order, ReturnOf(order, 3))).StatusCode); // 3 bought, only 2 sent
        Assert.Equal(HttpStatusCode.Created, (await Return(staff, order, ReturnOf(order, 2))).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await Return(staff, order, ReturnOf(order, 1))).StatusCode); // both already back

        Assert.Equal(HttpStatusCode.BadRequest, (await Return(staff, order, new ReturnInput { Reason = "x", Lines = [] })).StatusCode);
        foreach (var q in new[] { 0, -1, int.MinValue })
            Assert.Equal(HttpStatusCode.BadRequest, (await Return(staff, order, new ReturnInput { Reason = "x", Lines = [new ReturnLineInput { OrderLineId = order.Lines[0].Id, Quantity = q }] })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await Return(staff, order, new ReturnInput { Reason = "", Lines = [new ReturnLineInput { OrderLineId = order.Lines[0].Id, Quantity = 1 }] })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await Return(staff, order, new ReturnInput { Reason = "x", Lines = [new ReturnLineInput { OrderLineId = Guid.NewGuid(), Quantity = 1 }] })).StatusCode);
        var twice = new ReturnInput { Reason = "x", Lines = [new ReturnLineInput { OrderLineId = order.Lines[0].Id, Quantity = 1 }, new ReturnLineInput { OrderLineId = order.Lines[0].Id, Quantity = 1 }] };
        Assert.Equal(HttpStatusCode.BadRequest, (await Return(staff, order, twice)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await staff.PostAsJsonAsync($"/api/v1/orders/{Guid.NewGuid()}/returns", ReturnOf(order, 1), Json)).StatusCode);

        Assert.Equal(shelf + 2, await Stock(staff, product)); // only the one good return changed the shelf, and only once
        Assert.Single(await Read<List<ReturnDto>>(await staff.GetAsync($"/api/v1/orders/{order.Id}/returns")));
    }

    [Fact]
    public async Task The_delivery_charge_can_be_part_of_only_one_return()
    {
        var (staff, product, order) = await ProcessingOrder(2);
        await Ship(staff, order);
        Assert.Equal(HttpStatusCode.Created, (await Return(staff, order, ReturnOf(order, 1, shipping: true))).StatusCode);
        var again = await Return(staff, order, ReturnOf(order, 1, shipping: true));
        Assert.Equal(HttpStatusCode.Conflict, again.StatusCode);
        Assert.Equal(1, (await Get(staff, order)).Lines[0].QuantityReturned); // the refused return changed nothing
        Assert.Equal(HttpStatusCode.Created, (await Return(staff, order, ReturnOf(order, 1))).StatusCode);
    }

    [Fact]
    public async Task Simultaneous_returns_can_never_return_more_than_was_sent_and_restock_exactly_once_each()
    {
        var (staff, product, order) = await ProcessingOrder(3);
        await Ship(staff, order);
        var shelf = await Stock(staff, product);

        var results = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => Return(staff, order, ReturnOf(order, 1))));
        Assert.Equal(3, results.Count(r => r.StatusCode == HttpStatusCode.Created));
        Assert.Equal(5, results.Count(r => r.StatusCode == HttpStatusCode.Conflict));
        Assert.Equal(shelf + 3, await Stock(staff, product));
        Assert.Equal(3, (await Get(staff, order)).Lines[0].QuantityReturned);
    }

    [Fact]
    public void Returning_a_line_in_pieces_refunds_it_exactly_with_no_penny_lost_to_rounding()
    {
        // 10.00 split three ways: 3.33 + 3.34 + 3.33, however the pieces are taken.
        var parts = new[] { 1, 2, 3 }.Select(already => RefundSuggestion.ForLine(10m, 0m, 0m, 3, already - 1, 1)).ToList();
        Assert.Equal([3.33m, 3.34m, 3.33m], parts);
        Assert.Equal(10m, parts.Sum());

        // With a discount and tax: net = 100 - 7 + 18.60 = 111.60, every way of splitting adds back to it.
        foreach (var split in new[] { new[] { 1, 1, 1, 1, 1, 1, 1 }, new[] { 3, 4 }, new[] { 7 }, new[] { 2, 2, 3 } })
        {
            decimal total = 0; var returned = 0;
            foreach (var n in split) { total += RefundSuggestion.ForLine(100m, 7m, 18.60m, 7, returned, n); returned += n; }
            Assert.Equal(111.60m, total);
        }
        Assert.Equal(0m, RefundSuggestion.ForLine(10m, 0m, 0m, 3, 0, 0));
    }

    // ---- refunding through the payment provider ---------------------------------------------------------------------------------

    private static async Task<(PaymentDto Payment, OrderDto Order)> PaidViaTestpay(HttpClient staff, OrderDto order, decimal? amount = null)
    {
        var r = await staff.PostAsJsonAsync($"/api/v1/orders/{order.Id}/payments", new PaymentInput { Amount = amount ?? order.Total, Provider = "testpay", Reference = "tp_" + Tag() });
        Assert.Equal(HttpStatusCode.Created, r.StatusCode);
        return (await Read<PaymentDto>(r), order);
    }

    private static Task<HttpResponseMessage> RefundVia(HttpClient c, OrderDto order, PaymentDto payment, decimal amount, string? key = null) =>
        c.PostAsJsonAsync($"/api/v1/orders/{order.Id}/payments/{payment.Id}/refund", new RefundStartInput { Amount = amount, IdempotencyKey = key }, Json);

    private static async Task<List<PaymentDto>> Ledger(HttpClient c, OrderDto order) => await Read<List<PaymentDto>>(await c.GetAsync($"/api/v1/orders/{order.Id}/payments"));

    [Fact]
    public async Task A_refund_started_through_the_provider_is_booked_once_even_if_repeated_or_replayed_by_its_webhook()
    {
        var staff = await factory.CreateAuthenticatedApiClientAsync();
        var order = await Place(staff, (await NewProduct(staff, 20m), 1));
        var (payment, _) = await PaidViaTestpay(staff, order);

        var key = Tag();
        var first = await RefundVia(staff, order, payment, 4m, key);
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        var result = await Read<RefundResultDto>(first);
        Assert.Equal(("testpay", "Recorded", 4m), (result.Provider, result.Outcome, result.Amount));

        var booked = (await Ledger(staff, order)).Single(p => p.Kind == PaymentKind.Refund);
        Assert.Equal((result.Reference, "testpay", 4m, "admin@piranha.local"), (booked.Reference, booked.Provider, booked.Amount, booked.Actor));
        Assert.Equal(4m, (await Get(staff, order)).AmountRefunded);

        // The same request again (a double click) is the same refund: nothing more is booked.
        var repeat = await Read<RefundResultDto>(await RefundVia(staff, order, payment, 4m, key));
        Assert.Equal((result.Reference, "AlreadyRecorded"), (repeat.Reference, repeat.Outcome));
        Assert.Single(await Ledger(staff, order), p => p.Kind == PaymentKind.Refund);

        // The provider's own webhook for the same refund arrives afterwards and is recognised too.
        var hook = new TestGateway.WireEvent("refund", order.Id, 4m, order.Currency, result.Reference);
        var body = JsonSerializer.SerializeToUtf8Bytes(hook, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/payments/webhooks/testpay") { Content = new ByteArrayContent(body) };
        request.Content.Headers.ContentType = new("application/json");
        request.Headers.Add("X-Test-Signature", TestGateway.Sign(body));
        var replay = await Read<WebhookResultDto>(await factory.CreateClient().SendAsync(request));
        Assert.Equal((0, 1), (replay.Recorded, replay.AlreadyRecorded));
        Assert.Single(await Ledger(staff, order), p => p.Kind == PaymentKind.Refund);

        // A new key is a new refund.
        Assert.Equal("Recorded", (await Read<RefundResultDto>(await RefundVia(staff, order, payment, 1m, Tag()))).Outcome);
        Assert.Equal(5m, (await Get(staff, order)).AmountRefunded);
    }

    [Fact]
    public async Task A_provider_refund_cannot_exceed_what_that_provider_took_and_hides_the_providers_own_error()
    {
        var staff = await factory.CreateAuthenticatedApiClientAsync();
        var order = await Place(staff, (await NewProduct(staff, 20m), 1));
        var (payment, _) = await PaidViaTestpay(staff, order, 15m);

        Assert.Equal(HttpStatusCode.Conflict, (await RefundVia(staff, order, payment, 15.01m)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await RefundVia(staff, order, payment, 10m)).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await RefundVia(staff, order, payment, 5.01m)).StatusCode); // only 5 left
        Assert.Equal(HttpStatusCode.BadRequest, (await RefundVia(staff, order, payment, 0m)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await RefundVia(staff, order, payment, 1.005m)).StatusCode);

        var refused = await RefundVia(staff, order, payment, 0.13m); // the test provider refuses this amount
        Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
        var text = await refused.Content.ReadAsStringAsync();
        Assert.DoesNotContain("secret internal detail", text);
        Assert.Equal(10m, (await Get(staff, order)).AmountRefunded); // nothing was booked for the refused one
    }

    [Fact]
    public async Task A_refund_the_provider_is_still_working_on_is_not_booked_until_its_webhook_says_it_succeeded()
    {
        var staff = await factory.CreateAuthenticatedApiClientAsync();
        var order = await Place(staff, (await NewProduct(staff, 20m), 1));
        var (payment, _) = await PaidViaTestpay(staff, order);

        var pending = await Read<RefundResultDto>(await RefundVia(staff, order, payment, 0.07m));
        Assert.Equal("Pending", pending.Outcome);
        Assert.Equal(0m, (await Get(staff, order)).AmountRefunded);
    }

    [Fact]
    public async Task Only_a_payment_on_this_order_taken_through_a_refunding_provider_can_be_refunded_through_it_and_only_by_staff_who_may_write()
    {
        var staff = await factory.CreateAuthenticatedApiClientAsync();
        var product = await NewProduct(staff, 20m);
        var order = await Place(staff, (product, 1));
        var other = await Place(staff, (product, 1));
        var (card, _) = await PaidViaTestpay(staff, order);
        var cash = await Read<PaymentDto>(await staff.PostAsJsonAsync($"/api/v1/orders/{order.Id}/payments", new PaymentInput { Amount = 1m, Provider = "manual", Reference = "cash-" + Tag() }));
        var (otherCard, _) = await PaidViaTestpay(staff, other);

        Assert.Equal(HttpStatusCode.BadRequest, (await RefundVia(staff, order, cash, 1m)).StatusCode);          // not a gateway payment
        Assert.Equal(HttpStatusCode.BadRequest, (await RefundVia(staff, order, otherCard, 1m)).StatusCode);     // another order's payment
        Assert.Equal(HttpStatusCode.NotFound, (await staff.PostAsJsonAsync($"/api/v1/orders/{Guid.NewGuid()}/payments/{card.Id}/refund", new RefundStartInput { Amount = 1m }, Json)).StatusCode);
        Assert.Equal(0m, (await Get(staff, order)).AmountRefunded);

        var providers = await staff.GetFromJsonAsync<List<PaymentProviderDto>>("/api/v1/payment-providers", Json);
        Assert.True(providers!.Single(p => p.Name == "testpay").CanRefund);
        Assert.False(providers!.Single(p => p.Name == "manual").CanRefund);

        var (email, password) = await factory.CreateReadOnlyUserAsync();
        var reader = await factory.CreateAuthenticatedApiClientAsync(email, password);
        Assert.Equal(HttpStatusCode.Forbidden, (await RefundVia(reader, order, card, 1m)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await Ship(reader, order)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await Return(reader, order, ReturnOf(order, 1))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await reader.GetAsync($"/api/v1/orders/{order.Id}/shipments")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await reader.GetAsync($"/api/v1/orders/{order.Id}/returns")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await factory.CreateClient().GetAsync($"/api/v1/orders/{order.Id}/shipments")).StatusCode);
    }

    // ---- what the buyer sees ----------------------------------------------------------------------------------------------------

    [Fact]
    public async Task A_shopper_sees_tracking_for_their_own_order_without_any_member_of_staff_and_never_anyone_elses()
    {
        var staff = await factory.CreateAuthenticatedApiClientAsync();
        var product = await NewProduct(staff);

        async Task<(HttpClient Client, Guid Id)> Shopper()
        {
            var client = factory.CreateClient();
            var registered = await Read<RegisteredDto>(await client.PostAsJsonAsync("/api/v1/storefront/auth/register", new RegisterInput { Email = $"fs-{Tag()}@example.com", Password = "a-long-enough-password" }));
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", registered.Token.AccessToken);
            return (client, registered.Profile.Id);
        }

        var (alice, _) = await Shopper();
        var (bob, _) = await Shopper();
        var basket = await Read<BasketDto>(await alice.PostAsync("/api/v1/storefront/baskets", null));
        await alice.PutAsJsonAsync($"/api/v1/storefront/baskets/{basket.Id}/lines/{product.Id}", new BasketLineInput { Quantity = 2 });
        var order = await Read<OrderDto>(await alice.PostAsJsonAsync($"/api/v1/storefront/baskets/{basket.Id}/checkout", new CheckoutInput
        {
            Email = "ignored@example.com", ShippingAddress = new AddressInput { Recipient = "Alice", Line1 = "1 High St", City = "Leeds", CountryCode = "GB" },
        }));
        await Stage(staff, order, OrderStage.Processing);
        await Ship(staff, order, ShipOf(order, 1, "DPD", "DPD-777", "https://track.example/DPD-777"));

        var raw = await (await alice.GetAsync($"/api/v1/storefront/orders/{order.Id}")).Content.ReadAsStringAsync();
        var seen = JsonSerializer.Deserialize<OrderDto>(raw, Json)!;
        var parcel = Assert.Single(seen.Shipments!);
        Assert.Equal(("DPD", "DPD-777", "https://track.example/DPD-777"), (parcel.Carrier, parcel.TrackingNumber, parcel.TrackingUrl));
        Assert.DoesNotContain("admin@piranha.local", raw);
        Assert.DoesNotContain("actor", raw, StringComparison.OrdinalIgnoreCase);

        Assert.Equal(HttpStatusCode.NotFound, (await bob.GetAsync($"/api/v1/storefront/orders/{order.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await alice.GetAsync($"/api/v1/storefront/orders/{order.Id}/shipments")).StatusCode); // staff-only detail is not on the shopper's side
    }
}
