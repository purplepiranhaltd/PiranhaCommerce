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

/// <summary>A buyer asks to send goods back; staff answer; the return recorded on arrival closes it.</summary>
[Collection(OrderSettings.Name)]
public class ReturnRequestTests(AlertFactory factory) : IClassFixture<AlertFactory>
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };
    private static string Tag() => Guid.NewGuid().ToString("N")[..8];
    private static async Task<T> Read<T>(HttpResponseMessage r) => (await r.Content.ReadFromJsonAsync<T>(Json))!;

    /// <summary>A signed-in shopper, and one of their orders that staff have processed and sent in full (quantity items of one product).</summary>
    private async Task<(HttpClient Staff, HttpClient Shopper, OrderDto Order, string Email)> SentOrder(int quantity, bool ship = true)
    {
        var staff = await factory.CreateAuthenticatedApiClientAsync();
        var p = await Read<ProductDto>(await staff.PostAsJsonAsync("/api/v1/products", new ProductInput { Name = "RR " + Tag(), Sku = "RR-" + Tag(), Price = 10m }));
        await staff.PutAsJsonAsync($"/api/v1/products/{p.Id}/listing", new ProductListingInput { Published = true });
        await staff.PostAsJsonAsync($"/api/v1/products/{p.Id}/stock/adjustments", new StockAdjustmentInput { Delta = 50, Reason = "Delivery" });

        var shopper = factory.CreateClient();
        var email = $"rr-{Tag()}@example.com";
        var reg = await Read<RegisteredDto>(await shopper.PostAsJsonAsync("/api/v1/storefront/auth/register", new RegisterInput { Email = email, Password = "a-long-enough-password" }));
        shopper.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", reg.Token.AccessToken);
        var basket = await Read<BasketDto>(await shopper.PostAsync("/api/v1/storefront/baskets", null));
        await shopper.PutAsJsonAsync($"/api/v1/storefront/baskets/{basket.Id}/lines/{p.Id}", new BasketLineInput { Quantity = quantity });
        var checkout = await shopper.PostAsJsonAsync($"/api/v1/storefront/baskets/{basket.Id}/checkout", new CheckoutInput
        {
            ShippingAddress = new AddressInput { Recipient = "Ada", Line1 = "1 High St", City = "Leeds", PostalCode = "LS1", CountryCode = "GB" },
        });
        Assert.Equal(HttpStatusCode.OK, checkout.StatusCode);
        var order = await Read<OrderDto>(checkout);
        if (ship)
        {
            Assert.Equal(HttpStatusCode.OK, (await staff.PostAsJsonAsync($"/api/v1/orders/{order.Id}/stage", new OrderStageInput { Stage = OrderStage.Processing }, Json)).StatusCode);
            Assert.Equal(HttpStatusCode.Created, (await staff.PostAsJsonAsync($"/api/v1/orders/{order.Id}/shipments", new ShipmentInput(), Json)).StatusCode);
            order = await Read<OrderDto>(await staff.GetAsync($"/api/v1/orders/{order.Id}"));
        }
        return (staff, shopper, order, email);
    }

    private static Task<HttpResponseMessage> Ask(HttpClient shopper, OrderDto o, int quantity, string reason = "Too small", string? note = null)
        => shopper.PostAsJsonAsync($"/api/v1/storefront/orders/{o.Id}/return-requests",
            new ReturnRequestInput { Reason = reason, Note = note, Lines = [new ReturnRequestLineInput { OrderLineId = o.Lines[0].Id, Quantity = quantity }] }, Json);

    [Fact]
    public async Task A_shopper_asks_staff_approve_and_recording_the_return_closes_the_request()
    {
        var (staff, shopper, order, email) = await SentOrder(3);
        var shopEmails = factory.Email.To("alerts@example.com").Count;

        var asked = await Ask(shopper, order, 2, note: "Colour was wrong");
        Assert.Equal(HttpStatusCode.Created, asked.StatusCode);
        var request = await Read<ReturnRequestDto>(asked);
        Assert.Equal((ReturnRequestStatus.Requested, 2), (request.Status, request.Lines.Single().Quantity));
        Assert.Null(request.RespondedBy);
        Assert.Contains(factory.Email.To("alerts@example.com").Skip(shopEmails), m => m.TemplateKey == "alert.return-requested");

        // It shows up for staff, with a filter.
        var waiting = await Read<PagedResult<ReturnRequestDto>>(await staff.GetAsync("/api/v1/return-requests?status=Requested&pageSize=100"));
        Assert.Contains(waiting.Items, r => r.Id == request.Id);

        var approved = await Read<ReturnRequestDto>(await staff.PostAsJsonAsync($"/api/v1/return-requests/{request.Id}/approve", new ReturnResponseInput { Note = "Use the label" }));
        Assert.Equal(ReturnRequestStatus.Approved, approved.Status);
        Assert.NotNull(approved.RespondedBy);
        var answer = Assert.Single(factory.Email.To(email), m => m.TemplateKey == "return.answered");
        Assert.Equal(true, answer.Variables["approved"]);
        EmailContract.AssertMatchesDescriptor(factory, answer);

        // The shopper sees the answer, but not which staff member gave it.
        var mine = await Read<List<ReturnRequestDto>>(await shopper.GetAsync($"/api/v1/storefront/orders/{order.Id}/return-requests"));
        var seen = Assert.Single(mine);
        Assert.Equal((ReturnRequestStatus.Approved, "Use the label"), (seen.Status, seen.ResponseNote));
        Assert.Null(seen.RespondedBy);

        // Answered once only; and it cannot now be withdrawn.
        Assert.Equal(HttpStatusCode.Conflict, (await staff.PostAsJsonAsync($"/api/v1/return-requests/{request.Id}/reject", new ReturnResponseInput())).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await shopper.DeleteAsync($"/api/v1/storefront/orders/{order.Id}/return-requests/{request.Id}")).StatusCode);

        // When the goods arrive, the recorded return names the request and closes it. Only once.
        var input = new ReturnInput { Reason = "As requested", ReturnRequestId = request.Id, Lines = [new ReturnLineInput { OrderLineId = order.Lines[0].Id, Quantity = 2 }] };
        Assert.Equal(HttpStatusCode.Created, (await staff.PostAsJsonAsync($"/api/v1/orders/{order.Id}/returns", input, Json)).StatusCode);
        var received = await Read<ReturnRequestDto>(await staff.GetAsync($"/api/v1/return-requests/{request.Id}"));
        Assert.Equal(ReturnRequestStatus.Received, received.Status);
        Assert.NotNull(received.ReceivedReturnId);
        input.Lines[0].Quantity = 1;
        Assert.Equal(HttpStatusCode.Conflict, (await staff.PostAsJsonAsync($"/api/v1/orders/{order.Id}/returns", input, Json)).StatusCode);
    }

    [Fact]
    public async Task Requests_hold_back_what_they_cover_and_only_sent_goods_can_be_asked_for()
    {
        var (staff, shopper, order, _) = await SentOrder(3);
        Assert.Equal(HttpStatusCode.Created, (await Ask(shopper, order, 2)).StatusCode);
        var second = await Ask(shopper, order, 2); // only 1 is left to ask for
        Assert.Equal(HttpStatusCode.Conflict, second.StatusCode);
        Assert.Contains("Only 1", await second.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.Created, (await Ask(shopper, order, 1)).StatusCode);

        // A rejected request lets the quantity go again.
        var all = await Read<List<ReturnRequestDto>>(await shopper.GetAsync($"/api/v1/storefront/orders/{order.Id}/return-requests"));
        var first = all.Single(r => r.Lines.Single().Quantity == 2);
        Assert.Equal(HttpStatusCode.OK, (await staff.PostAsJsonAsync($"/api/v1/return-requests/{first.Id}/reject", new ReturnResponseInput { Note = "Outside 30 days" })).StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await Ask(shopper, order, 2)).StatusCode);

        // Bad input.
        Assert.Equal(HttpStatusCode.BadRequest, (await Ask(shopper, order, 0)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await Ask(shopper, order, 1, reason: "")).StatusCode);
        var empty = await shopper.PostAsJsonAsync($"/api/v1/storefront/orders/{order.Id}/return-requests", new ReturnRequestInput { Reason = "x" }, Json);
        Assert.Equal(HttpStatusCode.BadRequest, empty.StatusCode);
        var foreign = await shopper.PostAsJsonAsync($"/api/v1/storefront/orders/{order.Id}/return-requests",
            new ReturnRequestInput { Reason = "x", Lines = [new ReturnRequestLineInput { OrderLineId = Guid.NewGuid(), Quantity = 1 }] }, Json);
        Assert.Equal(HttpStatusCode.BadRequest, foreign.StatusCode);

        // An order nothing has left yet has nothing to send back.
        var (_, unsentShopper, unsent, _) = await SentOrder(1, ship: false);
        var tooSoon = await Ask(unsentShopper, unsent, 1);
        Assert.Equal(HttpStatusCode.Conflict, tooSoon.StatusCode);
        Assert.Contains("not been sent", await tooSoon.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task A_shopper_can_withdraw_an_unanswered_request_and_only_see_and_touch_their_own()
    {
        var (staff, shopper, order, _) = await SentOrder(2);
        var request = await Read<ReturnRequestDto>(await Ask(shopper, order, 1));

        var (_, other, _, _) = await SentOrder(1);
        Assert.Equal(HttpStatusCode.NotFound, (await other.GetAsync($"/api/v1/storefront/orders/{order.Id}/return-requests")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await Ask(other, order, 1)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await other.DeleteAsync($"/api/v1/storefront/orders/{order.Id}/return-requests/{request.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await factory.CreateClient().GetAsync($"/api/v1/storefront/orders/{order.Id}/return-requests")).StatusCode);

        Assert.Equal(HttpStatusCode.NoContent, (await shopper.DeleteAsync($"/api/v1/storefront/orders/{order.Id}/return-requests/{request.Id}")).StatusCode);
        Assert.Equal(ReturnRequestStatus.Cancelled, (await Read<ReturnRequestDto>(await staff.GetAsync($"/api/v1/return-requests/{request.Id}"))).Status);
        Assert.Equal(HttpStatusCode.Conflict, (await staff.PostAsJsonAsync($"/api/v1/return-requests/{request.Id}/approve", new ReturnResponseInput())).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await shopper.DeleteAsync($"/api/v1/storefront/orders/{order.Id}/return-requests/{request.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await Ask(shopper, order, 2)).StatusCode); // the quantity is free again
    }

    [Fact]
    public async Task Only_writers_answer_two_staff_answering_at_once_decide_once_and_unknown_ids_are_404()
    {
        var (staff, shopper, order, _) = await SentOrder(1);
        var request = await Read<ReturnRequestDto>(await Ask(shopper, order, 1));

        var (email, password) = await factory.CreateReadOnlyUserAsync();
        var reader = await factory.CreateAuthenticatedApiClientAsync(email, password);
        Assert.Equal(HttpStatusCode.OK, (await reader.GetAsync($"/api/v1/return-requests/{request.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await reader.PostAsJsonAsync($"/api/v1/return-requests/{request.Id}/approve", new ReturnResponseInput())).StatusCode);

        var results = await Task.WhenAll(
            staff.PostAsJsonAsync($"/api/v1/return-requests/{request.Id}/approve", new ReturnResponseInput()),
            staff.PostAsJsonAsync($"/api/v1/return-requests/{request.Id}/reject", new ReturnResponseInput()));
        Assert.Equal(1, results.Count(r => r.StatusCode == HttpStatusCode.OK));
        Assert.Equal(1, results.Count(r => r.StatusCode == HttpStatusCode.Conflict));

        Assert.Equal(HttpStatusCode.NotFound, (await staff.PostAsJsonAsync($"/api/v1/return-requests/{Guid.NewGuid()}/approve", new ReturnResponseInput())).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await staff.GetAsync($"/api/v1/return-requests/{Guid.NewGuid()}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await staff.GetAsync($"/api/v1/orders/{Guid.NewGuid()}/return-requests")).StatusCode);
        Assert.Single(await Read<List<ReturnRequestDto>>(await staff.GetAsync($"/api/v1/orders/{order.Id}/return-requests")));
    }

    [Fact]
    public async Task A_return_cannot_name_a_request_that_is_not_approved_or_belongs_to_another_order()
    {
        var (staff, shopper, order, _) = await SentOrder(2);
        var pending = await Read<ReturnRequestDto>(await Ask(shopper, order, 1));
        var line = new ReturnLineInput { OrderLineId = order.Lines[0].Id, Quantity = 1 };

        var unapproved = await staff.PostAsJsonAsync($"/api/v1/orders/{order.Id}/returns", new ReturnInput { Reason = "x", ReturnRequestId = pending.Id, Lines = [line] }, Json);
        Assert.Equal(HttpStatusCode.Conflict, unapproved.StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await staff.PostAsJsonAsync($"/api/v1/orders/{order.Id}/returns", new ReturnInput { Reason = "x", ReturnRequestId = Guid.NewGuid(), Lines = [line] }, Json)).StatusCode);

        // The failed attempts changed nothing: no stock came back and nothing was recorded.
        var detail = await Read<OrderDto>(await staff.GetAsync($"/api/v1/orders/{order.Id}"));
        Assert.Equal(0, detail.Lines[0].QuantityReturned);
    }

    [Fact]
    public async Task Erasing_a_shopper_clears_what_they_and_the_shop_wrote_and_withdraws_unanswered_requests()
    {
        var (staff, shopper, order, _) = await SentOrder(2);
        var request = await Read<ReturnRequestDto>(await Ask(shopper, order, 1, note: "My private reason"));
        // The order is completed (all sent), so erasure is allowed.
        var erased = await shopper.PostAsJsonAsync("/api/v1/storefront/me/erase", new EraseOwnAccountInput { Password = "a-long-enough-password" });
        Assert.Equal(HttpStatusCode.NoContent, erased.StatusCode);

        var after = await Read<ReturnRequestDto>(await staff.GetAsync($"/api/v1/return-requests/{request.Id}"));
        Assert.Null(after.Note);
        Assert.Equal(ReturnRequestStatus.Cancelled, after.Status);
    }
}
