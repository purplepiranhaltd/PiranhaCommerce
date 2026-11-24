using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using PiranhaCommerce.Customers;
using PiranhaCommerce.Ecommerce;
using PiranhaCommerce.Web.SampleData;

namespace PiranhaCommerce.IntegrationTests;

/// <summary>The sample shop's basket and guest checkout, driven the way a browser does it: fetch a page, take its antiforgery token, post its form, follow the redirect.</summary>
public partial class ShopCheckoutTests(PiranhaCommerceFactory factory) : IClassFixture<PiranhaCommerceFactory>
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };

    [GeneratedRegex("name=\"__RequestVerificationToken\"[^>]*value=\"([^\"]+)\"")]
    private static partial Regex TokenPattern();

    private async Task<HttpClient> BrowserAsync()
    {
        await factory.Services.GetRequiredService<SampleDataSeeder>().SeedAsync();
        return factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false, HandleCookies = true });
    }

    private static async Task<string> Get(HttpClient browser, string url)
    {
        var response = await browser.GetAsync(url);
        var body = WebUtility.HtmlDecode(await response.Content.ReadAsStringAsync());
        Assert.True(response.IsSuccessStatusCode, $"{(int)response.StatusCode} {url}: {body[..Math.Min(body.Length, 1500)]}");
        return body;
    }

    /// <summary>Posts a form the way a page does: with the token from that page. Returns where the browser is sent next.</summary>
    private static async Task<string> Post(HttpClient browser, string pageWithForm, string action, params (string, string)[] fields)
    {
        var page = await browser.GetStringAsync(pageWithForm);
        var token = TokenPattern().Match(page).Groups[1].Value;
        Assert.NotEmpty(token);
        var response = await browser.PostAsync(action, new FormUrlEncodedContent(fields.Append(("__RequestVerificationToken", token)).Select(f => KeyValuePair.Create(f.Item1, f.Item2))));
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        return response.Headers.Location!.OriginalString;
    }

    private async Task<ShopProductDto> Product(string slug) =>
        (await factory.CreateClient().GetFromJsonAsync<ShopProductDto>($"/api/v1/storefront/products/by-slug/{slug}", Json))!;

    private static (string, string)[] Address(string email = "guest@example.com", string postcode = "LS1 1AA", string country = "GB") =>
        [("email", email), ("recipient", "Gina Guest"), ("line1", "1 High Street"), ("line2", ""), ("city", "Leeds"), ("region", ""), ("postalCode", postcode), ("countryCode", country)];

    [Fact]
    public async Task A_guest_fills_a_basket_uses_a_code_picks_delivery_and_places_the_order_through_the_shops_own_forms()
    {
        var browser = await BrowserAsync();
        var lipstick = await Product("velvet-matte-lipstick");
        var ruby = lipstick.Variants.Single(v => v.Values[0].Value == "Ruby Red");

        // Nothing is in the basket, and visiting the shop creates nothing.
        Assert.Contains("Your basket is empty", await Get(browser, "/basket"));

        Assert.Equal("/basket", await Post(browser, "/product/velvet-matte-lipstick", "/basket/add", ("productId", ruby.ProductId.ToString()), ("quantity", "1"), ("returnTo", "/product/velvet-matte-lipstick")));
        Assert.Equal("/basket", await Post(browser, "/product/velvet-matte-lipstick", "/basket/add", ("productId", ruby.ProductId.ToString()), ("quantity", "1"), ("returnTo", "/product/velvet-matte-lipstick")));

        // Two of them: 2 x 14.00 + VAT. The header counts them.
        var basket = await Get(browser, "/basket");
        Assert.Contains("Velvet Matte Lipstick", basket);
        Assert.Contains("Basket (2)", basket);
        Assert.Contains("£33.60", basket);

        // The quantity can be changed.
        await Post(browser, "/basket", "/basket/set", ("productId", ruby.ProductId.ToString()), ("quantity", "3"));
        Assert.Contains("£50.40", await Get(browser, "/basket"));
        await Post(browser, "/basket", "/basket/set", ("productId", ruby.ProductId.ToString()), ("quantity", "2"));

        // A code that does not exist is explained; the real one takes 10% off.
        var bad = await Post(browser, "/basket", "/basket/promo", ("code", "NOPE"));
        Assert.StartsWith("/basket?f=", bad);
        Assert.Contains("class=\"notice error\"", await Get(browser, bad));
        await Post(browser, "/basket", "/basket/promo", ("code", "welcome10"));
        basket = await Get(browser, "/basket");
        Assert.Contains("WELCOME10", basket);
        Assert.Contains("£30.24", basket);   // 33.60 less 10%
        Assert.Contains("£3.36", basket);    // what the shopper saves, with VAT

        // Checkout step 1: a bad email is sent back with what was typed.
        var back = await Post(browser, "/checkout", "/checkout/details", Address("not-an-email"));
        Assert.StartsWith("/checkout?f=", back);
        var again = await Get(browser, back);
        Assert.Contains("not-an-email", again);
        Assert.Contains("class=\"notice error\"", again);

        // Step 2: delivery by address. The UK has two methods; the free-over limit is judged after the discount (25.20 is under 40).
        Assert.Equal("/checkout/delivery", await Post(browser, "/checkout", "/checkout/details", Address()));
        var delivery = await Get(browser, "/checkout/delivery");
        Assert.Contains("Standard delivery (3-5 working days)", delivery);
        Assert.Contains("Next working day delivery", delivery);
        Assert.DoesNotContain("International tracked", delivery);
        Assert.Contains("£3.95", delivery);

        var staff = await factory.CreateAuthenticatedApiClientAsync();
        var methods = await staff.GetFromJsonAsync<List<ShippingMethodDto>>("/api/v1/shipping-methods", Json);
        var standard = methods!.Single(m => m.Name.StartsWith("Standard"));

        Assert.Equal("/order/confirmed", await Post(browser, "/checkout/delivery", "/checkout/place", ("shippingMethodId", standard.Id.ToString()), ("notes", "Leave with the neighbour")));

        // The confirmation shows the order, and matches what staff see.
        var done = await Get(browser, "/order/confirmed");
        Assert.Contains("Thank you", done);
        var orders = await staff.GetFromJsonAsync<PiranhaCommerce.Core.Paging.PagedResult<OrderSummaryDto>>("/api/v1/orders?search=guest@example.com", Json);
        var summary = orders!.Items.Single();
        Assert.Contains(summary.Reference, done);
        var order = await staff.GetFromJsonAsync<OrderDto>($"/api/v1/orders/{summary.Id}", Json);
        Assert.Null(order!.CustomerId);                                  // a guest order belongs to no customer
        Assert.Equal("WELCOME10", order.PromotionCode);
        Assert.Equal(2.80m, order.DiscountTotal);                        // 10% of 28.00
        Assert.Equal(3.95m, order.ShippingTotal);
        Assert.Equal("Leave with the neighbour", order.Notes);
        Assert.Contains("£" + order.Total.ToString("N2", System.Globalization.CultureInfo.GetCultureInfo("en-GB")), done);

        // The basket is gone from the browser, and the order is not shown to anyone who did not place it.
        Assert.Contains("Your basket is empty", await Get(browser, "/basket"));
        var stranger = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false, HandleCookies = true });
        Assert.Contains("No recent order", await Get(stranger, "/order/confirmed"));
    }

    [Fact]
    public async Task The_shops_forms_refuse_what_they_should()
    {
        var browser = await BrowserAsync();
        var lipstick = await Product("velvet-matte-lipstick");
        var plumNoir = lipstick.Variants.Single(v => v.Values[0].Value == "Plum Noir");   // sold out
        var ruby = lipstick.Variants.Single(v => v.Values[0].Value == "Ruby Red");

        // No antiforgery token: refused, and nothing is made.
        var bare = await browser.PostAsync("/basket/add", new FormUrlEncodedContent([KeyValuePair.Create("productId", ruby.ProductId.ToString()), KeyValuePair.Create("quantity", "1")]));
        Assert.Equal(HttpStatusCode.BadRequest, bare.StatusCode);
        Assert.Contains("Your basket is empty", await Get(browser, "/basket"));

        // The parent of a product with shades cannot be bought (only a chosen shade can), and the way back is only ever a path on this site.
        var parent = await Post(browser, "/product/velvet-matte-lipstick", "/basket/add", ("productId", lipstick.Id.ToString()), ("quantity", "1"), ("returnTo", "//evil.example/x"));
        Assert.StartsWith("/basket?f=", parent);
        Assert.Contains("class=\"notice error\"", await Get(browser, parent));

        // A sold-out shade is refused with a reason, and leaves the basket empty.
        var gone = await Post(browser, "/product/velvet-matte-lipstick", "/basket/add", ("productId", plumNoir.ProductId.ToString()), ("quantity", "1"), ("returnTo", "/product/velvet-matte-lipstick"));
        Assert.StartsWith("/product/velvet-matte-lipstick?f=", gone);
        Assert.Contains("class=\"notice error\"", await Get(browser, gone));

        // Stock that disappears after it was put in the basket is caught at checkout, with a message, and no order is made.
        var berry = lipstick.Variants.Single(v => v.Values[0].Value == "Berry Bold");
        await Post(browser, "/product/velvet-matte-lipstick", "/basket/add", ("productId", berry.ProductId.ToString()), ("quantity", "2"), ("returnTo", "/product/velvet-matte-lipstick"));
        await Post(browser, "/checkout", "/checkout/details", Address("late@example.com"));
        using (var sell = factory.Services.CreateScope())
            await sell.ServiceProvider.GetRequiredService<PiranhaCommerce.Catalog.IStockService>()
                .AdjustAsync(berry.ProductId, new PiranhaCommerce.Catalog.StockAdjustmentInput { Delta = -(berry.Left ?? 18), Reason = "Test" }, "test");
        var staff = await factory.CreateAuthenticatedApiClientAsync();
        var methods = await staff.GetFromJsonAsync<List<ShippingMethodDto>>("/api/v1/shipping-methods", Json);
        var refused = await Post(browser, "/checkout/delivery", "/checkout/place", ("shippingMethodId", methods![0].Id.ToString()));
        Assert.Matches(@"^/(basket|checkout/delivery)\?f=", refused);
        Assert.Contains("class=\"notice error\"", await Get(browser, refused));
        var orders = await staff.GetFromJsonAsync<PiranhaCommerce.Core.Paging.PagedResult<OrderSummaryDto>>("/api/v1/orders?search=late@example.com", Json);
        Assert.Empty(orders!.Items);

        // The delivery step needs a basket and an address; without them it sends the shopper back.
        var fresh = await BrowserAsync();
        var r = await fresh.GetAsync("/checkout/delivery");
        Assert.Equal(HttpStatusCode.Redirect, r.StatusCode);

        // A basket that belongs to a signed-in customer is not a guest's to use, even if its id is in the cookie: it shows as empty, a post
        // leaves it alone, and the shopper gets a basket of their own.
        using var scope = factory.Services.CreateScope();
        var profile = await scope.ServiceProvider.GetRequiredService<ICustomerAccountService>()
            .RegisterAsync(new RegisterInput { Email = $"owner{Guid.NewGuid():N}@example.com", Password = "Owner-Passw0rd!" });
        var baskets = scope.ServiceProvider.GetRequiredService<IBasketService>();
        var owned = await baskets.CreateAsync(new BasketCreateInput { CustomerId = profile.Id });

        var thief = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false, HandleCookies = false });
        var borrowed = $".Petalwick.Basket={owned.Id}";
        var look = new HttpRequestMessage(HttpMethod.Get, "/basket");
        look.Headers.Add("Cookie", borrowed);
        Assert.Contains("Your basket is empty", WebUtility.HtmlDecode(await (await thief.SendAsync(look)).Content.ReadAsStringAsync()));

        var form = await thief.GetAsync("/product/velvet-matte-lipstick");
        var token = TokenPattern().Match(await form.Content.ReadAsStringAsync()).Groups[1].Value;
        var antiforgery = form.Headers.GetValues("Set-Cookie").First(c => c.StartsWith(".AspNetCore.Antiforgery", StringComparison.Ordinal)).Split(';')[0];
        var post = new HttpRequestMessage(HttpMethod.Post, "/basket/add")
        {
            Content = new FormUrlEncodedContent([KeyValuePair.Create("productId", ruby.ProductId.ToString()), KeyValuePair.Create("quantity", "1"), KeyValuePair.Create("__RequestVerificationToken", token)]),
        };
        post.Headers.Add("Cookie", $"{antiforgery}; {borrowed}");
        var posted = await thief.SendAsync(post);
        Assert.Equal(HttpStatusCode.Redirect, posted.StatusCode);
        Assert.Contains(".Petalwick.Basket=", string.Join(';', posted.Headers.GetValues("Set-Cookie")));           // a new basket was started...
        Assert.DoesNotContain(owned.Id.ToString(), string.Join(';', posted.Headers.GetValues("Set-Cookie")));    // ...not the customer's
        Assert.Empty((await baskets.GetAsync(owned.Id))!.Lines);
    }

    [Fact]
    public async Task Posting_forms_over_and_over_is_slowed_down_before_it_fills_the_basket_table()
    {
        await factory.Services.GetRequiredService<SampleDataSeeder>().SeedAsync();
        using var limited = factory.WithWebHostBuilder(b => b.UseSetting("PiranhaCommerce:Shop:FormsPerMinute", "5"));
        var browser = limited.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false, HandleCookies = true });
        var cleanser = await Product("gentle-cream-cleanser");

        var statuses = new List<HttpStatusCode>();
        for (var i = 0; i < 8; i++)
        {
            var page = await browser.GetStringAsync("/product/gentle-cream-cleanser");
            var token = TokenPattern().Match(page).Groups[1].Value;
            var response = await browser.PostAsync("/basket/add", new FormUrlEncodedContent([
                KeyValuePair.Create("productId", cleanser.Id.ToString()), KeyValuePair.Create("quantity", "1"), KeyValuePair.Create("__RequestVerificationToken", token)]));
            statuses.Add(response.StatusCode);
        }
        Assert.Equal(5, statuses.Count(s => s == HttpStatusCode.Redirect));
        Assert.Equal(3, statuses.Count(s => s == HttpStatusCode.TooManyRequests));
    }
}
