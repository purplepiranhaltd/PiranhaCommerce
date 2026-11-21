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

[Collection(OrderSettings.Name)]
public class StorefrontOrderTests(PiranhaFactory factory) : IClassFixture<PiranhaFactory>
{
    private const string Password = "a-long-enough-password";
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };

    private static string Tag() => Guid.NewGuid().ToString("N")[..10];
    private static async Task<T> Read<T>(HttpResponseMessage r) => (await r.Content.ReadFromJsonAsync<T>(Json))!;

    private async Task<(HttpClient Client, Guid Id, string Email)> NewShopperAsync()
    {
        var email = $"shopper-{Tag()}@example.com";
        var client = factory.CreateClient();
        var response = await client.PostAsJsonAsync("/api/v1/storefront/auth/register", new RegisterInput { Email = email, Password = Password });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var registered = (await response.Content.ReadFromJsonAsync<PiranhaCommerce.Customers.RegisteredDto>())!;
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", registered.Token.AccessToken);
        return (client, registered.Profile.Id, email);
    }

    private static async Task<ProductDto> NewProduct(HttpClient staff, int stock = 5)
    {
        var p = await Read<ProductDto>(await staff.PostAsJsonAsync("/api/v1/products", new ProductInput { Name = "Storefront " + Tag(), Sku = "SF-" + Tag(), Price = 10m }));
        Assert.Equal(HttpStatusCode.OK, (await staff.PutAsJsonAsync($"/api/v1/products/{p.Id}/listing", new ProductListingInput { Published = true })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await staff.PostAsJsonAsync($"/api/v1/products/{p.Id}/stock/adjustments",
            new StockAdjustmentInput { Delta = stock, Reason = "Delivery" })).StatusCode);
        return p;
    }

    private static async Task<decimal> Stock(HttpClient staff, Guid productId) =>
        (await staff.GetFromJsonAsync<StockLevelDto>($"/api/v1/products/{productId}/stock"))!.Quantity;

    private static async Task<BasketDto> ShopperBasketAsync(HttpClient shopper, ProductDto product, int quantity)
    {
        var created = await shopper.PostAsync("/api/v1/storefront/baskets", null);
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var basket = await Read<BasketDto>(created);
        var line = await shopper.PutAsJsonAsync($"/api/v1/storefront/baskets/{basket.Id}/lines/{product.Id}", new BasketLineInput { Quantity = quantity });
        Assert.Equal(HttpStatusCode.OK, line.StatusCode);
        return basket;
    }

    private static CheckoutInput Checkout() => new()
    {
        Email = "",
        ShippingAddress = new AddressInput { Recipient = "Sam Shopper", Line1 = "1 High St", City = "Leeds", PostalCode = "LS1 1AA", CountryCode = "gb" },
    };

    [Fact]
    public async Task A_shopper_fills_a_basket_checks_out_and_sees_the_order_in_their_history()
    {
        var staff = await factory.CreateAuthenticatedApiClientAsync();
        var (shopper, id, email) = await NewShopperAsync();
        var product = await NewProduct(staff);
        var basket = await ShopperBasketAsync(shopper, product, 2);
        Assert.Equal(id, basket.CustomerId);

        // The caller tries to name someone else as the customer and gives no email: the token wins, the account email fills in.
        var input = Checkout();
        input.CustomerId = Guid.NewGuid();
        input.Email = "elsewhere@example.com"; // and tries to send the order email somewhere else
        var placed = await shopper.PostAsJsonAsync($"/api/v1/storefront/baskets/{basket.Id}/checkout", input);
        Assert.Equal(HttpStatusCode.OK, placed.StatusCode);
        var order = await Read<OrderDto>(placed);
        Assert.Equal(id, order.CustomerId);
        Assert.Equal(email, order.Email);
        Assert.Empty(factory.Email.To("elsewhere@example.com"));
        Assert.Equal(20m, order.Subtotal);
        Assert.Equal(3m, await Stock(staff, product.Id));

        var history = await Read<PagedResult<OrderSummaryDto>>(await shopper.GetAsync("/api/v1/storefront/orders"));
        Assert.Equal(order.Id, Assert.Single(history.Items).Id);
        Assert.Equal(order.Reference, (await Read<OrderDto>(await shopper.GetAsync($"/api/v1/storefront/orders/{order.Id}"))).Reference);

        // Staff see the same order, linked to the customer.
        Assert.Equal(id, (await Read<OrderDto>(await staff.GetAsync($"/api/v1/orders/{order.Id}"))).CustomerId);
    }

    [Fact]
    public async Task One_shopper_cannot_see_or_touch_another_shoppers_orders_or_baskets()
    {
        var staff = await factory.CreateAuthenticatedApiClientAsync();
        var (alice, _, _) = await NewShopperAsync();
        var (bob, _, _) = await NewShopperAsync();
        var product = await NewProduct(staff);

        var aliceBasket = await ShopperBasketAsync(alice, product, 1);
        var aliceOrder = await Read<OrderDto>(await alice.PostAsJsonAsync($"/api/v1/storefront/baskets/{aliceBasket.Id}/checkout", Checkout()));
        var aliceOpen = await ShopperBasketAsync(alice, product, 1);

        // Orders: another shopper's id is simply not found, and the history lists only their own.
        Assert.Equal(HttpStatusCode.NotFound, (await bob.GetAsync($"/api/v1/storefront/orders/{aliceOrder.Id}")).StatusCode);
        Assert.Empty((await Read<PagedResult<OrderSummaryDto>>(await bob.GetAsync("/api/v1/storefront/orders"))).Items);

        // Baskets: read, change, quote, check out and claim are all refused for a basket that is not theirs.
        Assert.Equal(HttpStatusCode.NotFound, (await bob.GetAsync($"/api/v1/storefront/baskets/{aliceOpen.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await bob.PutAsJsonAsync($"/api/v1/storefront/baskets/{aliceOpen.Id}/lines/{product.Id}", new BasketLineInput { Quantity = 3 })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await bob.DeleteAsync($"/api/v1/storefront/baskets/{aliceOpen.Id}/lines/{product.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await bob.PostAsJsonAsync($"/api/v1/storefront/baskets/{aliceOpen.Id}/quote", new QuoteInput { CountryCode = "GB" })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await bob.PostAsJsonAsync($"/api/v1/storefront/baskets/{aliceOpen.Id}/checkout", Checkout())).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await bob.PostAsync($"/api/v1/storefront/baskets/{aliceOpen.Id}/claim", null)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await bob.PostAsJsonAsync($"/api/v1/storefront/baskets/{aliceOpen.Id}/promo", new CodeInput { Code = "ANYTHING" })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await bob.DeleteAsync($"/api/v1/storefront/baskets/{aliceOpen.Id}/promo")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await bob.PostAsJsonAsync($"/api/v1/storefront/baskets/{aliceOpen.Id}/voucher", new CodeInput { Code = "ANYTHING" })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await bob.DeleteAsync($"/api/v1/storefront/baskets/{aliceOpen.Id}/voucher")).StatusCode);

        // Alice's basket is untouched and her stock was not taken by Bob's attempts.
        Assert.Equal(1, (await Read<BasketDto>(await alice.GetAsync($"/api/v1/storefront/baskets/{aliceOpen.Id}"))).Lines.Single().Quantity);
        Assert.Equal(4m, await Stock(staff, product.Id));
    }

    [Fact]
    public async Task An_anonymous_basket_can_be_claimed_once_and_then_belongs_to_that_shopper()
    {
        var staff = await factory.CreateAuthenticatedApiClientAsync();
        var (alice, aliceId, _) = await NewShopperAsync();
        var (bob, _, _) = await NewShopperAsync();
        var product = await NewProduct(staff);

        var anonymous = await Read<BasketDto>(await staff.PostAsJsonAsync("/api/v1/baskets", new BasketCreateInput()));
        Assert.Equal(HttpStatusCode.OK, (await staff.PutAsJsonAsync($"/api/v1/baskets/{anonymous.Id}/lines/{product.Id}", new BasketLineInput { Quantity = 2 })).StatusCode);

        // Not theirs yet, so a shopper cannot read or change it without claiming.
        Assert.Equal(HttpStatusCode.NotFound, (await alice.GetAsync($"/api/v1/storefront/baskets/{anonymous.Id}")).StatusCode);

        var claimed = await alice.PostAsync($"/api/v1/storefront/baskets/{anonymous.Id}/claim", null);
        Assert.Equal(HttpStatusCode.OK, claimed.StatusCode);
        Assert.Equal(aliceId, (await Read<BasketDto>(claimed)).CustomerId);
        Assert.Equal(HttpStatusCode.OK, (await alice.PostAsync($"/api/v1/storefront/baskets/{anonymous.Id}/claim", null)).StatusCode); // again: still hers

        Assert.Equal(HttpStatusCode.NotFound, (await bob.PostAsync($"/api/v1/storefront/baskets/{anonymous.Id}/claim", null)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await alice.PostAsJsonAsync($"/api/v1/storefront/baskets/{anonymous.Id}/checkout", Checkout())).StatusCode);
    }

    [Fact]
    public async Task Two_shoppers_claiming_the_same_basket_at_once_leaves_exactly_one_owner()
    {
        var staff = await factory.CreateAuthenticatedApiClientAsync();
        var shoppers = new List<HttpClient>();
        for (var i = 0; i < 4; i++) shoppers.Add((await NewShopperAsync()).Client);
        var anonymous = await Read<BasketDto>(await staff.PostAsJsonAsync("/api/v1/baskets", new BasketCreateInput()));

        var results = await Task.WhenAll(shoppers.Select(s => s.PostAsync($"/api/v1/storefront/baskets/{anonymous.Id}/claim", null)));
        Assert.Equal(1, results.Count(r => r.StatusCode == HttpStatusCode.OK));
        Assert.Equal(3, results.Count(r => r.StatusCode == HttpStatusCode.NotFound));
    }

    [Fact]
    public async Task Placing_an_order_emails_a_confirmation_but_a_broken_mail_server_never_stops_the_order()
    {
        var staff = await factory.CreateAuthenticatedApiClientAsync();
        var (shopper, _, email) = await NewShopperAsync();
        var product = await NewProduct(staff);

        var firstBasket = await ShopperBasketAsync(shopper, product, 1);
        var first = await Read<OrderDto>(await shopper.PostAsJsonAsync($"/api/v1/storefront/baskets/{firstBasket.Id}/checkout", Checkout()));
        var mail = Assert.Single(factory.Email.To(email));
        // The email is a key and data: the words belong to whichever sender is installed (SMTP templates, Mailjet...).
        Assert.Equal("order.confirmation", mail.TemplateKey);
        EmailContract.AssertMatchesDescriptor(factory, mail);
        Assert.Equal(first.Reference, mail.Variables["reference"]);
        Assert.Equal(first.Total.ToString("N2", System.Globalization.CultureInfo.InvariantCulture), mail.Variables["total"]);
        var lines = Assert.IsAssignableFrom<IEnumerable<object>>(mail.Variables["lines"]);
        Assert.Equal(product.Name, ((Dictionary<string, object?>)Assert.Single(lines))["name"]);

        factory.Email.Fail = true;
        try
        {
            var basket = await ShopperBasketAsync(shopper, product, 1);
            Assert.Equal(HttpStatusCode.OK, (await shopper.PostAsJsonAsync($"/api/v1/storefront/baskets/{basket.Id}/checkout", Checkout())).StatusCode);
        }
        finally { factory.Email.Fail = false; }
        Assert.Equal(2, (await Read<PagedResult<OrderSummaryDto>>(await shopper.GetAsync("/api/v1/storefront/orders"))).TotalCount);

        // Repeating a checkout that already happened does not email again.
        var again = await shopper.PostAsJsonAsync($"/api/v1/storefront/baskets/{firstBasket.Id}/checkout", Checkout());
        Assert.Equal(first.Id, (await Read<OrderDto>(again)).Id);
        Assert.Single(factory.Email.To(email));
    }

    [Fact]
    public async Task Order_history_needs_a_shopper_token()
    {
        var staff = await factory.CreateAuthenticatedApiClientAsync();
        Assert.Equal(HttpStatusCode.Unauthorized, (await staff.GetAsync("/api/v1/storefront/orders")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await staff.PostAsync("/api/v1/storefront/baskets", null)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await factory.CreateClient().GetAsync("/api/v1/storefront/orders")).StatusCode);
    }
}
