using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.EntityFrameworkCore;
using PiranhaCommerce.Catalog;
using PiranhaCommerce.Customers;
using PiranhaCommerce.Data;
using PiranhaCommerce.Ecommerce;

namespace PiranhaCommerce.IntegrationTests;

/// <summary>Exporting everything held about a customer, and erasing them: orders are anonymised, never deleted.</summary>
[Collection(OrderSettings.Name)]
public class CustomerErasureTests(PiranhaCommerceFactory factory) : IClassFixture<PiranhaCommerceFactory>
{
    private const string Password = "a-long-enough-password";
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };
    private static string Tag() => Guid.NewGuid().ToString("N")[..10];
    private static async Task<T> Read<T>(HttpResponseMessage r) => (await r.Content.ReadFromJsonAsync<T>(Json))!;

    private sealed record Shopper(HttpClient Client, Guid Id, string Email, string Token);

    private async Task<Shopper> NewShopper(string? email = null)
    {
        email ??= $"erase-{Tag()}@example.com";
        var client = factory.CreateClient();
        var registered = await Read<RegisteredDto>(await client.PostAsJsonAsync("/api/v1/storefront/auth/register", new RegisterInput { Email = email, Password = Password, FirstName = "Ada", LastName = "Lovelace" }));
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", registered.Token.AccessToken);
        return new Shopper(client, registered.Profile.Id, email, registered.Token.AccessToken);
    }

    private static async Task<ProductDto> NewProduct(HttpClient staff)
    {
        var p = await Read<ProductDto>(await staff.PostAsJsonAsync("/api/v1/products", new ProductInput { Name = "Erase " + Tag(), Sku = "ER-" + Tag(), Price = 10m }));
        await staff.PutAsJsonAsync($"/api/v1/products/{p.Id}/listing", new ProductListingInput { Published = true });
        await staff.PostAsJsonAsync($"/api/v1/products/{p.Id}/stock/adjustments", new StockAdjustmentInput { Delta = 50, Reason = "Delivery" });
        return p;
    }

    private static CheckoutInput Details(string email, string? notes = "Leave it with Mrs Babbage next door") => new()
    {
        Email = email, Notes = notes,
        ShippingAddress = new AddressInput { Recipient = "Ada Lovelace", Line1 = "10 Secret Lane", Line2 = "Flat 2", City = "Leeds", Region = "West Yorkshire", PostalCode = "LS1 1AA", CountryCode = "GB" },
        BillingAddress = new AddressInput { Recipient = "Ada Lovelace", Line1 = "11 Billing Rd", City = "Leeds", PostalCode = "LS2 2BB", CountryCode = "GB" },
    };

    private static async Task<OrderDto> ShopperOrder(Shopper s, ProductDto p)
    {
        var basket = await Read<BasketDto>(await s.Client.PostAsync("/api/v1/storefront/baskets", null));
        await s.Client.PutAsJsonAsync($"/api/v1/storefront/baskets/{basket.Id}/lines/{p.Id}", new BasketLineInput { Quantity = 2 });
        var r = await s.Client.PostAsJsonAsync($"/api/v1/storefront/baskets/{basket.Id}/checkout", Details(s.Email));
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        return await Read<OrderDto>(r);
    }

    /// <summary>A guest order placed by staff (no account) under the given email.</summary>
    private static async Task<OrderDto> GuestOrder(HttpClient staff, ProductDto p, string email)
    {
        var basket = await Read<BasketDto>(await staff.PostAsJsonAsync("/api/v1/baskets", new BasketCreateInput()));
        await staff.PutAsJsonAsync($"/api/v1/baskets/{basket.Id}/lines/{p.Id}", new BasketLineInput { Quantity = 1 });
        var r = await staff.PostAsJsonAsync($"/api/v1/baskets/{basket.Id}/checkout", Details(email));
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        return await Read<OrderDto>(r);
    }

    private static Task<HttpResponseMessage> Stage(HttpClient c, OrderDto o, OrderStage to) => c.PostAsJsonAsync($"/api/v1/orders/{o.Id}/stage", new OrderStageInput { Stage = to }, Json);
    private static async Task<OrderDto> Get(HttpClient c, OrderDto o) => await Read<OrderDto>(await c.GetAsync($"/api/v1/orders/{o.Id}"));
    private static Task<HttpResponseMessage> Erase(HttpClient c, Guid id, string confirm) => c.PostAsJsonAsync($"/api/v1/customers/{id}/erase", new EraseCustomerInput { ConfirmEmail = confirm });

    /// <summary>Completed, paid, with a manual payment note, a shipment and a return note: the full set of things that must be cleaned.</summary>
    private static async Task<OrderDto> CompleteWithHistory(HttpClient staff, OrderDto order)
    {
        await Stage(staff, order, OrderStage.Processing);
        await staff.PostAsJsonAsync($"/api/v1/orders/{order.Id}/payments", new PaymentInput { Amount = order.Total, Provider = "manual", Reference = "p-" + Tag(), Note = "Paid by Ada's husband Charles in cash" });
        await staff.PostAsJsonAsync($"/api/v1/orders/{order.Id}/shipments", new ShipmentInput { Carrier = "DPD", TrackingNumber = "T-" + Tag() });
        var done = await Get(staff, order);
        await staff.PostAsJsonAsync($"/api/v1/orders/{order.Id}/returns", new ReturnInput
        {
            Reason = "Wrong size", Note = "Ada phoned from 07700 900123", Lines = [new ReturnLineInput { OrderLineId = done.Lines[0].Id, Quantity = 1 }],
        });
        return await Get(staff, order);
    }

    [Fact]
    public async Task Erasing_a_customer_anonymises_their_orders_and_guest_orders_keeps_the_money_and_removes_the_account()
    {
        var staff = await factory.CreateAuthenticatedApiClientAsync();
        var product = await NewProduct(staff);
        var ada = await NewShopper();
        var bob = await NewShopper();

        // Ada: an address-book entry, an order with full history, an open basket, and a guest order under her address in another letter case.
        await ada.Client.PostAsJsonAsync("/api/v1/storefront/me/addresses", new AddressInput { Line1 = "10 Secret Lane", City = "Leeds", CountryCode = "GB" });
        var adaOrder = await CompleteWithHistory(staff, await ShopperOrder(ada, product));
        var guest = await CompleteWithHistory(staff, await GuestOrder(staff, product, ada.Email.ToUpperInvariant()));
        var openBasket = await Read<BasketDto>(await ada.Client.PostAsync("/api/v1/storefront/baskets", null));
        var bobOrder = await Get(staff, await ShopperOrder(bob, product));
        var strangerOrder = await Get(staff, await GuestOrder(staff, product, $"stranger-{Tag()}@example.com"));

        var result = await Read<CustomerErasureResultDto>(await Erase(staff, ada.Id, ada.Email));
        Assert.Equal(3, result.Changed["orders"]); // two orders anonymised + one open basket deleted

        // The customer, their address book and their sign-in are gone, and the old token no longer works.
        Assert.Equal(HttpStatusCode.NotFound, (await staff.GetAsync($"/api/v1/customers/{ada.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await ada.Client.GetAsync("/api/v1/storefront/me")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await factory.CreateClient().PostAsJsonAsync("/api/v1/storefront/auth/token", new SignInInput(ada.Email, Password))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await staff.GetAsync($"/api/v1/baskets/{openBasket.Id}")).StatusCode);

        foreach (var before in new[] { adaOrder, guest })
        {
            var after = await Get(staff, before);
            Assert.Equal("erased@erased.invalid", after.Email);
            Assert.Null(after.CustomerId);
            Assert.Null(after.Notes);
            foreach (var a in new[] { after.ShippingAddress!, after.BillingAddress! })
            {
                Assert.Equal(("Erased", "Erased", "GB"), (a.Line1, a.City, a.CountryCode)); // the country stays for tax reporting
                Assert.Null(a.Recipient); Assert.Null(a.Line2); Assert.Null(a.Region); Assert.Null(a.PostalCode);
            }
            // What the shop must keep is exactly as it was.
            Assert.Equal((before.Stage, before.Total, before.TaxTotal, before.AmountPaid, before.AmountRefunded), (after.Stage, after.Total, after.TaxTotal, after.AmountPaid, after.AmountRefunded));
            Assert.Equal(before.Lines.Select(l => (l.Name, l.Quantity, l.LineTotal, l.QuantityShipped, l.QuantityReturned)), after.Lines.Select(l => (l.Name, l.Quantity, l.LineTotal, l.QuantityShipped, l.QuantityReturned)));
            Assert.Equal(before.Shipments!.Count, after.Shipments!.Count);

            var payments = await Read<List<PaymentDto>>(await staff.GetAsync($"/api/v1/orders/{before.Id}/payments"));
            Assert.Equal(before.Total, payments.Single(p => p.Kind == PaymentKind.Payment).Amount);
            Assert.All(payments, p => Assert.Null(p.Note));
            var returns = await Read<List<ReturnDto>>(await staff.GetAsync($"/api/v1/orders/{before.Id}/returns"));
            Assert.Equal("Wrong size", Assert.Single(returns).Reason);
            Assert.Null(returns[0].Note);
        }

        // Other people's orders are untouched, including a guest order under another address.
        var bobAfter = await Get(staff, bobOrder);
        Assert.Equal((bobOrder.Email, bobOrder.CustomerId, bobOrder.Notes, bobOrder.ShippingAddress!.Line1), (bobAfter.Email, bobAfter.CustomerId, bobAfter.Notes, bobAfter.ShippingAddress!.Line1));
        var stranger = await Get(staff, strangerOrder);
        Assert.Equal((strangerOrder.Email, "10 Secret Lane", "Ada Lovelace"), (stranger.Email, stranger.ShippingAddress!.Line1, stranger.ShippingAddress.Recipient));

        // A record that it happened, with no personal data in it.
        using var scope = factory.Services.CreateScope();
        var record = await scope.ServiceProvider.GetRequiredService<PiranhaCommerceDbContext>().Set<CustomerErasure>().AsNoTracking().SingleAsync(e => e.CustomerId == ada.Id);
        Assert.Equal(("admin@piranhacommerce.local", "orders: 3"), (record.Actor, record.Details));

        // Erasing again is simply "not found".
        Assert.Equal(HttpStatusCode.NotFound, (await Erase(staff, ada.Id, ada.Email)).StatusCode);
    }

    [Fact]
    public async Task Erasure_is_refused_with_the_reasons_while_an_order_is_open_or_a_refund_is_owed_and_nothing_changes()
    {
        var staff = await factory.CreateAuthenticatedApiClientAsync();
        var product = await NewProduct(staff);
        var s = await NewShopper();
        var open = await ShopperOrder(s, product); // Placed

        var refused = await Erase(staff, s.Id, s.Email);
        Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
        var text = await refused.Content.ReadAsStringAsync();
        Assert.Contains(open.Reference, text);
        Assert.DoesNotContain(s.Email, text); // a reason names the order, never the person
        Assert.Equal(HttpStatusCode.OK, (await staff.GetAsync($"/api/v1/customers/{s.Id}")).StatusCode);
        Assert.Equal(s.Email, (await Get(staff, open)).Email);

        // Cancelled but paid and not refunded: the contact details are the only way to return the money.
        await staff.PostAsJsonAsync($"/api/v1/orders/{open.Id}/payments", new PaymentInput { Amount = open.Total, Provider = "manual", Reference = "p-" + Tag() });
        await Stage(staff, open, OrderStage.Cancelled);
        var owed = await Erase(staff, s.Id, s.Email);
        Assert.Equal(HttpStatusCode.Conflict, owed.StatusCode);
        Assert.Contains("has not been refunded", await owed.Content.ReadAsStringAsync());

        await staff.PostAsJsonAsync($"/api/v1/orders/{open.Id}/refunds", new PaymentInput { Amount = open.Total, Provider = "manual", Reference = "r-" + Tag() });
        Assert.Equal(HttpStatusCode.OK, (await Erase(staff, s.Id, s.Email.ToUpperInvariant())).StatusCode); // the confirmation ignores letter case
    }

    [Fact]
    public async Task The_confirmation_must_be_the_customers_own_email_and_only_staff_who_may_write_can_erase()
    {
        var staff = await factory.CreateAuthenticatedApiClientAsync();
        var s = await NewShopper();

        Assert.Equal(HttpStatusCode.BadRequest, (await Erase(staff, s.Id, "someone-else@example.com")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await Erase(staff, s.Id, "")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await Erase(staff, Guid.NewGuid(), "x@example.com")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await staff.GetAsync($"/api/v1/customers/{s.Id}")).StatusCode);

        var (email, password) = await factory.CreateReadOnlyUserAsync();
        var reader = await factory.CreateAuthenticatedApiClientAsync(email, password);
        Assert.Equal(HttpStatusCode.Forbidden, (await Erase(reader, s.Id, s.Email)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await reader.GetAsync($"/api/v1/customers/{s.Id}/export")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await factory.CreateClient().GetAsync($"/api/v1/customers/{s.Id}/export")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await s.Client.GetAsync($"/api/v1/customers/{s.Id}/export")).StatusCode); // a shopper token is not staff
        Assert.Equal(HttpStatusCode.NotFound, (await staff.GetAsync($"/api/v1/customers/{Guid.NewGuid()}/export")).StatusCode);
    }

    [Fact]
    public async Task An_export_holds_everything_about_that_customer_and_nothing_about_anyone_else_and_never_a_password()
    {
        var staff = await factory.CreateAuthenticatedApiClientAsync();
        var product = await NewProduct(staff);
        var ada = await NewShopper();
        var bob = await NewShopper();
        await staff.PutAsJsonAsync($"/api/v1/customers/{ada.Id}", new CustomerInput { Email = ada.Email, FirstName = "Ada", LastName = "Lovelace", Notes = "Prefers morning delivery" });
        await ada.Client.PostAsJsonAsync("/api/v1/storefront/me/addresses", new AddressInput { Label = "Home", Line1 = "10 Secret Lane", City = "Leeds", CountryCode = "GB" });
        var adaOrder = await ShopperOrder(ada, product);
        var guest = await GuestOrder(staff, product, ada.Email);
        var bobOrder = await ShopperOrder(bob, product);

        var raw = await (await staff.GetAsync($"/api/v1/customers/{ada.Id}/export")).Content.ReadAsStringAsync();
        var doc = JsonDocument.Parse(raw).RootElement;
        Assert.Equal(ada.Email, doc.GetProperty("customer").GetProperty("email").GetString());
        Assert.Equal("Prefers morning delivery", doc.GetProperty("customer").GetProperty("notes").GetString()); // staff export includes staff notes
        Assert.True(doc.GetProperty("hasSignIn").GetBoolean());
        Assert.Equal("Home", Assert.Single(doc.GetProperty("addresses").EnumerateArray()).GetProperty("label").GetString());
        var refs = doc.GetProperty("sections").GetProperty("orders").GetProperty("orders").EnumerateArray().Select(o => o.GetProperty("reference").GetString()).ToList();
        Assert.Equal(new[] { adaOrder.Reference, guest.Reference }.Order(), refs.Order());
        Assert.DoesNotContain(bobOrder.Reference, raw);
        Assert.DoesNotContain(bob.Email, raw);
        Assert.DoesNotContain("passwordHash", raw, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("tokenHash", raw, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Leave it with Mrs Babbage next door", raw); // order notes are theirs
    }

    [Fact]
    public async Task Nothing_is_emailed_about_an_erased_order_even_when_a_late_refund_arrives()
    {
        var staff = await factory.CreateAuthenticatedApiClientAsync();
        var product = await NewProduct(staff);
        var s = await NewShopper();
        var order = await ShopperOrder(s, product);
        await staff.PostAsJsonAsync($"/api/v1/orders/{order.Id}/payments", new PaymentInput { Amount = order.Total, Provider = "manual", Reference = "p-" + Tag() });
        await Stage(staff, order, OrderStage.Processing);
        await staff.PostAsJsonAsync($"/api/v1/orders/{order.Id}/shipments", new ShipmentInput());
        Assert.Equal(HttpStatusCode.OK, (await Erase(staff, s.Id, s.Email)).StatusCode);

        var originalBefore = factory.Email.To(s.Email).Count;
        var placeholderBefore = factory.Email.To("erased@erased.invalid").Count;
        Assert.Equal(HttpStatusCode.Created, (await staff.PostAsJsonAsync($"/api/v1/orders/{order.Id}/refunds", new PaymentInput { Amount = 1m, Provider = "manual", Reference = "r-" + Tag() })).StatusCode);

        Assert.Equal(originalBefore, factory.Email.To(s.Email).Count);
        Assert.Equal(placeholderBefore, factory.Email.To("erased@erased.invalid").Count);
    }

    [Fact]
    public async Task Simultaneous_erasures_of_one_customer_erase_once()
    {
        var staff = await factory.CreateAuthenticatedApiClientAsync();
        var s = await NewShopper();
        var results = await Task.WhenAll(Enumerable.Range(0, 5).Select(_ => Erase(staff, s.Id, s.Email)));
        Assert.Equal(1, results.Count(r => r.StatusCode == HttpStatusCode.OK));
        Assert.Equal(4, results.Count(r => r.StatusCode == HttpStatusCode.NotFound));
    }
}
