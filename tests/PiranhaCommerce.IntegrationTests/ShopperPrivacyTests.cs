using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using PiranhaCommerce.Api.Auth;
using PiranhaCommerce.Catalog;
using PiranhaCommerce.Customers;
using PiranhaCommerce.Data;
using PiranhaCommerce.Ecommerce;

namespace PiranhaCommerce.IntegrationTests;

/// <summary>A shopper seeing everything held about them, and erasing their own account (password required).</summary>
[Collection(OrderSettings.Name)]
public class ShopperPrivacyTests(PiranhaFactory factory) : IClassFixture<PiranhaFactory>
{
    private const string Password = "a-long-enough-password";
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };
    private static string Tag() => Guid.NewGuid().ToString("N")[..10];
    private static async Task<T> Read<T>(HttpResponseMessage r) => (await r.Content.ReadFromJsonAsync<T>(Json))!;

    private sealed record Shopper(HttpClient Client, Guid Id, string Email);

    private async Task<Shopper> NewShopper()
    {
        var email = $"self-{Tag()}@example.com";
        var client = factory.CreateClient();
        var reg = await Read<RegisteredDto>(await client.PostAsJsonAsync("/api/v1/storefront/auth/register", new RegisterInput { Email = email, Password = Password, FirstName = "Ada" }));
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", reg.Token.AccessToken);
        return new Shopper(client, reg.Profile.Id, email);
    }

    private static async Task<OrderDto> PlaceOrder(HttpClient staff, Shopper s)
    {
        var p = await Read<ProductDto>(await staff.PostAsJsonAsync("/api/v1/products", new ProductInput { Name = "Self " + Tag(), Sku = "SF-" + Tag(), Price = 10m }));
        await staff.PutAsJsonAsync($"/api/v1/products/{p.Id}/listing", new ProductListingInput { Published = true });
        await staff.PostAsJsonAsync($"/api/v1/products/{p.Id}/stock/adjustments", new StockAdjustmentInput { Delta = 10, Reason = "Delivery" });
        var basket = await Read<BasketDto>(await s.Client.PostAsync("/api/v1/storefront/baskets", null));
        await s.Client.PutAsJsonAsync($"/api/v1/storefront/baskets/{basket.Id}/lines/{p.Id}", new BasketLineInput { Quantity = 1 });
        var r = await s.Client.PostAsJsonAsync($"/api/v1/storefront/baskets/{basket.Id}/checkout", new CheckoutInput
        {
            Email = s.Email,
            ShippingAddress = new AddressInput { Recipient = "Ada Lovelace", Line1 = "10 Secret Lane", City = "Leeds", PostalCode = "LS1 1AA", CountryCode = "GB" },
        });
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        return await Read<OrderDto>(r);
    }

    [Fact]
    public async Task A_shopper_can_export_their_own_data_and_only_theirs_and_never_the_password()
    {
        var staff = await factory.CreateAuthenticatedApiClientAsync();
        var ada = await NewShopper();
        var bob = await NewShopper();
        var adaOrder = await PlaceOrder(staff, ada);
        var bobOrder = await PlaceOrder(staff, bob);

        var response = await ada.Client.GetAsync("/api/v1/storefront/me/export");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var raw = await response.Content.ReadAsStringAsync();
        Assert.Contains(ada.Email, raw);
        Assert.Contains(adaOrder.Reference, raw);
        Assert.DoesNotContain(bobOrder.Reference, raw);
        Assert.DoesNotContain(bob.Email, raw);
        Assert.DoesNotContain("passwordHash", raw, StringComparison.OrdinalIgnoreCase);

        Assert.Equal(HttpStatusCode.Unauthorized, (await factory.CreateClient().GetAsync("/api/v1/storefront/me/export")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await staff.GetAsync("/api/v1/storefront/me/export")).StatusCode); // a staff token is not a shopper's
    }

    [Fact]
    public async Task Erasing_your_own_account_needs_the_password_and_then_anonymises_the_orders_and_ends_the_session()
    {
        var staff = await factory.CreateAuthenticatedApiClientAsync();
        var s = await NewShopper();
        var order = await PlaceOrder(staff, s);
        await staff.PostAsJsonAsync($"/api/v1/orders/{order.Id}/stage", new OrderStageInput { Stage = OrderStage.Cancelled }, Json); // nothing open or owed

        // Wrong password, and no password: refused, nothing changed.
        var wrong = await s.Client.PostAsJsonAsync("/api/v1/storefront/me/erase", new EraseOwnAccountInput { Password = "not-my-password" });
        Assert.Equal(HttpStatusCode.BadRequest, wrong.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await s.Client.PostAsJsonAsync("/api/v1/storefront/me/erase", new EraseOwnAccountInput { Password = "" })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await s.Client.GetAsync("/api/v1/storefront/me")).StatusCode);
        Assert.Equal(s.Email, (await Read<OrderDto>(await staff.GetAsync($"/api/v1/orders/{order.Id}"))).Email);

        Assert.Equal(HttpStatusCode.NoContent, (await s.Client.PostAsJsonAsync("/api/v1/storefront/me/erase", new EraseOwnAccountInput { Password = Password })).StatusCode);

        Assert.Equal(HttpStatusCode.Unauthorized, (await s.Client.GetAsync("/api/v1/storefront/me")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await factory.CreateClient().PostAsJsonAsync("/api/v1/storefront/auth/token", new SignInInput(s.Email, Password))).StatusCode);
        var after = await Read<OrderDto>(await staff.GetAsync($"/api/v1/orders/{order.Id}"));
        Assert.Equal(("erased@erased.invalid", "Erased", order.Total), (after.Email, after.ShippingAddress!.Line1, after.Total));

        using var scope = factory.Services.CreateScope();
        var record = await scope.ServiceProvider.GetRequiredService<PiranhaDbContext>().Set<CustomerErasure>().AsNoTracking().SingleAsync(e => e.CustomerId == s.Id);
        Assert.Equal("customer (self-service)", record.Actor);
    }

    [Fact]
    public async Task Erasing_your_own_account_is_refused_while_an_order_is_open_and_nothing_changes()
    {
        var staff = await factory.CreateAuthenticatedApiClientAsync();
        var s = await NewShopper();
        var order = await PlaceOrder(staff, s); // Placed: still to be fulfilled

        var refused = await s.Client.PostAsJsonAsync("/api/v1/storefront/me/erase", new EraseOwnAccountInput { Password = Password });
        Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
        Assert.Contains(order.Reference, await refused.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.OK, (await s.Client.GetAsync("/api/v1/storefront/me")).StatusCode);
    }

    [Fact]
    public async Task Wrong_passwords_on_the_erase_form_count_towards_a_lockout()
    {
        var s = await NewShopper();
        for (var i = 0; i < 5; i++)
            await s.Client.PostAsJsonAsync("/api/v1/storefront/me/erase", new EraseOwnAccountInput { Password = "wrong-password-" + i });

        // Locked: even the right password is refused now, so a stolen token cannot be used to guess it.
        Assert.Equal(HttpStatusCode.BadRequest, (await s.Client.PostAsJsonAsync("/api/v1/storefront/me/erase", new EraseOwnAccountInput { Password = Password })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await s.Client.GetAsync("/api/v1/storefront/me")).StatusCode);
    }
}
