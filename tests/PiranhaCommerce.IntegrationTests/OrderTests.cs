using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using PiranhaCommerce.Catalog;
using PiranhaCommerce.Core.Paging;
using PiranhaCommerce.Customers;
using PiranhaCommerce.Ecommerce;

namespace PiranhaCommerce.IntegrationTests;

[Collection(OrderSettings.Name)]
public class OrderTests(PiranhaFactory factory) : IClassFixture<PiranhaFactory>
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };

    private static string Tag() => Guid.NewGuid().ToString("N")[..10];

    private static async Task<T> Read<T>(HttpResponseMessage r) => (await r.Content.ReadFromJsonAsync<T>(Json))!;

    private static async Task<ProductDto> NewProduct(HttpClient c, decimal price = 10m, int? stock = 5, bool published = true)
    {
        var p = await Read<ProductDto>(await c.PostAsJsonAsync("/api/v1/products", new ProductInput { Name = "Order " + Tag(), Sku = "SKU-" + Tag(), Price = price }));
        if (published) Assert.Equal(HttpStatusCode.OK, (await c.PutAsJsonAsync($"/api/v1/products/{p.Id}/listing", new ProductListingInput { Published = true })).StatusCode);
        if (stock is > 0)
            Assert.Equal(HttpStatusCode.OK, (await c.PostAsJsonAsync($"/api/v1/products/{p.Id}/stock/adjustments",
                new StockAdjustmentInput { Delta = stock.Value, Reason = "Delivery" })).StatusCode);
        return p;
    }

    private static async Task<BasketDto> NewBasket(HttpClient c, params (ProductDto Product, int Quantity)[] lines)
    {
        var r = await c.PostAsJsonAsync("/api/v1/baskets", new BasketCreateInput());
        Assert.Equal(HttpStatusCode.Created, r.StatusCode);
        var basket = await Read<BasketDto>(r);
        foreach (var (product, quantity) in lines)
            Assert.Equal(HttpStatusCode.OK, (await c.PutAsJsonAsync($"/api/v1/baskets/{basket.Id}/lines/{product.Id}", new BasketLineInput { Quantity = quantity })).StatusCode);
        return basket;
    }

    private static CheckoutInput Checkout(string? email = null) => new()
    {
        Email = email ?? $"buyer-{Tag()}@example.com",
        ShippingAddress = new AddressInput { Recipient = "Ada Buyer", Line1 = "1 High St", City = "Leeds", PostalCode = "LS1 1AA", CountryCode = "gb" },
    };

    private static Task<HttpResponseMessage> Place(HttpClient c, Guid basketId, CheckoutInput? input = null) =>
        c.PostAsJsonAsync($"/api/v1/baskets/{basketId}/checkout", input ?? Checkout());

    private static async Task<decimal> Stock(HttpClient c, Guid productId) =>
        (await c.GetFromJsonAsync<StockLevelDto>($"/api/v1/products/{productId}/stock"))!.Quantity;

    [Fact]
    public async Task Basket_lines_are_added_changed_and_removed_with_live_prices()
    {
        var c = await factory.CreateAuthenticatedApiClientAsync();
        var a = await NewProduct(c, 4.50m);
        var b = await NewProduct(c, 10m);
        var basket = await NewBasket(c, (a, 2), (b, 1));

        var read = await Read<BasketDto>(await c.GetAsync($"/api/v1/baskets/{basket.Id}"));
        Assert.Equal(19m, read.Subtotal);
        Assert.Equal(2, read.Lines.Count);

        // changing the product's price changes the basket: nothing is frozen until checkout
        var update = new ProductInput { Name = a.Name, Sku = a.Sku, Price = 5m };
        Assert.Equal(HttpStatusCode.OK, (await c.PutAsJsonAsync($"/api/v1/products/{a.Id}", update)).StatusCode);
        Assert.Equal(20m, (await Read<BasketDto>(await c.GetAsync($"/api/v1/baskets/{basket.Id}"))).Subtotal);

        var changed = await Read<BasketDto>(await c.PutAsJsonAsync($"/api/v1/baskets/{basket.Id}/lines/{a.Id}", new BasketLineInput { Quantity = 4 }));
        Assert.Equal(30m, changed.Subtotal);
        Assert.Equal(2, changed.Lines.Count); // set, not added twice

        var removed = await Read<BasketDto>(await c.DeleteAsync($"/api/v1/baskets/{basket.Id}/lines/{a.Id}"));
        Assert.Single(removed.Lines);
        Assert.Equal(HttpStatusCode.NotFound, (await c.GetAsync($"/api/v1/baskets/{Guid.NewGuid()}")).StatusCode);
    }

    [Fact]
    public async Task Only_published_products_can_be_added_and_bad_quantities_are_refused()
    {
        var c = await factory.CreateAuthenticatedApiClientAsync();
        var hidden = await NewProduct(c, published: false);
        var shown = await NewProduct(c);
        var basket = await NewBasket(c);

        Assert.Equal(HttpStatusCode.BadRequest, (await c.PutAsJsonAsync($"/api/v1/baskets/{basket.Id}/lines/{hidden.Id}", new BasketLineInput { Quantity = 1 })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await c.PutAsJsonAsync($"/api/v1/baskets/{basket.Id}/lines/{Guid.NewGuid()}", new BasketLineInput { Quantity = 1 })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await c.PutAsJsonAsync($"/api/v1/baskets/{basket.Id}/lines/{shown.Id}", new BasketLineInput { Quantity = -1 })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await c.PutAsJsonAsync($"/api/v1/baskets/{basket.Id}/lines/{shown.Id}", new BasketLineInput { Quantity = 10_001 })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await c.PostAsJsonAsync("/api/v1/baskets", new BasketCreateInput { CustomerId = Guid.NewGuid() })).StatusCode);
    }

    [Fact]
    public async Task Simultaneous_adds_of_the_same_product_leave_one_line()
    {
        var c = await factory.CreateAuthenticatedApiClientAsync();
        var p = await NewProduct(c);
        var basket = await NewBasket(c);

        var results = await Task.WhenAll(Enumerable.Range(1, 8).Select(i =>
            c.PutAsJsonAsync($"/api/v1/baskets/{basket.Id}/lines/{p.Id}", new BasketLineInput { Quantity = i })));
        Assert.All(results, r => Assert.Equal(HttpStatusCode.OK, r.StatusCode));

        var read = await Read<BasketDto>(await c.GetAsync($"/api/v1/baskets/{basket.Id}"));
        Assert.Single(read.Lines);
    }

    [Fact]
    public async Task Checkout_copies_everything_takes_stock_and_numbers_the_order()
    {
        var c = await factory.CreateAuthenticatedApiClientAsync();
        var a = await NewProduct(c, 4.50m, stock: 10);
        var b = await NewProduct(c, 10m, stock: 3);
        var basket = await NewBasket(c, (a, 2), (b, 1));

        var r = await Place(c, basket.Id, Checkout("Buyer@Example.com"));
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        var order = await Read<OrderDto>(r);

        Assert.Equal(OrderStage.Placed, order.Stage);
        Assert.Equal(19m, order.Total);
        Assert.Equal("GBP", order.Currency);
        Assert.Equal($"ORD-{order.Number}", order.Reference);
        Assert.True(order.Number >= OrderNumbers.First);
        Assert.Equal("GB", order.ShippingAddress!.CountryCode);
        Assert.Equal(order.ShippingAddress, order.BillingAddress); // defaults to shipping
        Assert.Equal([OrderStage.Processing, OrderStage.Cancelled], order.NextStages);
        Assert.Equal(2, order.Lines.Count);
        Assert.Equal(9m, order.Lines.Sum(l => l.LineTotal) - 10m);

        Assert.Equal(8m, await Stock(c, a.Id));
        Assert.Equal(2m, await Stock(c, b.Id));
        var moves = await Read<PagedResult<StockMovementDto>>(await c.GetAsync($"/api/v1/products/{a.Id}/stock/movements"));
        Assert.Contains(moves.Items, m => m.Delta == -2m && m.Reference == order.Reference && m.Balance == 8m);

        // later edits to the product or its price never change what was sold
        await c.PutAsJsonAsync($"/api/v1/products/{a.Id}", new ProductInput { Name = "Renamed", Sku = a.Sku, Price = 99m });
        var again = await Read<OrderDto>(await c.GetAsync($"/api/v1/orders/{order.Id}"));
        Assert.Equal(19m, again.Total);
        Assert.Equal(a.Name, again.Lines.Single(l => l.ProductId == a.Id).Name);
        Assert.Equal(4.50m, again.Lines.Single(l => l.ProductId == a.Id).UnitPrice);

        var byNumber = await Read<OrderDto>(await c.GetAsync($"/api/v1/orders/by-number/{order.Number}"));
        Assert.Equal(order.Id, byNumber.Id);

        // the basket is spent
        var basketNow = await Read<BasketDto>(await c.GetAsync($"/api/v1/baskets/{basket.Id}"));
        Assert.Equal(BasketStatus.CheckedOut, basketNow.Status);
        Assert.Equal(order.Id, basketNow.OrderId);
        Assert.Equal(HttpStatusCode.Conflict, (await c.PutAsJsonAsync($"/api/v1/baskets/{basket.Id}/lines/{a.Id}", new BasketLineInput { Quantity = 1 })).StatusCode);
    }

    [Fact]
    public async Task Checking_out_the_same_basket_again_returns_the_same_order_without_taking_more_stock()
    {
        var c = await factory.CreateAuthenticatedApiClientAsync();
        var p = await NewProduct(c, stock: 10);
        var basket = await NewBasket(c, (p, 3));

        var first = await Read<OrderDto>(await Place(c, basket.Id));
        var second = await Read<OrderDto>(await Place(c, basket.Id));
        Assert.Equal(first.Id, second.Id);
        Assert.Equal(7m, await Stock(c, p.Id));
    }

    [Fact]
    public async Task Simultaneous_checkouts_of_one_basket_make_exactly_one_order()
    {
        var c = await factory.CreateAuthenticatedApiClientAsync();
        var p = await NewProduct(c, stock: 10);
        var basket = await NewBasket(c, (p, 2));

        var results = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => Place(c, basket.Id)));
        Assert.All(results, r => Assert.Equal(HttpStatusCode.OK, r.StatusCode));
        var orders = await Task.WhenAll(results.Select(Read<OrderDto>));
        Assert.Single(orders.Select(o => o.Id).Distinct());
        Assert.Equal(8m, await Stock(c, p.Id)); // taken once
    }

    [Fact]
    public async Task Simultaneous_checkouts_of_different_baskets_get_distinct_numbers()
    {
        var c = await factory.CreateAuthenticatedApiClientAsync();
        var p = await NewProduct(c, stock: 50);
        var baskets = await Task.WhenAll(Enumerable.Range(0, 12).Select(_ => NewBasket(c, (p, 1))));

        var results = await Task.WhenAll(baskets.Select(b => Place(c, b.Id)));
        Assert.All(results, r => Assert.Equal(HttpStatusCode.OK, r.StatusCode));
        var numbers = (await Task.WhenAll(results.Select(Read<OrderDto>))).Select(o => o.Number).ToList();
        Assert.Equal(12, numbers.Distinct().Count());
        Assert.Equal(38m, await Stock(c, p.Id));
    }

    [Fact]
    public async Task Only_as_many_baskets_succeed_as_there_is_stock_for()
    {
        var c = await factory.CreateAuthenticatedApiClientAsync();
        var p = await NewProduct(c, stock: 2);
        var baskets = await Task.WhenAll(Enumerable.Range(0, 6).Select(_ => NewBasket(c, (p, 1))));

        var results = await Task.WhenAll(baskets.Select(b => Place(c, b.Id)));
        Assert.Equal(2, results.Count(r => r.StatusCode == HttpStatusCode.OK));
        Assert.Equal(4, results.Count(r => r.StatusCode == HttpStatusCode.Conflict));
        Assert.Equal(0m, await Stock(c, p.Id)); // never negative
    }

    [Fact]
    public async Task Short_stock_on_one_line_leaves_everything_untouched()
    {
        var c = await factory.CreateAuthenticatedApiClientAsync();
        var plenty = await NewProduct(c, stock: 10);
        var scarce = await NewProduct(c, stock: 1);
        var basket = await NewBasket(c, (plenty, 2), (scarce, 2));

        var ordersBefore = (await Read<PagedResult<OrderSummaryDto>>(await c.GetAsync("/api/v1/orders?pageSize=1"))).TotalCount;
        Assert.Equal(HttpStatusCode.Conflict, (await Place(c, basket.Id)).StatusCode);

        Assert.Equal(10m, await Stock(c, plenty.Id)); // all or nothing
        Assert.Equal(1m, await Stock(c, scarce.Id));
        Assert.Equal(BasketStatus.Open, (await Read<BasketDto>(await c.GetAsync($"/api/v1/baskets/{basket.Id}"))).Status);
        Assert.True(ordersBefore <= (await Read<PagedResult<OrderSummaryDto>>(await c.GetAsync("/api/v1/orders?pageSize=1"))).TotalCount);
        Assert.Empty((await Read<PagedResult<OrderSummaryDto>>(await c.GetAsync($"/api/v1/orders?search={Uri.EscapeDataString(Checkout().Email)}"))).Items);

        // fix the basket and it goes through
        await c.PutAsJsonAsync($"/api/v1/baskets/{basket.Id}/lines/{scarce.Id}", new BasketLineInput { Quantity = 1 });
        Assert.Equal(HttpStatusCode.OK, (await Place(c, basket.Id)).StatusCode);
    }

    [Fact]
    public async Task Checkout_refuses_bad_input_empty_baskets_and_products_no_longer_for_sale()
    {
        var c = await factory.CreateAuthenticatedApiClientAsync();
        var p = await NewProduct(c, stock: 5);

        var empty = await NewBasket(c);
        Assert.Equal(HttpStatusCode.Conflict, (await Place(c, empty.Id)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await Place(c, Guid.NewGuid())).StatusCode);

        var basket = await NewBasket(c, (p, 1));
        var noAddress = Checkout();
        noAddress.ShippingAddress = null;
        Assert.Equal(HttpStatusCode.BadRequest, (await Place(c, basket.Id, noAddress)).StatusCode);
        var badCountry = Checkout();
        badCountry.ShippingAddress!.CountryCode = "United Kingdom";
        var bad = await Place(c, basket.Id, badCountry);
        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);
        Assert.Contains("ShippingAddress.CountryCode", await bad.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.BadRequest, (await Place(c, basket.Id, Checkout("not-an-email"))).StatusCode);
        var unknownCustomer = Checkout();
        unknownCustomer.CustomerId = Guid.NewGuid();
        Assert.Equal(HttpStatusCode.BadRequest, (await Place(c, basket.Id, unknownCustomer)).StatusCode);

        // taken off sale after it was put in the basket
        await c.PutAsJsonAsync($"/api/v1/products/{p.Id}/listing", new ProductListingInput { Published = false });
        Assert.Equal(HttpStatusCode.Conflict, (await Place(c, basket.Id)).StatusCode);
        Assert.Equal(5m, await Stock(c, p.Id));
        Assert.Equal(BasketStatus.Open, (await Read<BasketDto>(await c.GetAsync($"/api/v1/baskets/{basket.Id}"))).Status);
    }

    [Fact]
    public async Task Cancelling_puts_stock_back_exactly_once_even_when_requested_simultaneously()
    {
        var c = await factory.CreateAuthenticatedApiClientAsync();
        var p = await NewProduct(c, stock: 10);
        var order = await Read<OrderDto>(await Place(c, (await NewBasket(c, (p, 4))).Id));
        Assert.Equal(6m, await Stock(c, p.Id));

        var results = await Task.WhenAll(Enumerable.Range(0, 6).Select(_ =>
            c.PostAsJsonAsync($"/api/v1/orders/{order.Id}/stage", new OrderStageInput { Stage = OrderStage.Cancelled }, Json)));
        Assert.Equal(1, results.Count(r => r.StatusCode == HttpStatusCode.OK));
        Assert.Equal(5, results.Count(r => r.StatusCode == HttpStatusCode.Conflict));
        Assert.Equal(10m, await Stock(c, p.Id));

        var moves = await Read<PagedResult<StockMovementDto>>(await c.GetAsync($"/api/v1/products/{p.Id}/stock/movements"));
        Assert.Equal(1, moves.Items.Count(m => m.Reason == "Order cancelled" && m.Reference == order.Reference));
        Assert.Equal(OrderStage.Cancelled, (await Read<OrderDto>(await c.GetAsync($"/api/v1/orders/{order.Id}"))).Stage);
    }

    [Fact]
    public async Task Orders_move_forward_only_along_allowed_stages()
    {
        var c = await factory.CreateAuthenticatedApiClientAsync();
        var p = await NewProduct(c, stock: 10);
        var order = await Read<OrderDto>(await Place(c, (await NewBasket(c, (p, 1))).Id));

        Task<HttpResponseMessage> Move(OrderStage to) => c.PostAsJsonAsync($"/api/v1/orders/{order.Id}/stage", new OrderStageInput { Stage = to }, Json);

        Assert.Equal(HttpStatusCode.Conflict, (await Move(OrderStage.Completed)).StatusCode); // cannot skip Processing
        Assert.Equal(HttpStatusCode.Conflict, (await Move(OrderStage.Placed)).StatusCode);
        Assert.Equal(OrderStage.Processing, (await Read<OrderDto>(await Move(OrderStage.Processing))).Stage);
        Assert.Equal(OrderStage.Completed, (await Read<OrderDto>(await Move(OrderStage.Completed))).Stage);
        Assert.Equal(HttpStatusCode.Conflict, (await Move(OrderStage.Cancelled)).StatusCode); // completed is final
        Assert.Equal(9m, await Stock(c, p.Id)); // not restocked
        Assert.Equal(HttpStatusCode.NotFound, (await c.PostAsJsonAsync($"/api/v1/orders/{Guid.NewGuid()}/stage", new OrderStageInput { Stage = OrderStage.Processing }, Json)).StatusCode);
    }

    [Fact]
    public async Task A_product_that_has_been_sold_cannot_be_deleted()
    {
        var c = await factory.CreateAuthenticatedApiClientAsync();
        var p = await NewProduct(c, stock: 3);
        await Place(c, (await NewBasket(c, (p, 1))).Id);
        Assert.Equal(HttpStatusCode.Conflict, (await c.DeleteAsync($"/api/v1/products/{p.Id}")).StatusCode);
    }

    [Fact]
    public async Task Deleting_a_customer_leaves_their_orders_and_baskets_intact()
    {
        var c = await factory.CreateAuthenticatedApiClientAsync();
        var customer = await Read<CustomerDto>(await c.PostAsJsonAsync("/api/v1/customers", new CustomerInput { Email = $"keep-{Tag()}@example.com", FirstName = "Keep" }));
        var p = await NewProduct(c, stock: 3);
        var basket = await Read<BasketDto>(await c.PostAsJsonAsync("/api/v1/baskets", new BasketCreateInput { CustomerId = customer.Id }));
        await c.PutAsJsonAsync($"/api/v1/baskets/{basket.Id}/lines/{p.Id}", new BasketLineInput { Quantity = 1 });

        var input = Checkout(customer.Email);
        input.CustomerId = customer.Id;
        var order = await Read<OrderDto>(await Place(c, basket.Id, input));
        Assert.Equal(customer.Id, order.CustomerId);

        Assert.Equal(HttpStatusCode.NoContent, (await c.DeleteAsync($"/api/v1/customers/{customer.Id}")).StatusCode);

        var kept = await Read<OrderDto>(await c.GetAsync($"/api/v1/orders/{order.Id}"));
        Assert.Equal(customer.Email, kept.Email); // the order carries its own copy
        Assert.Equal("Leeds", kept.ShippingAddress!.City);
    }

    [Fact]
    public async Task Orders_can_be_listed_filtered_and_searched()
    {
        var c = await factory.CreateAuthenticatedApiClientAsync();
        var p = await NewProduct(c, stock: 10);
        var email = $"find-{Tag()}@example.com";
        var order = await Read<OrderDto>(await Place(c, (await NewBasket(c, (p, 1))).Id, Checkout(email)));

        async Task<PagedResult<OrderSummaryDto>> List(string query) => await Read<PagedResult<OrderSummaryDto>>(await c.GetAsync("/api/v1/orders?" + query));

        Assert.Equal(order.Id, (await List("search=" + Uri.EscapeDataString(email))).Items.Single().Id);
        Assert.Equal(order.Id, (await List($"search=ORD-{order.Number}")).Items.Single().Id);
        Assert.Equal(order.Id, (await List($"search={order.Number}")).Items.Single().Id);
        Assert.Contains((await List("stage=Placed&pageSize=100")).Items, o => o.Id == order.Id);
        Assert.DoesNotContain((await List("stage=Cancelled&pageSize=100")).Items, o => o.Id == order.Id);
    }

    [Fact]
    public async Task Read_only_users_can_read_orders_but_not_place_or_change_them()
    {
        var admin = await factory.CreateAuthenticatedApiClientAsync();
        var p = await NewProduct(admin, stock: 5);
        var basket = await NewBasket(admin, (p, 1));
        var order = await Read<OrderDto>(await Place(admin, (await NewBasket(admin, (p, 1))).Id));

        var (email, password) = await factory.CreateReadOnlyUserAsync();
        var reader = await factory.CreateAuthenticatedApiClientAsync(email, password);

        Assert.Equal(HttpStatusCode.OK, (await reader.GetAsync($"/api/v1/orders/{order.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await Place(reader, basket.Id)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await reader.PostAsJsonAsync($"/api/v1/orders/{order.Id}/stage", new OrderStageInput { Stage = OrderStage.Processing }, Json)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await reader.PostAsJsonAsync("/api/v1/baskets", new BasketCreateInput())).StatusCode);
    }
}
