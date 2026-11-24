using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using PiranhaCommerce.Catalog;
using PiranhaCommerce.Customers;
using PiranhaCommerce.Data;
using PiranhaCommerce.Ecommerce;

namespace PiranhaCommerce.IntegrationTests;

/// <summary>Abandoned open baskets are cleared away; anything in use, and every order's own basket, is left alone.</summary>
[Collection(OrderSettings.Name)]
public class BasketHousekeepingTests(PiranhaCommerceFactory factory) : IClassFixture<PiranhaCommerceFactory>
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };
    private static string Tag() => Guid.NewGuid().ToString("N")[..8];
    private static async Task<T> Read<T>(HttpResponseMessage r) => (await r.Content.ReadFromJsonAsync<T>(Json))!;

    private async Task BackdateAsync(Guid basketId, int daysAgo)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PiranhaCommerceDbContext>();
        var when = DateTimeOffset.UtcNow.AddDays(-daysAgo);
        await db.Set<Basket>().Where(b => b.Id == basketId).ExecuteUpdateAsync(s => s.SetProperty(b => b.CreatedUtc, when).SetProperty(b => b.UpdatedUtc, (DateTimeOffset?)when));
    }

    private async Task<int> RunAsync(int days)
    {
        using var scope = factory.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<IBasketHousekeeper>().RemoveAbandonedAsync(TimeSpan.FromDays(days));
    }

    [Fact]
    public async Task Old_untouched_open_baskets_go_with_their_lines_and_everything_else_stays()
    {
        var staff = await factory.CreateAuthenticatedApiClientAsync();
        var p = await Read<ProductDto>(await staff.PostAsJsonAsync("/api/v1/products", new ProductInput { Name = "H " + Tag(), Sku = "H-" + Tag(), Price = 5m }));
        await staff.PutAsJsonAsync($"/api/v1/products/{p.Id}/listing", new ProductListingInput { Published = true });
        await staff.PostAsJsonAsync($"/api/v1/products/{p.Id}/stock/adjustments", new StockAdjustmentInput { Delta = 10, Reason = "Delivery" });

        async Task<BasketDto> Open(bool withLine = true)
        {
            var b = await Read<BasketDto>(await staff.PostAsJsonAsync("/api/v1/baskets", new BasketCreateInput()));
            if (withLine) await staff.PutAsJsonAsync($"/api/v1/baskets/{b.Id}/lines/{p.Id}", new BasketLineInput { Quantity = 1 });
            return b;
        }

        var stale = await Open();
        var staleEmpty = await Open(withLine: false);
        var recent = await Open();
        var touched = await Open();
        var ordered = await Open();
        var checkout = await staff.PostAsJsonAsync($"/api/v1/baskets/{ordered.Id}/checkout", new CheckoutInput
        {
            Email = $"h-{Tag()}@example.com",
            ShippingAddress = new AddressInput { Recipient = "Ada", Line1 = "1 High St", City = "Leeds", PostalCode = "LS1", CountryCode = "GB" },
        });
        Assert.Equal(HttpStatusCode.OK, checkout.StatusCode);

        foreach (var b in new[] { stale, staleEmpty, touched, ordered }) await BackdateAsync(b.Id, 90);
        await BackdateAsync(recent.Id, 10);
        // Someone returns to an old basket: changing it counts as being active.
        Assert.Equal(HttpStatusCode.OK, (await staff.PutAsJsonAsync($"/api/v1/baskets/{touched.Id}/lines/{p.Id}", new BasketLineInput { Quantity = 2 })).StatusCode);

        Assert.True(await RunAsync(60) >= 2);

        Assert.Equal(HttpStatusCode.NotFound, (await staff.GetAsync($"/api/v1/baskets/{stale.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await staff.GetAsync($"/api/v1/baskets/{staleEmpty.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await staff.GetAsync($"/api/v1/baskets/{recent.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await staff.GetAsync($"/api/v1/baskets/{touched.Id}")).StatusCode);
        var kept = await Read<BasketDto>(await staff.GetAsync($"/api/v1/baskets/{ordered.Id}")); // checked out: the order's own record
        Assert.Equal(BasketStatus.CheckedOut, kept.Status);

        // Its lines went too, not left behind.
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PiranhaCommerceDbContext>();
        Assert.False(await db.Set<BasketLine>().AnyAsync(l => l.BasketId == stale.Id));

        Assert.Equal(0, await RunAsync(60)); // nothing more to do
    }

    [Fact]
    public async Task A_zero_or_negative_age_never_clears_baskets_and_the_option_defaults_to_sixty_days()
    {
        var staff = await factory.CreateAuthenticatedApiClientAsync();
        var b = await Read<BasketDto>(await staff.PostAsJsonAsync("/api/v1/baskets", new BasketCreateInput()));
        Assert.Equal(0, await RunAsync(0));
        Assert.Equal(0, await RunAsync(-5));
        Assert.Equal(HttpStatusCode.OK, (await staff.GetAsync($"/api/v1/baskets/{b.Id}")).StatusCode);
        Assert.Equal(60, new EcommerceOptions().AbandonedBasketDays);
    }
}
