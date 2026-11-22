using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using PiranhaCommerce.Catalog;
using PiranhaCommerce.Customers;
using PiranhaCommerce.Ecommerce;

namespace PiranhaCommerce.IntegrationTests;

/// <summary>What a customer pays: group percentage, fixed price per product, best price wins, then a promo code on the result.</summary>
[Collection(OrderSettings.Name)]
public class GroupPricingTests(PiranhaFactory factory) : IClassFixture<PiranhaFactory>
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };
    private static string Tag() => Guid.NewGuid().ToString("N")[..8];
    private static async Task<T> Read<T>(HttpResponseMessage r) => (await r.Content.ReadFromJsonAsync<T>(Json))!;

    private static async Task<CustomerGroupDto> NewGroup(HttpClient c, decimal percent = 0m) =>
        await Read<CustomerGroupDto>(await c.PostAsJsonAsync("/api/v1/customer-groups", new CustomerGroupInput { Name = "G " + Tag(), DiscountPercent = percent }));

    private static async Task<ProductDto> NewProduct(HttpClient c, decimal price)
    {
        var p = await Read<ProductDto>(await c.PostAsJsonAsync("/api/v1/products", new ProductInput { Name = "GP " + Tag(), Sku = "GP-" + Tag(), Price = price }));
        await c.PutAsJsonAsync($"/api/v1/products/{p.Id}/listing", new ProductListingInput { Published = true });
        await c.PostAsJsonAsync($"/api/v1/products/{p.Id}/stock/adjustments", new StockAdjustmentInput { Delta = 100, Reason = "Delivery" });
        return p;
    }

    private async Task<(HttpClient Client, Guid Id)> NewShopper()
    {
        var client = factory.CreateClient();
        var reg = await Read<RegisteredDto>(await client.PostAsJsonAsync("/api/v1/storefront/auth/register", new RegisterInput { Email = $"gp-{Tag()}@example.com", Password = "a-long-enough-password" }));
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", reg.Token.AccessToken);
        return (client, reg.Profile.Id);
    }

    private static Task<HttpResponseMessage> Join(HttpClient staff, Guid group, Guid customer) => staff.PutAsync($"/api/v1/customer-groups/{group}/members/{customer}", null);
    private static Task<HttpResponseMessage> Fix(HttpClient staff, ProductDto p, CustomerGroupDto g, decimal price) =>
        staff.PutAsJsonAsync($"/api/v1/products/{p.Id}/group-prices/{g.Id}", new GroupPriceInput { Price = price });

    private static async Task<BasketDto> BasketWith(HttpClient shopper, ProductDto p, int quantity = 1)
    {
        var b = await Read<BasketDto>(await shopper.PostAsync("/api/v1/storefront/baskets", null));
        return await Read<BasketDto>(await shopper.PutAsJsonAsync($"/api/v1/storefront/baskets/{b.Id}/lines/{p.Id}", new BasketLineInput { Quantity = quantity }));
    }

    private static async Task<decimal> PriceOf(HttpClient shopper, ProductDto p) =>
        (await Read<List<ShopperPriceDto>>(await shopper.GetAsync($"/api/v1/storefront/prices?productIds={p.Id}"))).Single().Price;

    [Fact]
    public async Task A_group_percentage_applies_to_the_basket_the_quote_and_the_order_and_everyone_else_pays_the_regular_price()
    {
        var staff = await factory.CreateAuthenticatedApiClientAsync();
        var group = await NewGroup(staff, 20m);
        var p = await NewProduct(staff, 100m);
        var (member, memberId) = await NewShopper();
        var (outsider, _) = await NewShopper();
        await Join(staff, group.Id, memberId);

        var basket = await BasketWith(member, p, 2);
        var line = basket.Lines.Single();
        Assert.Equal((80m, 100m, 160m), (line.UnitPrice, line.RegularPrice, line.LineTotal));
        Assert.Equal(160m, basket.Subtotal);

        var quote = await Read<QuoteDto>(await member.PostAsJsonAsync($"/api/v1/storefront/baskets/{basket.Id}/quote", new QuoteInput { CountryCode = "GB" }));
        Assert.Equal(160m, quote.Subtotal);

        var order = await Read<OrderDto>(await member.PostAsJsonAsync($"/api/v1/storefront/baskets/{basket.Id}/checkout",
            new CheckoutInput { ShippingAddress = new AddressInput { Recipient = "Ada", Line1 = "1 High St", City = "Leeds", PostalCode = "LS1", CountryCode = "GB" } }));
        Assert.Equal(80m, order.Lines.Single().UnitPrice); // the order keeps the price that was charged
        Assert.Equal(160m, order.Subtotal);

        var other = await BasketWith(outsider, p);
        Assert.Equal((100m, (decimal?)null), (other.Lines.Single().UnitPrice, other.Lines.Single().RegularPrice));

        // Later changes to the group never change an order already placed.
        await staff.PutAsJsonAsync($"/api/v1/customer-groups/{group.Id}", new CustomerGroupInput { Name = group.Name, DiscountPercent = 50m });
        Assert.Equal(160m, (await Read<OrderDto>(await staff.GetAsync($"/api/v1/orders/{order.Id}"))).Subtotal);
    }

    [Fact]
    public async Task A_fixed_price_beats_the_groups_percentage_but_a_group_can_never_make_something_dearer()
    {
        var staff = await factory.CreateAuthenticatedApiClientAsync();
        var group = await NewGroup(staff, 20m);
        var p = await NewProduct(staff, 100m);
        var (shopper, id) = await NewShopper();
        await Join(staff, group.Id, id);
        Assert.Equal(80m, await PriceOf(shopper, p));

        var row = await Read<ProductGroupPriceDto>(await Fix(staff, p, group, 70m));
        Assert.Equal((70m, 70m, 20m), (row.FixedPrice!.Value, row.GroupPrice, row.DiscountPercent));
        Assert.Equal(70m, await PriceOf(shopper, p));

        // A fixed price above the regular price is ignored: they pay the lower.
        await Fix(staff, p, group, 150m);
        Assert.Equal(100m, await PriceOf(shopper, p));

        // Removing it brings the percentage back.
        Assert.Equal(HttpStatusCode.NoContent, (await staff.DeleteAsync($"/api/v1/products/{p.Id}/group-prices/{group.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await staff.DeleteAsync($"/api/v1/products/{p.Id}/group-prices/{group.Id}")).StatusCode);
        Assert.Equal(80m, await PriceOf(shopper, p));

        // A fixed price for a different product does not leak.
        var q = await NewProduct(staff, 50m);
        await Fix(staff, p, group, 10m);
        Assert.Equal(40m, await PriceOf(shopper, q));
    }

    [Fact]
    public async Task In_several_groups_the_best_price_wins()
    {
        var staff = await factory.CreateAuthenticatedApiClientAsync();
        var tenOff = await NewGroup(staff, 10m);
        var fixedGroup = await NewGroup(staff);
        var p = await NewProduct(staff, 100m);
        var (shopper, id) = await NewShopper();
        await Join(staff, tenOff.Id, id);
        await Join(staff, fixedGroup.Id, id);
        await Fix(staff, p, fixedGroup, 85m);
        Assert.Equal(85m, await PriceOf(shopper, p)); // fixed 85 beats 10% off (90)

        await staff.PutAsJsonAsync($"/api/v1/customer-groups/{tenOff.Id}", new CustomerGroupInput { Name = tenOff.Name, DiscountPercent = 30m });
        Assert.Equal(70m, await PriceOf(shopper, p)); // now 30% off (70) is better

        // Leaving a group drops that group's price.
        await staff.DeleteAsync($"/api/v1/customer-groups/{tenOff.Id}/members/{id}");
        Assert.Equal(85m, await PriceOf(shopper, p));
    }

    [Fact]
    public async Task A_basket_follows_group_membership_and_a_promo_code_discounts_the_group_price()
    {
        var staff = await factory.CreateAuthenticatedApiClientAsync();
        var group = await NewGroup(staff, 20m);
        var p = await NewProduct(staff, 100m);
        var (shopper, id) = await NewShopper();

        var basket = await BasketWith(shopper, p);
        Assert.Equal(100m, basket.Subtotal);
        await Join(staff, group.Id, id); // joins after the basket exists: the price follows
        Assert.Equal(80m, (await Read<BasketDto>(await shopper.GetAsync($"/api/v1/storefront/baskets/{basket.Id}"))).Subtotal);

        var code = "PC" + Tag();
        await staff.PostAsJsonAsync("/api/v1/promotions", new PromotionInput { Code = code, Kind = PromotionKind.Percent, Value = 10 }, Json);
        Assert.Equal(HttpStatusCode.OK, (await shopper.PostAsJsonAsync($"/api/v1/storefront/baskets/{basket.Id}/promo", new CodeInput { Code = code })).StatusCode);
        var quote = await Read<QuoteDto>(await shopper.PostAsJsonAsync($"/api/v1/storefront/baskets/{basket.Id}/quote", new QuoteInput { CountryCode = "GB" }));
        Assert.Equal((80m, 8m), (quote.Subtotal, quote.DiscountTotal)); // 10% of the group price, not of 100

        await staff.DeleteAsync($"/api/v1/customer-groups/{group.Id}/members/{id}");
        Assert.Equal(100m, (await Read<BasketDto>(await shopper.GetAsync($"/api/v1/storefront/baskets/{basket.Id}"))).Subtotal);
    }

    [Fact]
    public async Task Percentages_are_rounded_to_pence_and_checked_and_a_staff_basket_for_a_customer_uses_their_price()
    {
        var staff = await factory.CreateAuthenticatedApiClientAsync();
        var group = await NewGroup(staff, 15m);
        var p = await NewProduct(staff, 9.99m);
        var (shopper, id) = await NewShopper();
        await Join(staff, group.Id, id);
        Assert.Equal(8.49m, await PriceOf(shopper, p)); // 8.4915

        var prices = await Read<List<ShopperPriceDto>>(await staff.GetAsync($"/api/v1/customers/{id}/prices?productIds={p.Id}&productIds={Guid.NewGuid()}"));
        Assert.Equal((8.49m, 9.99m, true), (prices.Single().Price, prices.Single().RegularPrice, prices.Single().Discounted)); // unknown product left out
        var guest = await Read<List<ShopperPriceDto>>(await staff.GetAsync($"/api/v1/customers/{Guid.NewGuid()}/prices?productIds={p.Id}"));
        Assert.False(guest.Single().Discounted);

        // Staff placing an order for the customer: the same price.
        var basket = await Read<BasketDto>(await staff.PostAsJsonAsync("/api/v1/baskets", new BasketCreateInput { CustomerId = id }));
        await staff.PutAsJsonAsync($"/api/v1/baskets/{basket.Id}/lines/{p.Id}", new BasketLineInput { Quantity = 1 });
        var order = await Read<OrderDto>(await staff.PostAsJsonAsync($"/api/v1/baskets/{basket.Id}/checkout", new CheckoutInput
        {
            Email = $"gp-{Tag()}@example.com", CustomerId = id,
            ShippingAddress = new AddressInput { Recipient = "Ada", Line1 = "1 High St", City = "Leeds", PostalCode = "LS1", CountryCode = "GB" },
        }));
        Assert.Equal(8.49m, order.Lines.Single().UnitPrice);

        foreach (var bad in new[] { -1m, 100.5m, 101m, 10.555m })
            Assert.Equal(HttpStatusCode.BadRequest, (await staff.PostAsJsonAsync("/api/v1/customer-groups", new CustomerGroupInput { Name = "Bad " + Tag(), DiscountPercent = bad })).StatusCode);
    }

    [Fact]
    public async Task Group_prices_are_validated_listed_and_removed_with_their_group_and_only_writers_set_them()
    {
        var staff = await factory.CreateAuthenticatedApiClientAsync();
        var group = await NewGroup(staff, 5m);
        var p = await NewProduct(staff, 40m);

        Assert.Equal(HttpStatusCode.BadRequest, (await Fix(staff, p, group, -1m)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await Fix(staff, p, group, 1.23456m)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await staff.PutAsJsonAsync($"/api/v1/products/{p.Id}/group-prices/{Guid.NewGuid()}", new GroupPriceInput { Price = 1 })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await staff.PutAsJsonAsync($"/api/v1/products/{Guid.NewGuid()}/group-prices/{group.Id}", new GroupPriceInput { Price = 1 })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await staff.GetAsync($"/api/v1/products/{Guid.NewGuid()}/group-prices")).StatusCode);

        await Fix(staff, p, group, 30m);
        await Fix(staff, p, group, 31m); // setting again replaces it
        var rows = await Read<List<ProductGroupPriceDto>>(await staff.GetAsync($"/api/v1/products/{p.Id}/group-prices"));
        var mine = rows.Single(r => r.GroupId == group.Id);
        Assert.Equal((31m, 31m), (mine.FixedPrice!.Value, mine.GroupPrice));

        var viewer = await factory.CreateReadOnlyUserAsync();
        var read = await factory.CreateAuthenticatedApiClientAsync(viewer.Email, viewer.Password);
        Assert.Equal(HttpStatusCode.OK, (await read.GetAsync($"/api/v1/products/{p.Id}/group-prices")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await Fix(read, p, group, 1m)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await read.DeleteAsync($"/api/v1/products/{p.Id}/group-prices/{group.Id}")).StatusCode);

        // The group goes, and its prices go with it.
        Assert.Equal(HttpStatusCode.NoContent, (await staff.DeleteAsync($"/api/v1/customer-groups/{group.Id}")).StatusCode);
        Assert.DoesNotContain(await Read<List<ProductGroupPriceDto>>(await staff.GetAsync($"/api/v1/products/{p.Id}/group-prices")), r => r.GroupId == group.Id);
    }

    private static async Task<CategoryDto> NewCategory(HttpClient c, Guid? parent = null) =>
        await Read<CategoryDto>(await c.PostAsJsonAsync("/api/v1/categories", new CategoryInput { Name = "Cat " + Tag(), ParentId = parent }));

    private static Task<HttpResponseMessage> Put(HttpClient c, CustomerGroupDto g, CategoryDto cat, decimal percent) =>
        c.PutAsJsonAsync($"/api/v1/customer-groups/{g.Id}/category-discounts/{cat.Id}", new GroupCategoryDiscountInput { Percent = percent });

    private static Task<HttpResponseMessage> Place(HttpClient c, ProductDto p, params CategoryDto[] cats) =>
        c.PutAsJsonAsync($"/api/v1/products/{p.Id}/categories", new ProductCategoriesInput { CategoryIds = cats.Select(x => x.Id).ToList() });

    [Fact]
    public async Task A_category_percentage_overrides_the_group_percentage_for_products_in_it_and_a_fixed_price_still_wins()
    {
        var staff = await factory.CreateAuthenticatedApiClientAsync();
        var group = await NewGroup(staff, 5m);
        var shoes = await NewCategory(staff);
        var inShoes = await NewProduct(staff, 100m); await Place(staff, inShoes, shoes);
        var elsewhere = await NewProduct(staff, 100m);
        var (shopper, id) = await NewShopper();
        await Join(staff, group.Id, id);
        Assert.Equal(95m, await PriceOf(shopper, inShoes));

        Assert.Equal(HttpStatusCode.OK, (await Put(staff, group, shoes, 25m)).StatusCode);
        Assert.Equal(75m, await PriceOf(shopper, inShoes));
        Assert.Equal(95m, await PriceOf(shopper, elsewhere)); // only the category

        // The product page shows where the percentage came from.
        var row = (await Read<List<ProductGroupPriceDto>>(await staff.GetAsync($"/api/v1/products/{inShoes.Id}/group-prices"))).Single(r => r.GroupId == group.Id);
        Assert.Equal((25m, 5m, 75m), (row.CategoryPercent!.Value, row.DiscountPercent, row.GroupPrice));

        // The basket uses it too.
        var basket = await BasketWith(shopper, inShoes);
        Assert.Equal((75m, 100m), (basket.Lines.Single().UnitPrice, basket.Lines.Single().RegularPrice));

        // A fixed price for the product is more specific still.
        await Fix(staff, inShoes, group, 60m);
        Assert.Equal(60m, await PriceOf(shopper, inShoes));
        await staff.DeleteAsync($"/api/v1/products/{inShoes.Id}/group-prices/{group.Id}");

        // Removing the category discount brings the group percentage back.
        Assert.Equal(HttpStatusCode.NoContent, (await staff.DeleteAsync($"/api/v1/customer-groups/{group.Id}/category-discounts/{shoes.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await staff.DeleteAsync($"/api/v1/customer-groups/{group.Id}/category-discounts/{shoes.Id}")).StatusCode);
        Assert.Equal(95m, await PriceOf(shopper, inShoes));
    }

    [Fact]
    public async Task A_category_covers_its_sub_categories_the_nearest_one_set_wins_and_zero_switches_it_off()
    {
        var staff = await factory.CreateAuthenticatedApiClientAsync();
        var group = await NewGroup(staff, 5m);
        var clothing = await NewCategory(staff);
        var shoes = await NewCategory(staff, clothing.Id);
        var boots = await NewCategory(staff, shoes.Id);
        var boot = await NewProduct(staff, 100m); await Place(staff, boot, boots);
        var (shopper, id) = await NewShopper();
        await Join(staff, group.Id, id);

        await Put(staff, group, clothing, 30m);
        Assert.Equal(70m, await PriceOf(shopper, boot)); // two levels down still covered

        await Put(staff, group, shoes, 10m);
        Assert.Equal(90m, await PriceOf(shopper, boot)); // nearest wins, even though it is less

        await Put(staff, group, boots, 0m);
        Assert.Equal(100m, await PriceOf(shopper, boot)); // 0 means none here, not even the 5% of the group
    }

    [Fact]
    public async Task A_product_in_several_categories_gets_the_largest_percentage_and_groups_still_compete()
    {
        var staff = await factory.CreateAuthenticatedApiClientAsync();
        var group = await NewGroup(staff);
        var a = await NewCategory(staff); var b = await NewCategory(staff);
        var p = await NewProduct(staff, 200m); await Place(staff, p, a, b);
        var (shopper, id) = await NewShopper();
        await Join(staff, group.Id, id);
        await Put(staff, group, a, 10m);
        await Put(staff, group, b, 40m);
        Assert.Equal(120m, await PriceOf(shopper, p));

        var other = await NewGroup(staff, 50m); // a second group with a better price wins
        await Join(staff, other.Id, id);
        Assert.Equal(100m, await PriceOf(shopper, p));
    }

    [Fact]
    public async Task Category_discounts_are_validated_listed_and_writers_only_and_go_with_the_group_or_category()
    {
        var staff = await factory.CreateAuthenticatedApiClientAsync();
        var group = await NewGroup(staff);
        var cat = await NewCategory(staff);
        Assert.Equal(HttpStatusCode.BadRequest, (await Put(staff, group, cat, -1m)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await Put(staff, group, cat, 100.5m)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await Put(staff, group, cat, 12.345m)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await staff.PutAsJsonAsync($"/api/v1/customer-groups/{Guid.NewGuid()}/category-discounts/{cat.Id}", new GroupCategoryDiscountInput { Percent = 5 })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await staff.PutAsJsonAsync($"/api/v1/customer-groups/{group.Id}/category-discounts/{Guid.NewGuid()}", new GroupCategoryDiscountInput { Percent = 5 })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await staff.GetAsync($"/api/v1/customer-groups/{Guid.NewGuid()}/category-discounts")).StatusCode);

        await Put(staff, group, cat, 12m);
        await Put(staff, group, cat, 15m); // replaces
        var listed = await Read<List<GroupCategoryDiscountDto>>(await staff.GetAsync($"/api/v1/customer-groups/{group.Id}/category-discounts"));
        Assert.Equal(15m, listed.Single().Percent);

        var viewer = await factory.CreateReadOnlyUserAsync();
        var read = await factory.CreateAuthenticatedApiClientAsync(viewer.Email, viewer.Password);
        Assert.Equal(HttpStatusCode.OK, (await read.GetAsync($"/api/v1/customer-groups/{group.Id}/category-discounts")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await Put(read, group, cat, 1m)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await read.DeleteAsync($"/api/v1/customer-groups/{group.Id}/category-discounts/{cat.Id}")).StatusCode);

        // Deleting the category takes its discount with it; so does deleting the group.
        Assert.Equal(HttpStatusCode.NoContent, (await staff.DeleteAsync($"/api/v1/categories/{cat.Id}")).StatusCode);
        Assert.Empty(await Read<List<GroupCategoryDiscountDto>>(await staff.GetAsync($"/api/v1/customer-groups/{group.Id}/category-discounts")));
        var cat2 = await NewCategory(staff);
        await Put(staff, group, cat2, 5m);
        Assert.Equal(HttpStatusCode.NoContent, (await staff.DeleteAsync($"/api/v1/customer-groups/{group.Id}")).StatusCode);
    }
}
