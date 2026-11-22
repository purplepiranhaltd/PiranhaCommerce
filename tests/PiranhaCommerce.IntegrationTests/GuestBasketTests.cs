using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using PiranhaCommerce.Catalog;
using PiranhaCommerce.Customers;
using PiranhaCommerce.Ecommerce;

namespace PiranhaCommerce.IntegrationTests;

/// <summary>Shopping and checking out without an account, and merging a guest basket into a signed-in shopper's own.</summary>
[Collection(OrderSettings.Name)]
public class GuestBasketTests(PiranhaFactory factory) : IClassFixture<PiranhaFactory>
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };
    private static string Tag() => Guid.NewGuid().ToString("N")[..8];
    private static async Task<T> Read<T>(HttpResponseMessage r) => (await r.Content.ReadFromJsonAsync<T>(Json))!;
    private const string Guests = "/api/v1/storefront/guest/baskets";

    private static async Task<ProductDto> NewProduct(HttpClient staff, decimal price = 10m, bool published = true, int stock = 100)
    {
        var p = await Read<ProductDto>(await staff.PostAsJsonAsync("/api/v1/products", new ProductInput { Name = "GB " + Tag(), Sku = "GB-" + Tag(), Price = price }));
        await staff.PutAsJsonAsync($"/api/v1/products/{p.Id}/listing", new ProductListingInput { Published = published });
        await staff.PostAsJsonAsync($"/api/v1/products/{p.Id}/stock/adjustments", new StockAdjustmentInput { Delta = stock, Reason = "Delivery" });
        return p;
    }

    private static async Task<BasketDto> NewGuestBasket(HttpClient anon)
    {
        var r = await anon.PostAsync(Guests, null);
        Assert.Equal(HttpStatusCode.Created, r.StatusCode);
        return await Read<BasketDto>(r);
    }

    private static Task<HttpResponseMessage> AddGuest(HttpClient anon, Guid basket, ProductDto p, int q) =>
        anon.PutAsJsonAsync($"{Guests}/{basket}/lines/{p.Id}", new BasketLineInput { Quantity = q });

    private static CheckoutInput Checkout(string? email = null) => new()
    {
        Email = email ?? $"guest-{Tag()}@example.com",
        ShippingAddress = new AddressInput { Recipient = "Ada", Line1 = "1 High St", City = "Leeds", PostalCode = "LS1", CountryCode = "GB" },
    };

    private async Task<(HttpClient Client, Guid Id)> NewShopper()
    {
        var client = factory.CreateClient();
        var reg = await Read<RegisteredDto>(await client.PostAsJsonAsync("/api/v1/storefront/auth/register", new RegisterInput { Email = $"gm-{Tag()}@example.com", Password = "a-long-enough-password" }));
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", reg.Token.AccessToken);
        return (client, reg.Profile.Id);
    }

    private static async Task<BasketDto> ShopperBasket(HttpClient shopper, params (ProductDto P, int Q)[] lines)
    {
        var b = await Read<BasketDto>(await shopper.PostAsync("/api/v1/storefront/baskets", null));
        foreach (var (p, q) in lines) await shopper.PutAsJsonAsync($"/api/v1/storefront/baskets/{b.Id}/lines/{p.Id}", new BasketLineInput { Quantity = q });
        return b;
    }

    private static async Task<BasketDto> Fetch(HttpClient shopper, Guid id) => await Read<BasketDto>(await shopper.GetAsync($"/api/v1/storefront/baskets/{id}"));

    // ---- guests ------------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task A_guest_can_fill_a_basket_get_a_quote_and_place_an_order_with_an_email_and_no_account()
    {
        var staff = await factory.CreateAuthenticatedApiClientAsync();
        var anon = factory.CreateClient();
        var p = await NewProduct(staff, 12.5m);

        var basket = await NewGuestBasket(anon);
        Assert.Null(basket.CustomerId);
        var filled = await Read<BasketDto>(await AddGuest(anon, basket.Id, p, 2));
        Assert.Equal(25m, filled.Subtotal);
        Assert.Equal(25m, (await Read<QuoteDto>(await anon.PostAsJsonAsync($"{Guests}/{basket.Id}/quote", new QuoteInput { CountryCode = "GB" }))).Subtotal);

        var email = $"guest-{Tag()}@example.com";
        var placed = await anon.PostAsJsonAsync($"{Guests}/{basket.Id}/checkout", Checkout(email));
        Assert.Equal(HttpStatusCode.OK, placed.StatusCode);
        var order = await Read<OrderDto>(placed);
        Assert.Null(order.CustomerId);
        Assert.Equal((email, 25m), (order.Email, order.Subtotal));
        Assert.Contains(factory.Email.To(email), m => m.TemplateKey == "order.confirmation");

        // The same basket again is the same order, and the basket can no longer change.
        Assert.Equal(order.Id, (await Read<OrderDto>(await anon.PostAsJsonAsync($"{Guests}/{basket.Id}/checkout", Checkout(email)))).Id);
        Assert.Equal(HttpStatusCode.Conflict, (await AddGuest(anon, basket.Id, p, 5)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await anon.GetAsync($"{Guests}/{basket.Id}")).StatusCode);
    }

    [Fact]
    public async Task Guest_routes_only_touch_baskets_with_no_owner_and_the_shopper_routes_still_need_a_token()
    {
        var staff = await factory.CreateAuthenticatedApiClientAsync();
        var anon = factory.CreateClient();
        var p = await NewProduct(staff);
        var (shopper, _) = await NewShopper();
        var owned = await ShopperBasket(shopper, (p, 1));

        Assert.Equal(HttpStatusCode.NotFound, (await anon.GetAsync($"{Guests}/{owned.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await AddGuest(anon, owned.Id, p, 3)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await anon.PostAsJsonAsync($"{Guests}/{owned.Id}/checkout", Checkout())).StatusCode);
        Assert.Equal(1, (await Fetch(shopper, owned.Id)).Lines.Single().Quantity); // untouched
        Assert.Equal(HttpStatusCode.NotFound, (await anon.GetAsync($"{Guests}/{Guid.NewGuid()}")).StatusCode);

        var guest = await NewGuestBasket(anon);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anon.GetAsync($"/api/v1/storefront/baskets/{guest.Id}")).StatusCode); // the signed-in routes still need a token
        Assert.Equal(HttpStatusCode.NotFound, (await shopper.GetAsync($"/api/v1/storefront/baskets/{guest.Id}")).StatusCode);  // and are for the owner only
    }

    [Fact]
    public async Task Guest_checkout_checks_the_email_and_address_ignores_a_customer_id_and_a_code_limited_to_a_group_is_refused()
    {
        var staff = await factory.CreateAuthenticatedApiClientAsync();
        var anon = factory.CreateClient();
        var p = await NewProduct(staff, 20m);
        var (_, memberId) = await NewShopper();

        var basket = await NewGuestBasket(anon);
        await AddGuest(anon, basket.Id, p, 1);
        Assert.Equal(HttpStatusCode.BadRequest, (await anon.PostAsJsonAsync($"{Guests}/{basket.Id}/checkout", Checkout("not-an-email"))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await anon.PostAsJsonAsync($"{Guests}/{basket.Id}/checkout", new CheckoutInput { Email = "a@example.com" })).StatusCode);

        // A promo code that anyone may use works; one limited to a group does not, and looks like any bad code.
        var open = "OPEN" + Tag().ToUpperInvariant(); var limited = "LTD" + Tag().ToUpperInvariant();
        await staff.PostAsJsonAsync("/api/v1/promotions", new PromotionInput { Code = open, Kind = PromotionKind.Percent, Value = 10 }, Json);
        var group = await Read<CustomerGroupDto>(await staff.PostAsJsonAsync("/api/v1/customer-groups", new CustomerGroupInput { Name = "G " + Tag() }));
        await staff.PostAsJsonAsync("/api/v1/promotions", new PromotionInput { Code = limited, Kind = PromotionKind.Percent, Value = 50, CustomerGroupId = group.Id }, Json);
        Assert.Equal(HttpStatusCode.OK, (await anon.PostAsJsonAsync($"{Guests}/{basket.Id}/promo", new CodeInput { Code = open })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await anon.PostAsJsonAsync($"{Guests}/{basket.Id}/promo", new CodeInput { Code = limited })).StatusCode);
        Assert.Equal(2m, (await Read<QuoteDto>(await anon.PostAsJsonAsync($"{Guests}/{basket.Id}/quote", new QuoteInput { CountryCode = "GB" }))).DiscountTotal);

        // A customer id in the body is dropped: the order belongs to nobody.
        var input = Checkout(); input.CustomerId = memberId;
        var order = await Read<OrderDto>(await anon.PostAsJsonAsync($"{Guests}/{basket.Id}/checkout", input));
        Assert.Null(order.CustomerId);
        Assert.Equal(18m, order.Subtotal - order.DiscountTotal);
    }

    [Fact]
    public async Task A_shop_can_turn_guest_checkout_off_and_guests_can_still_fill_a_basket()
    {
        await using var strict = new NoGuestFactory();
        var staff = await strict.CreateAuthenticatedApiClientAsync();
        var anon = strict.CreateClient();
        var p = await NewProduct(staff);
        var basket = await NewGuestBasket(anon);
        Assert.Equal(HttpStatusCode.OK, (await AddGuest(anon, basket.Id, p, 1)).StatusCode);
        var refused = await anon.PostAsJsonAsync($"{Guests}/{basket.Id}/checkout", Checkout());
        Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
        Assert.Contains("Sign in", await refused.Content.ReadAsStringAsync());
    }

    private sealed class NoGuestFactory : PiranhaFactory
    {
        protected override IReadOnlyDictionary<string, string?> ExtraSettings => new Dictionary<string, string?> { ["Piranha:Ecommerce:AllowGuestCheckout"] = "false" };
    }

    [Fact]
    public async Task Starting_guest_baskets_is_limited_per_client()
    {
        await using var limited = new GuestLimitFactory();
        var anon = limited.CreateClient();
        for (var i = 0; i < 3; i++) Assert.Equal(HttpStatusCode.Created, (await anon.PostAsync(Guests, null)).StatusCode);
        Assert.Equal(HttpStatusCode.TooManyRequests, (await anon.PostAsync(Guests, null)).StatusCode);
    }

    private sealed class GuestLimitFactory : PiranhaFactory
    {
        protected override IReadOnlyDictionary<string, string?> ExtraSettings => new Dictionary<string, string?> { ["Piranha:RateLimit:GuestBasketAttempts"] = "3" };
    }

    // ---- merging -----------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Merging_adds_quantities_keeps_what_is_only_in_one_basket_and_removes_the_guest_basket()
    {
        var staff = await factory.CreateAuthenticatedApiClientAsync();
        var anon = factory.CreateClient();
        var a = await NewProduct(staff); var b = await NewProduct(staff); var c = await NewProduct(staff);
        var (shopper, _) = await NewShopper();
        var mine = await ShopperBasket(shopper, (a, 1), (b, 2));

        var guest = await NewGuestBasket(anon);
        await AddGuest(anon, guest.Id, a, 2);
        await AddGuest(anon, guest.Id, c, 1);

        var merged = await shopper.PostAsJsonAsync($"/api/v1/storefront/baskets/{mine.Id}/merge", new MergeBasketInput { FromBasketId = guest.Id });
        Assert.Equal(HttpStatusCode.OK, merged.StatusCode);
        var basket = await Read<BasketDto>(merged);
        Assert.Equal(mine.Id, basket.Id);
        Assert.Equal(3, basket.Lines.Single(l => l.ProductId == a.Id).Quantity);
        Assert.Equal(2, basket.Lines.Single(l => l.ProductId == b.Id).Quantity);
        Assert.Equal(1, basket.Lines.Single(l => l.ProductId == c.Id).Quantity);

        Assert.Equal(HttpStatusCode.NotFound, (await anon.GetAsync($"{Guests}/{guest.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await shopper.PostAsJsonAsync($"/api/v1/storefront/baskets/{mine.Id}/merge", new MergeBasketInput { FromBasketId = guest.Id })).StatusCode); // already used
    }

    [Fact]
    public async Task Merging_caps_a_line_leaves_behind_what_can_no_longer_be_bought_and_brings_a_code_only_when_there_is_none()
    {
        var staff = await factory.CreateAuthenticatedApiClientAsync();
        var anon = factory.CreateClient();
        var p = await NewProduct(staff, 10m, stock: 100_000);
        var gone = await NewProduct(staff);
        var (shopper, _) = await NewShopper();
        var mine = await ShopperBasket(shopper, (p, BasketLineInput.Max - 1));

        var guest = await NewGuestBasket(anon);
        await AddGuest(anon, guest.Id, p, 5);
        await AddGuest(anon, guest.Id, gone, 1);
        await staff.PutAsJsonAsync($"/api/v1/products/{gone.Id}/listing", new ProductListingInput { Published = false }); // withdrawn meanwhile
        var code = "MRG" + Tag().ToUpperInvariant();
        await staff.PostAsJsonAsync("/api/v1/promotions", new PromotionInput { Code = code, Kind = PromotionKind.Percent, Value = 10 }, Json);
        await anon.PostAsJsonAsync($"{Guests}/{guest.Id}/promo", new CodeInput { Code = code });

        var merged = await Read<BasketDto>(await shopper.PostAsJsonAsync($"/api/v1/storefront/baskets/{mine.Id}/merge", new MergeBasketInput { FromBasketId = guest.Id }));
        Assert.Equal(BasketLineInput.Max, merged.Lines.Single(l => l.ProductId == p.Id).Quantity);
        Assert.DoesNotContain(merged.Lines, l => l.ProductId == gone.Id);
        Assert.Equal(code, merged.PromotionCode);

        // A basket that already has a code keeps it.
        var other = await ShopperBasket(shopper, (p, 1));
        var own = "OWN" + Tag().ToUpperInvariant();
        await staff.PostAsJsonAsync("/api/v1/promotions", new PromotionInput { Code = own, Kind = PromotionKind.Percent, Value = 5 }, Json);
        await shopper.PostAsJsonAsync($"/api/v1/storefront/baskets/{other.Id}/promo", new CodeInput { Code = own });
        var guest2 = await NewGuestBasket(anon);
        await AddGuest(anon, guest2.Id, p, 1);
        await anon.PostAsJsonAsync($"{Guests}/{guest2.Id}/promo", new CodeInput { Code = code });
        Assert.Equal(own, (await Read<BasketDto>(await shopper.PostAsJsonAsync($"/api/v1/storefront/baskets/{other.Id}/merge", new MergeBasketInput { FromBasketId = guest2.Id }))).PromotionCode);
    }

    [Fact]
    public async Task A_code_limited_to_a_group_does_not_come_along_for_someone_outside_it()
    {
        var staff = await factory.CreateAuthenticatedApiClientAsync();
        var anon = factory.CreateClient();
        var p = await NewProduct(staff);
        var group = await Read<CustomerGroupDto>(await staff.PostAsJsonAsync("/api/v1/customer-groups", new CustomerGroupInput { Name = "G " + Tag() }));
        var code = "GRP" + Tag().ToUpperInvariant();
        await staff.PostAsJsonAsync("/api/v1/promotions", new PromotionInput { Code = code, Kind = PromotionKind.Percent, Value = 10, CustomerGroupId = group.Id }, Json);

        var (member, memberId) = await NewShopper();
        await staff.PutAsync($"/api/v1/customer-groups/{group.Id}/members/{memberId}", null);
        var (outsider, _) = await NewShopper();

        // The code can only have been put on a guest basket before it was limited; set it directly to model that.
        foreach (var (shopper, expected) in new[] { (member, code), (outsider, (string?)null) })
        {
            var guest = await NewGuestBasket(anon);
            await AddGuest(anon, guest.Id, p, 1);
            using (var scope = factory.Services.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<PiranhaCommerce.Data.PiranhaDbContext>();
                await db.Set<Basket>().Where(b => b.Id == guest.Id).ExecuteUpdateAsync(s => s.SetProperty(b => b.PromotionCode, code));
            }
            var mine = await ShopperBasket(shopper);
            var merged = await Read<BasketDto>(await shopper.PostAsJsonAsync($"/api/v1/storefront/baskets/{mine.Id}/merge", new MergeBasketInput { FromBasketId = guest.Id }));
            Assert.Equal(expected, merged.PromotionCode);
        }
    }

    [Fact]
    public async Task Merge_refuses_a_basket_that_is_not_a_guest_basket_or_a_target_that_is_not_yours_and_changes_nothing()
    {
        var staff = await factory.CreateAuthenticatedApiClientAsync();
        var anon = factory.CreateClient();
        var p = await NewProduct(staff);
        var (alice, _) = await NewShopper();
        var (bob, _) = await NewShopper();
        var aliceBasket = await ShopperBasket(alice, (p, 1));
        var bobBasket = await ShopperBasket(bob, (p, 4));
        var guest = await NewGuestBasket(anon);
        await AddGuest(anon, guest.Id, p, 2);

        // Another shopper's basket cannot be pulled in; nor can it be merged into someone else's.
        Assert.Equal(HttpStatusCode.NotFound, (await alice.PostAsJsonAsync($"/api/v1/storefront/baskets/{aliceBasket.Id}/merge", new MergeBasketInput { FromBasketId = bobBasket.Id })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await alice.PostAsJsonAsync($"/api/v1/storefront/baskets/{bobBasket.Id}/merge", new MergeBasketInput { FromBasketId = guest.Id })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await alice.PostAsJsonAsync($"/api/v1/storefront/baskets/{aliceBasket.Id}/merge", new MergeBasketInput { FromBasketId = aliceBasket.Id })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await alice.PostAsJsonAsync($"/api/v1/storefront/baskets/{aliceBasket.Id}/merge", new MergeBasketInput { FromBasketId = Guid.NewGuid() })).StatusCode);
        Assert.Equal(1, (await Fetch(alice, aliceBasket.Id)).Lines.Single().Quantity);
        Assert.Equal(4, (await Fetch(bob, bobBasket.Id)).Lines.Single().Quantity);
        Assert.Equal(2, (await Read<BasketDto>(await anon.GetAsync($"{Guests}/{guest.Id}"))).Lines.Single().Quantity);

        // A guest basket that has already been checked out cannot be merged.
        var placed = await NewGuestBasket(anon);
        await AddGuest(anon, placed.Id, p, 1);
        await anon.PostAsJsonAsync($"{Guests}/{placed.Id}/checkout", Checkout());
        Assert.Equal(HttpStatusCode.NotFound, (await alice.PostAsJsonAsync($"/api/v1/storefront/baskets/{aliceBasket.Id}/merge", new MergeBasketInput { FromBasketId = placed.Id })).StatusCode);

        // Signing in is required.
        Assert.Equal(HttpStatusCode.Unauthorized, (await anon.PostAsJsonAsync($"/api/v1/storefront/baskets/{aliceBasket.Id}/merge", new MergeBasketInput { FromBasketId = guest.Id })).StatusCode);
    }

    [Fact]
    public async Task Two_merges_of_the_same_guest_basket_at_once_move_its_lines_exactly_once()
    {
        var staff = await factory.CreateAuthenticatedApiClientAsync();
        var anon = factory.CreateClient();
        var p = await NewProduct(staff);
        var (shopper, _) = await NewShopper();
        var one = await ShopperBasket(shopper, (p, 1));
        var two = await ShopperBasket(shopper);
        var guest = await NewGuestBasket(anon);
        await AddGuest(anon, guest.Id, p, 3);

        var results = await Task.WhenAll(
            shopper.PostAsJsonAsync($"/api/v1/storefront/baskets/{one.Id}/merge", new MergeBasketInput { FromBasketId = guest.Id }),
            shopper.PostAsJsonAsync($"/api/v1/storefront/baskets/{two.Id}/merge", new MergeBasketInput { FromBasketId = guest.Id }));
        Assert.Equal(1, results.Count(r => r.StatusCode == HttpStatusCode.OK));
        Assert.All(results.Where(r => r.StatusCode != HttpStatusCode.OK), r => Assert.True(r.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.Conflict));
        var total = (await Fetch(shopper, one.Id)).Lines.Sum(l => l.Quantity) + (await Fetch(shopper, two.Id)).Lines.Sum(l => l.Quantity);
        Assert.Equal(4, total); // 1 + 3, once
    }
}
