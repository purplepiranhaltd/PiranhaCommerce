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

/// <summary>Shopper accounts in the sample shop, driven through its forms: register, sign in, take the basket along, order as a customer, see only your own orders.</summary>
public partial class ShopAccountTests(PiranhaCommerceFactory factory) : IClassFixture<PiranhaCommerceFactory>
{
    private const string Password = "Shopper-Passw0rd!";
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };

    [GeneratedRegex("name=\"__RequestVerificationToken\"[^>]*value=\"([^\"]+)\"")]
    private static partial Regex TokenPattern();

    private static string NewEmail() => $"shopper-{Guid.NewGuid():N}@example.com";

    private async Task<HttpClient> BrowserAsync(PiranhaCommerceFactory? from = null)
    {
        await factory.Services.GetRequiredService<SampleDataSeeder>().SeedAsync();
        return factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false, HandleCookies = true });
    }

    private static async Task<string> Get(HttpClient browser, string url)
    {
        var response = await browser.GetAsync(url);
        var body = WebUtility.HtmlDecode(await response.Content.ReadAsStringAsync());
        Assert.True(response.IsSuccessStatusCode, $"{(int)response.StatusCode} {url}: {body[..Math.Min(body.Length, 800)]}");
        return body;
    }

    /// <summary>Posts a form as a page does, with the token from that page. Returns where the browser is sent.</summary>
    private static async Task<string> Post(HttpClient browser, string pageWithForm, string action, params (string, string)[] fields)
    {
        var page = await browser.GetStringAsync(pageWithForm);
        var token = TokenPattern().Match(page).Groups[1].Value;
        Assert.NotEmpty(token);
        var response = await browser.PostAsync(action, new FormUrlEncodedContent(fields.Append(("__RequestVerificationToken", token)).Select(f => KeyValuePair.Create(f.Item1, f.Item2))));
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        return response.Headers.Location!.OriginalString;
    }

    private async Task<ShopProductDto> Lipstick() =>
        (await factory.CreateClient().GetFromJsonAsync<ShopProductDto>("/api/v1/storefront/products/by-slug/velvet-matte-lipstick", Json))!;

    private static async Task AddAsync(HttpClient browser, Guid productId, int quantity = 1) =>
        Assert.Equal("/basket", await Post(browser, "/product/velvet-matte-lipstick", "/basket/add", ("productId", productId.ToString()), ("quantity", quantity.ToString()), ("returnTo", "/product/velvet-matte-lipstick")));

    private static Task<string> Register(HttpClient browser, string email, string password = Password) =>
        Post(browser, "/account/sign-in", "/account/register", ("email", email), ("password", password), ("firstName", "Sam"), ("lastName", "Shopper"));

    private static Task<string> SignIn(HttpClient browser, string email, string password = Password, string returnTo = "") =>
        Post(browser, "/account/sign-in", "/account/sign-in", ("email", email), ("password", password), ("returnTo", returnTo));

    [Fact]
    public async Task A_shopper_registers_keeps_their_basket_orders_as_a_customer_and_sees_only_their_own_orders()
    {
        var lipstick = await Lipstick();
        var ruby = lipstick.Variants.Single(v => v.Values[0].Value == "Ruby Red");
        var email = NewEmail();

        // A guest fills a basket, then registers: the basket becomes theirs and the header shows their name.
        var browser = await BrowserAsync();
        await AddAsync(browser, ruby.ProductId);
        Assert.Equal("/account", await Register(browser, email));
        var account = await Get(browser, "/account");
        Assert.Contains(email, account);
        Assert.Contains("You have not placed an order", account);
        var basket = await Get(browser, "/basket");
        Assert.Contains("Velvet Matte Lipstick", basket);
        Assert.Contains("Sam Shopper", basket);        // the header

        // Signing out ends it, and takes the basket out of this browser.
        Assert.Equal("/", await Post(browser, "/account", "/account/sign-out"));
        Assert.Contains("Your basket is empty", await Get(browser, "/basket"));
        var locked = await browser.GetAsync("/account");
        Assert.Equal(HttpStatusCode.Redirect, locked.StatusCode);
        Assert.Contains("/account/sign-in", locked.Headers.Location!.OriginalString);

        // Another browser: a wrong password is one vague answer and keeps what was typed; the right one brings the basket back.
        var other = await BrowserAsync();
        var wrong = await SignIn(other, email, "Wrong-Passw0rd!");
        Assert.StartsWith("/account/sign-in?f=", wrong);
        var again = await Get(other, wrong);
        Assert.Contains("do not match an account", again);
        Assert.Contains(email, again);
        Assert.Equal("/account", await SignIn(other, email));
        Assert.Contains("Velvet Matte Lipstick", await Get(other, "/basket"));

        // It is the customer who orders, with their account's email, whatever the form says.
        Assert.Equal("/checkout/delivery", await Post(other, "/checkout", "/checkout/details",
            ("email", "someone-else@example.com"), ("recipient", "Sam Shopper"), ("line1", "2 Low Road"), ("line2", ""), ("city", "Leeds"), ("region", ""), ("postalCode", "LS1 1AA"), ("countryCode", "GB")));
        var staff = await factory.CreateAuthenticatedApiClientAsync();
        var methods = await staff.GetFromJsonAsync<List<ShippingMethodDto>>("/api/v1/shipping-methods", Json);
        Assert.Equal("/order/confirmed", await Post(other, "/checkout/delivery", "/checkout/place", ("shippingMethodId", methods!.First(m => m.Name.StartsWith("Standard")).Id.ToString())));
        Assert.Contains("Thank you", await Get(other, "/order/confirmed"));

        var summary = (await staff.GetFromJsonAsync<PiranhaCommerce.Core.Paging.PagedResult<OrderSummaryDto>>($"/api/v1/orders?search={email}", Json))!.Items.Single();
        var order = (await staff.GetFromJsonAsync<OrderDto>($"/api/v1/orders/{summary.Id}", Json))!;
        Assert.NotNull(order.CustomerId);
        Assert.Equal(email, order.Email);

        // Their account lists it and opens it.
        Assert.Contains(summary.Reference, await Get(other, "/account"));
        var detail = await Get(other, $"/account/orders/{summary.Id}");
        Assert.Contains("Velvet Matte Lipstick", detail);

        // Nobody else can: another customer gets a 404, and so does someone not signed in.
        var stranger = await BrowserAsync();
        await Register(stranger, NewEmail());
        Assert.Equal(HttpStatusCode.NotFound, (await stranger.GetAsync($"/account/orders/{summary.Id}")).StatusCode);
        Assert.DoesNotContain(summary.Reference, await Get(stranger, "/account"));
        var nobody = await BrowserAsync();
        Assert.Equal(HttpStatusCode.Redirect, (await nobody.GetAsync($"/account/orders/{summary.Id}")).StatusCode);
    }

    [Fact]
    public async Task Signing_in_adds_the_guest_basket_to_the_one_the_customer_already_had_and_changing_the_password_ends_the_session()
    {
        var lipstick = await Lipstick();
        var coral = lipstick.Variants.Single(v => v.Values[0].Value == "Coral Crush");
        var rosewood = lipstick.Variants.Single(v => v.Values[0].Value == "Rosewood");
        var email = NewEmail();

        var first = await BrowserAsync();
        await Register(first, email);
        await AddAsync(first, coral.ProductId);
        await Post(first, "/account", "/account/sign-out");

        // As a guest on another browser: the same shade and a different one. Signing in adds them up.
        var second = await BrowserAsync();
        await AddAsync(second, coral.ProductId);
        await AddAsync(second, rosewood.ProductId);
        Assert.Equal("/account", await SignIn(second, email));
        var basket = await Get(second, "/basket");
        Assert.Contains("Coral Crush", basket);
        Assert.Contains("Rosewood", basket);
        Assert.Contains("Basket (3)", basket);                            // 1 + 1 coral, 1 rosewood

        // A password change (here by the customer through the service) ends every shop session at once.
        var profile = await ProfileOf(email);
        using (var scope = factory.Services.CreateScope())
            Assert.NotNull(await scope.ServiceProvider.GetRequiredService<ICustomerAccountService>()
                .ChangePasswordAsync(profile.Id, new ChangePasswordInput { CurrentPassword = Password, NewPassword = "Another-Passw0rd!" }));
        var after = await second.GetAsync("/account");
        Assert.Equal(HttpStatusCode.Redirect, after.StatusCode);
        Assert.Contains("/account/sign-in", after.Headers.Location!.OriginalString);
    }

    private async Task<ProfileDto> ProfileOf(string email)
    {
        using var scope = factory.Services.CreateScope();
        var found = (await scope.ServiceProvider.GetRequiredService<ICustomerService>().ListAsync(1, 5, email)).Items.Single();
        return (await scope.ServiceProvider.GetRequiredService<ICustomerAccountService>().GetProfileAsync(found.Id))!;
    }

    [Fact]
    public async Task A_forgotten_password_is_reset_from_the_emailed_code_and_the_form_never_says_who_has_an_account()
    {
        var email = NewEmail();
        var browser = await BrowserAsync();
        await Register(browser, email);
        await Post(browser, "/account", "/account/sign-out");

        // The same answer for an address with an account and one without.
        var known = await Post(browser, "/account/forgot", "/account/forgot", ("email", email));
        var unknown = await Post(browser, "/account/forgot", "/account/forgot", ("email", NewEmail()));
        Assert.StartsWith("/account/forgot?f=", known);
        Assert.StartsWith("/account/forgot?f=", unknown);
        Assert.Contains("If that address has an account", await Get(browser, known));
        Assert.Contains("If that address has an account", await Get(browser, unknown));

        var code = (string)factory.Email.To(email)[^1].Variables["code"]!;
        var reset = await Post(browser, $"/account/reset?email={Uri.EscapeDataString(email)}&token={Uri.EscapeDataString(code)}", "/account/reset", ("email", email), ("token", code), ("password", "Brand-New-Passw0rd!"));
        Assert.StartsWith("/account/sign-in?f=", reset);
        Assert.Contains("Your password has been changed", await Get(browser, reset));

        // The code works once; the new password signs in and the old one does not.
        var reused = await Post(browser, "/account/forgot", "/account/reset", ("email", email), ("token", code), ("password", "Yet-Another-Passw0rd!"));
        Assert.StartsWith("/account/forgot?f=", reused);
        Assert.StartsWith("/account/sign-in?f=", await SignIn(browser, email, Password));
        Assert.Equal("/account", await SignIn(browser, email, "Brand-New-Passw0rd!"));
    }

    [Fact]
    public async Task Sign_in_forms_are_limited_harder_than_the_rest_and_a_shop_can_require_an_account_to_order()
    {
        await factory.Services.GetRequiredService<SampleDataSeeder>().SeedAsync();
        using var strict = factory.WithWebHostBuilder(b => b
            .UseSetting("PiranhaCommerce:Shop:AccountFormsPerMinute", "3")
            .UseSetting("PiranhaCommerce:Ecommerce:AllowGuestCheckout", "false"));
        var browser = strict.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false, HandleCookies = true });

        var statuses = new List<HttpStatusCode>();
        for (var i = 0; i < 5; i++)
        {
            var token = TokenPattern().Match(await browser.GetStringAsync("/account/sign-in")).Groups[1].Value;
            var response = await browser.PostAsync("/account/sign-in", new FormUrlEncodedContent([
                KeyValuePair.Create("email", "nobody@example.com"), KeyValuePair.Create("password", "Wrong-Passw0rd!"), KeyValuePair.Create("__RequestVerificationToken", token)]));
            statuses.Add(response.StatusCode);
        }
        Assert.Equal(3, statuses.Count(s => s == HttpStatusCode.Redirect));
        Assert.Equal(2, statuses.Count(s => s == HttpStatusCode.TooManyRequests));

        // With guest orders off, checkout asks a guest to sign in.
        var lipstick = await Lipstick();
        var guest = strict.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false, HandleCookies = true });
        var ruby = lipstick.Variants.Single(v => v.Values[0].Value == "Ruby Red");
        await AddAsync(guest, ruby.ProductId);
        Assert.Contains("only takes orders from signed-in customers", await Get(guest, "/checkout"));
        var sent = await Post(guest, "/basket", "/checkout/details", ("email", "g@example.com"), ("line1", "1 High Street"), ("city", "Leeds"), ("countryCode", "GB"));
        Assert.StartsWith("/account/sign-in", sent);
    }
}
