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

/// <summary>Named sets of customers, and a promotion limited to one. Checkout is involved, so these run with the other store-wide tests.</summary>
[Collection(OrderSettings.Name)]
public class CustomerGroupTests(PiranhaCommerceFactory factory) : IClassFixture<PiranhaCommerceFactory>
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };
    private static string Tag() => Guid.NewGuid().ToString("N")[..8];
    private static async Task<T> Read<T>(HttpResponseMessage r) => (await r.Content.ReadFromJsonAsync<T>(Json))!;

    private static async Task<CustomerGroupDto> NewGroup(HttpClient c, string? name = null)
    {
        var r = await c.PostAsJsonAsync("/api/v1/customer-groups", new CustomerGroupInput { Name = name ?? "Group " + Tag() });
        Assert.Equal(HttpStatusCode.Created, r.StatusCode);
        return await Read<CustomerGroupDto>(r);
    }

    private static async Task<CustomerDto> NewCustomer(HttpClient c)
    {
        var r = await c.PostAsJsonAsync("/api/v1/customers", new CustomerInput { Email = $"grp-{Tag()}@example.com" });
        Assert.Equal(HttpStatusCode.Created, r.StatusCode);
        return await Read<CustomerDto>(r);
    }

    private static Task<HttpResponseMessage> Add(HttpClient c, Guid group, Guid customer) => c.PutAsync($"/api/v1/customer-groups/{group}/members/{customer}", null);

    [Fact]
    public async Task Groups_have_unique_names_and_are_edited_and_deleted_by_writers_only()
    {
        var c = await factory.CreateAuthenticatedApiClientAsync();
        var name = "Trade " + Tag();
        var group = await NewGroup(c, name);
        Assert.Equal(0, group.MemberCount);

        var duplicate = await c.PostAsJsonAsync("/api/v1/customer-groups", new CustomerGroupInput { Name = name.ToUpperInvariant() });
        Assert.Equal(HttpStatusCode.BadRequest, duplicate.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await c.PostAsJsonAsync("/api/v1/customer-groups", new CustomerGroupInput { Name = "  " })).StatusCode);

        var other = await NewGroup(c);
        Assert.Equal(HttpStatusCode.BadRequest, (await c.PutAsJsonAsync($"/api/v1/customer-groups/{other.Id}", new CustomerGroupInput { Name = name.ToLowerInvariant() })).StatusCode);
        var renamed = await Read<CustomerGroupDto>(await c.PutAsJsonAsync($"/api/v1/customer-groups/{group.Id}", new CustomerGroupInput { Name = name + " (VIP)", Description = "Best customers" }));
        Assert.Equal(("Best customers", name + " (VIP)"), (renamed.Description, renamed.Name));
        Assert.Contains(await Read<List<CustomerGroupDto>>(await c.GetAsync("/api/v1/customer-groups")), g => g.Id == group.Id);

        var (email, password) = await factory.CreateReadOnlyUserAsync();
        var reader = await factory.CreateAuthenticatedApiClientAsync(email, password);
        Assert.Equal(HttpStatusCode.OK, (await reader.GetAsync($"/api/v1/customer-groups/{group.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await reader.PostAsJsonAsync("/api/v1/customer-groups", new CustomerGroupInput { Name = "x" + Tag() })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await reader.DeleteAsync($"/api/v1/customer-groups/{group.Id}")).StatusCode);

        Assert.Equal(HttpStatusCode.NoContent, (await c.DeleteAsync($"/api/v1/customer-groups/{group.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await c.GetAsync($"/api/v1/customer-groups/{group.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await c.DeleteAsync($"/api/v1/customer-groups/{group.Id}")).StatusCode);
    }

    [Fact]
    public async Task Membership_is_added_once_removed_cleanly_and_goes_when_the_group_or_customer_does()
    {
        var c = await factory.CreateAuthenticatedApiClientAsync();
        var group = await NewGroup(c);
        var ada = await NewCustomer(c);
        var bob = await NewCustomer(c);

        var first = await Add(c, group.Id, ada.Id);
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        var added = await Read<CustomerGroupMemberDto>(first);
        Assert.Equal(ada.Email, added.Email);
        Assert.NotNull(added.AddedBy);

        // Again, and many at once, is still one membership.
        await Task.WhenAll(Enumerable.Range(0, 5).Select(_ => Add(c, group.Id, ada.Id)));
        Assert.Equal(HttpStatusCode.OK, (await Add(c, group.Id, bob.Id)).StatusCode);
        var members = await Read<PagedResult<CustomerGroupMemberDto>>(await c.GetAsync($"/api/v1/customer-groups/{group.Id}/members"));
        Assert.Equal(2, members.TotalCount);
        Assert.Equal(2, (await Read<CustomerGroupDto>(await c.GetAsync($"/api/v1/customer-groups/{group.Id}"))).MemberCount);

        Assert.Equal(HttpStatusCode.NotFound, (await Add(c, group.Id, Guid.NewGuid())).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await Add(c, Guid.NewGuid(), ada.Id)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await c.GetAsync($"/api/v1/customer-groups/{Guid.NewGuid()}/members")).StatusCode);

        var groupsOfAda = await Read<List<CustomerGroupRefDto>>(await c.GetAsync($"/api/v1/customers/{ada.Id}/groups"));
        Assert.Equal(group.Id, Assert.Single(groupsOfAda).Id);
        Assert.Equal(HttpStatusCode.NotFound, (await c.GetAsync($"/api/v1/customers/{Guid.NewGuid()}/groups")).StatusCode);

        Assert.Equal(HttpStatusCode.NoContent, (await c.DeleteAsync($"/api/v1/customer-groups/{group.Id}/members/{bob.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await c.DeleteAsync($"/api/v1/customer-groups/{group.Id}/members/{bob.Id}")).StatusCode); // not a member: fine
        Assert.Equal(HttpStatusCode.NotFound, (await c.DeleteAsync($"/api/v1/customer-groups/{Guid.NewGuid()}/members/{bob.Id}")).StatusCode);

        // Deleting the customer takes them out of the group.
        Assert.Equal(HttpStatusCode.NoContent, (await c.DeleteAsync($"/api/v1/customers/{ada.Id}")).StatusCode);
        Assert.Equal(0, (await Read<CustomerGroupDto>(await c.GetAsync($"/api/v1/customer-groups/{group.Id}"))).MemberCount);

        // Deleting the group leaves the customers.
        var carol = await NewCustomer(c);
        await Add(c, group.Id, carol.Id);
        Assert.Equal(HttpStatusCode.NoContent, (await c.DeleteAsync($"/api/v1/customer-groups/{group.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await c.GetAsync($"/api/v1/customers/{carol.Id}")).StatusCode);
        Assert.Empty(await Read<List<CustomerGroupRefDto>>(await c.GetAsync($"/api/v1/customers/{carol.Id}/groups")));
    }

    // ---- a promotion limited to a group ------------------------------------------------------------------------------------------

    private async Task<(HttpClient Shopper, Guid CustomerId, ProductDto Product)> Shopper(HttpClient staff)
    {
        var product = await Read<ProductDto>(await staff.PostAsJsonAsync("/api/v1/products", new ProductInput { Name = "GP " + Tag(), Sku = "GP-" + Tag(), Price = 20m }));
        await staff.PutAsJsonAsync($"/api/v1/products/{product.Id}/listing", new ProductListingInput { Published = true });
        await staff.PostAsJsonAsync($"/api/v1/products/{product.Id}/stock/adjustments", new StockAdjustmentInput { Delta = 50, Reason = "Delivery" });
        var shopper = factory.CreateClient();
        var reg = await Read<RegisteredDto>(await shopper.PostAsJsonAsync("/api/v1/storefront/auth/register", new RegisterInput { Email = $"gs-{Tag()}@example.com", Password = "a-long-enough-password" }));
        shopper.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", reg.Token.AccessToken);
        return (shopper, reg.Profile.Id, product);
    }

    private static async Task<BasketDto> Basket(HttpClient shopper, ProductDto p)
    {
        var b = await Read<BasketDto>(await shopper.PostAsync("/api/v1/storefront/baskets", null));
        await shopper.PutAsJsonAsync($"/api/v1/storefront/baskets/{b.Id}/lines/{p.Id}", new BasketLineInput { Quantity = 1 });
        return b;
    }

    private static CheckoutInput Checkout() => new()
    {
        ShippingAddress = new AddressInput { Recipient = "Ada", Line1 = "1 High St", City = "Leeds", PostalCode = "LS1", CountryCode = "GB" },
    };

    [Fact]
    public async Task A_code_limited_to_a_group_works_for_members_only_and_looks_like_any_bad_code_to_others()
    {
        var staff = await factory.CreateAuthenticatedApiClientAsync();
        var group = await NewGroup(staff);
        var code = "GRP" + Tag().ToUpperInvariant();
        var created = await staff.PostAsJsonAsync("/api/v1/promotions", new PromotionInput { Code = code, Kind = PromotionKind.Percent, Value = 10, CustomerGroupId = group.Id }, Json);
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var promo = await Read<PromotionDto>(created);
        Assert.Equal((group.Id, group.Name), (promo.CustomerGroupId, promo.CustomerGroupName));

        var (member, memberId, product) = await Shopper(staff);
        var (outsider, _, _) = await Shopper(staff);
        await Add(staff, group.Id, memberId);

        // The outsider is told what anyone with a wrong code is told.
        var outsiderBasket = await Basket(outsider, product);
        var refused = await outsider.PostAsJsonAsync($"/api/v1/storefront/baskets/{outsiderBasket.Id}/promo", new CodeInput { Code = code });
        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
        Assert.Contains(PromotionRules.NotValid, await refused.Content.ReadAsStringAsync());
        var unknown = await outsider.PostAsJsonAsync($"/api/v1/storefront/baskets/{outsiderBasket.Id}/promo", new CodeInput { Code = "NOPE" + Tag() });
        Assert.Contains(PromotionRules.NotValid, await unknown.Content.ReadAsStringAsync()); // the same words as for a code that is not for them

        // A staff basket for nobody in particular cannot use it either.
        var guestBasket = await Read<BasketDto>(await staff.PostAsJsonAsync("/api/v1/baskets", new BasketCreateInput()));
        Assert.Equal(HttpStatusCode.BadRequest, (await staff.PostAsJsonAsync($"/api/v1/baskets/{guestBasket.Id}/promo", new CodeInput { Code = code })).StatusCode);

        // The member gets the discount, in the quote and on the order.
        var basket = await Basket(member, product);
        Assert.Equal(HttpStatusCode.OK, (await member.PostAsJsonAsync($"/api/v1/storefront/baskets/{basket.Id}/promo", new CodeInput { Code = code })).StatusCode);
        var order = await Read<OrderDto>(await member.PostAsJsonAsync($"/api/v1/storefront/baskets/{basket.Id}/checkout", Checkout()));
        Assert.Equal((2m, code), (order.DiscountTotal, order.PromotionCode));
    }

    [Fact]
    public async Task Leaving_the_group_after_applying_the_code_stops_checkout_and_the_group_cannot_be_deleted_while_a_promotion_uses_it()
    {
        var staff = await factory.CreateAuthenticatedApiClientAsync();
        var group = await NewGroup(staff);
        var code = "GRP" + Tag().ToUpperInvariant();
        var promo = await Read<PromotionDto>(await staff.PostAsJsonAsync("/api/v1/promotions", new PromotionInput { Code = code, Kind = PromotionKind.Amount, Value = 5, CustomerGroupId = group.Id }, Json));

        var (shopper, customerId, product) = await Shopper(staff);
        await Add(staff, group.Id, customerId);
        var basket = await Basket(shopper, product);
        Assert.Equal(HttpStatusCode.OK, (await shopper.PostAsJsonAsync($"/api/v1/storefront/baskets/{basket.Id}/promo", new CodeInput { Code = code })).StatusCode);

        await staff.DeleteAsync($"/api/v1/customer-groups/{group.Id}/members/{customerId}");
        var quote = await Read<QuoteDto>(await shopper.PostAsJsonAsync($"/api/v1/storefront/baskets/{basket.Id}/quote", new QuoteInput { CountryCode = "GB" }));
        Assert.Equal(0m, quote.DiscountTotal);
        Assert.NotNull(quote.PromotionProblem);
        var checkout = await shopper.PostAsJsonAsync($"/api/v1/storefront/baskets/{basket.Id}/checkout", Checkout());
        Assert.Equal(HttpStatusCode.Conflict, checkout.StatusCode);
        Assert.Equal(0, (await Read<PromotionDto>(await staff.GetAsync($"/api/v1/promotions/{promo.Id}"))).Redeemed); // nothing was used up

        var blocked = await staff.DeleteAsync($"/api/v1/customer-groups/{group.Id}");
        Assert.Equal(HttpStatusCode.Conflict, blocked.StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await staff.GetAsync($"/api/v1/customer-groups/{group.Id}")).StatusCode);

        Assert.Equal(HttpStatusCode.NoContent, (await staff.DeleteAsync($"/api/v1/promotions/{promo.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await staff.DeleteAsync($"/api/v1/customer-groups/{group.Id}")).StatusCode);

        var unknownGroup = await staff.PostAsJsonAsync("/api/v1/promotions", new PromotionInput { Code = "GRP" + Tag(), Kind = PromotionKind.Amount, Value = 1, CustomerGroupId = Guid.NewGuid() }, Json);
        Assert.Equal(HttpStatusCode.BadRequest, unknownGroup.StatusCode);
    }
}
