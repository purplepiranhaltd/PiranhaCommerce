using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using PiranhaCommerce.Catalog;
using PiranhaCommerce.Core.Paging;
using PiranhaCommerce.Customers;
using PiranhaCommerce.Data;
using PiranhaCommerce.Ecommerce;

namespace PiranhaCommerce.IntegrationTests;

/// <summary>Unpaid orders stop holding stock: cancelled after a while, but never one that has had a payment or has moved on.</summary>
[Collection(OrderSettings.Name)]
public class UnpaidOrderExpiryTests(PiranhaCommerceFactory factory) : IClassFixture<PiranhaCommerceFactory>
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };
    private static string Tag() => Guid.NewGuid().ToString("N")[..8];
    private static async Task<T> Read<T>(HttpResponseMessage r) => (await r.Content.ReadFromJsonAsync<T>(Json))!;

    private static async Task<(OrderDto Order, ProductDto Product)> Placed(HttpClient staff, decimal price = 10m)
    {
        var p = await Read<ProductDto>(await staff.PostAsJsonAsync("/api/v1/products", new ProductInput { Name = "X " + Tag(), Sku = "X-" + Tag(), Price = price }));
        await staff.PutAsJsonAsync($"/api/v1/products/{p.Id}/listing", new ProductListingInput { Published = true });
        await staff.PostAsJsonAsync($"/api/v1/products/{p.Id}/stock/adjustments", new StockAdjustmentInput { Delta = 5, Reason = "Delivery" });
        var b = await Read<BasketDto>(await staff.PostAsJsonAsync("/api/v1/baskets", new BasketCreateInput()));
        await staff.PutAsJsonAsync($"/api/v1/baskets/{b.Id}/lines/{p.Id}", new BasketLineInput { Quantity = 2 });
        var order = await Read<OrderDto>(await staff.PostAsJsonAsync($"/api/v1/baskets/{b.Id}/checkout", new CheckoutInput
        {
            Email = $"x-{Tag()}@example.com",
            ShippingAddress = new AddressInput { Recipient = "Ada", Line1 = "1 High St", City = "Leeds", PostalCode = "LS1", CountryCode = "GB" },
        }));
        return (order, p);
    }

    private async Task Age(Guid orderId, TimeSpan by)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PiranhaCommerceDbContext>();
        await db.Set<Order>().Where(o => o.Id == orderId).ExecuteUpdateAsync(s => s.SetProperty(o => o.CreatedUtc, DateTimeOffset.UtcNow - by));
    }

    private async Task<int> Expire(TimeSpan olderThan)
    {
        using var scope = factory.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<IUnpaidOrderExpiry>().ExpireAsync(olderThan);
    }

    private static async Task<decimal> OnHand(HttpClient staff, Guid productId) => (await Read<StockLevelDto>(await staff.GetAsync($"/api/v1/products/{productId}/stock"))).Quantity;
    private static async Task<OrderStage> StageOf(HttpClient staff, Guid orderId) => (await Read<OrderDto>(await staff.GetAsync($"/api/v1/orders/{orderId}"))).Stage;

    [Fact]
    public async Task An_old_unpaid_order_is_cancelled_its_stock_comes_back_and_the_buyer_is_told()
    {
        var staff = await factory.CreateAuthenticatedApiClientAsync();
        var (order, product) = await Placed(staff);
        Assert.Equal(3m, await OnHand(staff, product.Id)); // 5 - 2 held by the order
        await Age(order.Id, TimeSpan.FromHours(30));

        Assert.True(await Expire(TimeSpan.FromHours(24)) >= 1);

        Assert.Equal(OrderStage.Cancelled, await StageOf(staff, order.Id));
        Assert.Equal(5m, await OnHand(staff, product.Id));
        var moves = await Read<PagedResult<StockMovementDto>>(await staff.GetAsync($"/api/v1/products/{product.Id}/stock/movements"));
        Assert.Contains(moves.Items, m => m.Reason == "Order cancelled" && m.Actor == "system (unpaid order expired)");
        Assert.Contains(factory.Email.To(order.Email), m => m.TemplateKey == "order.cancelled");

        Assert.Equal(0, await Expire(TimeSpan.FromHours(24))); // nothing left to do, and running again is harmless
    }

    [Fact]
    public async Task A_recent_order_a_paid_order_and_an_order_in_progress_are_left_alone()
    {
        var staff = await factory.CreateAuthenticatedApiClientAsync();

        var (recent, _) = await Placed(staff);
        await Age(recent.Id, TimeSpan.FromHours(1));

        var (paid, _) = await Placed(staff);
        await staff.PostAsJsonAsync($"/api/v1/orders/{paid.Id}/payments", new PaymentInput { Amount = 1m, Provider = "manual", Reference = "r-" + Tag() }); // even part-paid
        await Age(paid.Id, TimeSpan.FromHours(48));

        var (moving, _) = await Placed(staff);
        await staff.PostAsJsonAsync($"/api/v1/orders/{moving.Id}/stage", new OrderStageInput { Stage = OrderStage.Processing }, Json);
        await Age(moving.Id, TimeSpan.FromHours(48));

        await Expire(TimeSpan.FromHours(24));

        Assert.Equal(OrderStage.Placed, await StageOf(staff, recent.Id));
        Assert.Equal(OrderStage.Placed, await StageOf(staff, paid.Id));
        Assert.Equal(OrderStage.Processing, await StageOf(staff, moving.Id));
    }

    [Fact]
    public async Task Zero_or_negative_ages_do_nothing_and_a_payment_that_arrives_after_expiry_is_refused()
    {
        var staff = await factory.CreateAuthenticatedApiClientAsync();
        var (order, _) = await Placed(staff);
        await Age(order.Id, TimeSpan.FromDays(10));
        Assert.Equal(0, await Expire(TimeSpan.Zero));
        Assert.Equal(0, await Expire(TimeSpan.FromHours(-5)));
        Assert.Equal(OrderStage.Placed, await StageOf(staff, order.Id));

        await Expire(TimeSpan.FromHours(24));
        Assert.Equal(OrderStage.Cancelled, await StageOf(staff, order.Id));
        var late = await staff.PostAsJsonAsync($"/api/v1/orders/{order.Id}/payments", new PaymentInput { Amount = order.Total, Provider = "manual", Reference = "late-" + Tag() });
        Assert.Equal(System.Net.HttpStatusCode.Conflict, late.StatusCode);
    }

    [Fact]
    public async Task Expiring_while_a_payment_is_being_recorded_never_leaves_a_paid_order_cancelled()
    {
        var staff = await factory.CreateAuthenticatedApiClientAsync();
        for (var i = 0; i < 4; i++)
        {
            var (order, _) = await Placed(staff);
            await Age(order.Id, TimeSpan.FromHours(48));
            var pay = staff.PostAsJsonAsync($"/api/v1/orders/{order.Id}/payments", new PaymentInput { Amount = order.Total, Provider = "manual", Reference = "race-" + Tag() });
            var expire = Expire(TimeSpan.FromHours(24));
            await Task.WhenAll(pay, expire);

            var paid = (await pay).IsSuccessStatusCode;
            var stage = await StageOf(staff, order.Id);
            // Either the payment won (order stays Placed, paid) or the cancel did (payment refused). Never paid and cancelled.
            if (paid) Assert.Equal(OrderStage.Placed, stage);
            else Assert.Equal(OrderStage.Cancelled, stage);
        }
    }
}
