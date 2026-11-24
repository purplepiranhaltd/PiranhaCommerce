using System.Text;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using PiranhaCommerce.Catalog;
using PiranhaCommerce.Customers;
using PiranhaCommerce.Ecommerce;
using PiranhaCommerce.Web.SampleData;

namespace PiranhaCommerce.IntegrationTests;

/// <summary>Wishlist, reviews, back-in-stock sign-up and basket suggestions in the sample shop, through its forms.</summary>
public partial class ShopEngagementTests(PiranhaFactory factory) : IClassFixture<PiranhaFactory>
{
    private const string Password = "Shopper-Passw0rd!";
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
        Assert.True(response.IsSuccessStatusCode, $"{(int)response.StatusCode} {url}: {body[..Math.Min(body.Length, 800)]}");
        return body;
    }

    private static async Task<string> Post(HttpClient browser, string pageWithForm, string action, params (string, string)[] fields)
    {
        var page = await browser.GetStringAsync(pageWithForm);
        var token = TokenPattern().Match(page).Groups[1].Value;
        Assert.NotEmpty(token);
        var response = await browser.PostAsync(action, new FormUrlEncodedContent(fields.Append(("__RequestVerificationToken", token)).Select(f => KeyValuePair.Create(f.Item1, f.Item2))));
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        return response.Headers.Location!.OriginalString;
    }

    /// <summary>Posts a form with files, as a browser does for a photo: the page's antiforgery token, then the fields and each file under "photos".</summary>
    private static async Task<string> PostWithPhotos(HttpClient browser, string pageWithForm, string action, (string, string)[] fields, params (string Name, byte[] Bytes, string Type)[] photos)
    {
        var page = await browser.GetStringAsync(pageWithForm);
        var token = TokenPattern().Match(page).Groups[1].Value;
        Assert.NotEmpty(token);
        var form = new MultipartFormDataContent();
        foreach (var (k, v) in fields.Append(("__RequestVerificationToken", token))) form.Add(new StringContent(v), k);
        foreach (var (name, bytes, type) in photos)
        {
            var file = new ByteArrayContent(bytes);
            file.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(type);
            form.Add(file, "photos", name);
        }
        var response = await browser.PostAsync(action, form);
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        return response.Headers.Location!.OriginalString;
    }

    private async Task<HttpClient> ShopperAsync(string? email = null)
    {
        var browser = await BrowserAsync();
        Assert.Equal("/account", await Post(browser, "/account/sign-in", "/account/register",
            ("email", email ?? $"engaged-{Guid.NewGuid():N}@example.com"), ("password", Password), ("firstName", "Eve"), ("lastName", "Engaged")));
        return browser;
    }

    private async Task<ShopProductDto> Product(string slug) =>
        (await factory.CreateClient().GetFromJsonAsync<ShopProductDto>($"/api/v1/storefront/products/by-slug/{slug}", Json))!;

    [Fact]
    public async Task A_review_can_carry_photos_that_show_once_it_is_approved_and_a_bad_file_is_explained()
    {
        var shopper = await ShopperAsync();
        var guest = await BrowserAsync();
        var cleanser = await Product("gentle-cream-cleanser");
        var page = "/product/gentle-cream-cleanser";
        Assert.Contains("name=\"photos\"", await Get(shopper, page));                                           // the form offers a file box

        var sent = await PostWithPhotos(shopper, page, "/reviews/save", [("productId", cleanser.Id.ToString()), ("returnTo", page), ("rating", "5"), ("title", "Photo review"), ("body", "See it")],
            ("good.png", ImageTests.Png(600, 400), "image/png"), ("bad.png", Encoding.UTF8.GetBytes("not a picture"), "image/png"));
        Assert.StartsWith(page, sent);
        var after = await Get(shopper, sent);                                                                     // the message rides in the address
        Assert.Contains("Thank you", after);
        Assert.Contains("\"bad.png\" was not added", after);                                                    // the problem is said, the good one is kept
        Assert.Contains("with 1 photo", after);

        using (var scope = factory.Services.CreateScope())
        {
            var reviews = scope.ServiceProvider.GetRequiredService<IReviewService>();
            var mine = (await reviews.ListAsync(ReviewStatus.Pending, cleanser.Id, 1, 50)).Items.Single(r => r.Title == "Photo review");
            Assert.DoesNotContain("review-photos", await Get(guest, page));                                      // still waiting: nobody sees the photo
            await reviews.SetStatusAsync(mine.Id, ReviewStatus.Approved, "test");
        }

        var shown = await Get(guest, page);
        Assert.Contains("class=\"review-photos\"", shown);
        var src = Regex.Match(shown, "<ul class=\"review-photos\">.*?<a href=\"([^\"]+)\"", RegexOptions.Singleline).Groups[1].Value;
        var image = await guest.GetAsync(src);
        Assert.Equal(("image/png", HttpStatusCode.OK), (image.Content.Headers.ContentType!.MediaType, image.StatusCode));

        // More photos can be added later; the review then waits for another look.
        Assert.Contains("action=\"/reviews/photo\"", await Get(shopper, page));
        await PostWithPhotos(shopper, page, "/reviews/photo", [("reviewId", Regex.Match(await shopper.GetStringAsync(page), "name=\"reviewId\" value=\"([0-9a-f-]{36})\"").Groups[1].Value), ("returnTo", page)],
            ("more.png", ImageTests.Png(300, 300), "image/png"));
        Assert.Contains("with 2 photos", await Get(shopper, page));
        Assert.DoesNotContain("review-photos", await Get(guest, page));                                           // back to waiting
    }

    [Fact]
    public async Task A_wishlist_needs_a_sign_in_then_saves_shows_and_removes_products()
    {
        var guest = await BrowserAsync();
        var cleanser = await Product("gentle-cream-cleanser");

        // A guest is sent to sign in, and back.
        var sent = await Post(guest, "/product/gentle-cream-cleanser", "/wishlist/add", ("productId", cleanser.Id.ToString()), ("returnTo", "/product/gentle-cream-cleanser"));
        Assert.StartsWith("/account/sign-in?returnTo=", sent);
        Assert.Contains("Sign in to save to your wishlist", await Get(guest, "/product/gentle-cream-cleanser"));

        var shopper = await ShopperAsync();
        var saved = await Post(shopper, "/product/gentle-cream-cleanser", "/wishlist/add", ("productId", cleanser.Id.ToString()), ("returnTo", "/product/gentle-cream-cleanser"));
        Assert.StartsWith("/product/gentle-cream-cleanser?f=", saved);
        var page = await Get(shopper, saved);
        Assert.Contains("Saved to your wishlist", page);
        Assert.Contains("On your", page);
        Assert.Contains("Gentle Cream Cleanser 150ml", await Get(shopper, "/account/wishlist"));

        // Saving twice keeps one; removing empties it.
        await Post(shopper, "/product/gentle-cream-cleanser", "/wishlist/add", ("productId", cleanser.Id.ToString()), ("returnTo", "/product/gentle-cream-cleanser"));
        Assert.Single(Regex.Matches(await Get(shopper, "/account/wishlist"), "Gentle Cream Cleanser 150ml &lt;|<strong>Gentle Cream Cleanser 150ml</strong>"));
        Assert.Equal("/account/wishlist", await Post(shopper, "/account/wishlist", "/wishlist/remove", ("productId", cleanser.Id.ToString()), ("returnTo", "/account/wishlist")));
        Assert.Contains("Nothing saved yet", await Get(shopper, "/account/wishlist"));
    }

    [Fact]
    public async Task A_review_waits_for_approval_then_shows_with_its_writer_and_one_review_per_product()
    {
        var lipstick = await Product("velvet-matte-lipstick");
        var guest = await BrowserAsync();
        Assert.Contains("Sign in</a> to write a review", await Get(guest, "/product/velvet-matte-lipstick"));

        var email = $"reviewer-{Guid.NewGuid():N}@example.com";
        var shopper = await ShopperAsync(email);
        var send = ("productId", lipstick.Id.ToString());

        // No stars: sent back with what was typed.
        var bad = await Post(shopper, "/product/velvet-matte-lipstick", "/reviews/save", send, ("rating", ""), ("title", "Nice shade"), ("body", "Lasts well."), ("returnTo", "/product/velvet-matte-lipstick"));
        var again = await Get(shopper, bad);
        Assert.Contains("Choose a rating", again);
        Assert.Contains("Nice shade", again);

        var ok = await Post(shopper, "/product/velvet-matte-lipstick", "/reviews/save", send, ("rating", "4"), ("title", "Nice shade"), ("body", "Lasts well."), ("returnTo", "/product/velvet-matte-lipstick"));
        var thanks = await Get(shopper, ok);
        Assert.Contains("will appear once we have read it", thanks);
        Assert.Contains("Your review:", thanks);                       // the writer sees it straight away...
        Assert.DoesNotContain("Lasts well.", await Get(guest, "/product/velvet-matte-lipstick"));   // ...nobody else does yet

        // A second one is refused with the reason.
        var twice = await Post(shopper, "/product/velvet-matte-lipstick", "/reviews/save", send, ("rating", "5"), ("returnTo", "/product/velvet-matte-lipstick"));
        Assert.Contains("already reviewed", await Get(shopper, twice));

        // Staff approve it; now everyone sees it, with a first name and initial, never the email.
        using (var scope = factory.Services.CreateScope())
        {
            var reviews = scope.ServiceProvider.GetRequiredService<IReviewService>();
            var mine = (await reviews.ListAsync(ReviewStatus.Pending, lipstick.Id, 1, 50)).Items.Single(r => r.Title == "Nice shade");
            await reviews.SetStatusAsync(mine.Id, ReviewStatus.Approved, "test");
        }
        var shown = await Get(guest, "/product/velvet-matte-lipstick");
        Assert.Contains("Lasts well.", shown);
        Assert.Contains("Eve E", shown);
        Assert.DoesNotContain(email, shown);
        Assert.Contains("(4 reviews)", shown);                          // the 3 seeded ones and this one

        Guid mineId;
        // The shop's reply shows under it, as plain text (markup in it is escaped, not run).
        using (var scope = factory.Services.CreateScope())
        {
            var reviews = scope.ServiceProvider.GetRequiredService<IReviewService>();
            var mine = (await reviews.ListAsync(ReviewStatus.Approved, lipstick.Id, 1, 50)).Items.Single(r => r.Title == "Nice shade");
            mineId = mine.Id;
            await reviews.SetReplyAsync(mine.Id, "Thank you, Eve! <script>alert(1)</script>", "test");
        }
        var answered = await Get(guest, "/product/velvet-matte-lipstick");
        Assert.Contains("Reply from the shop", answered);
        Assert.Contains("Thank you, Eve!", answered);
        Assert.DoesNotContain("<script>alert(1)</script>", await guest.GetStringAsync("/product/velvet-matte-lipstick")); // the raw page, before any decoding

        // Helpful: no button for a guest or for the writer; another signed-in shopper taps it, sees it marked, and can take it back.
        static int Buttons(string page) => Regex.Matches(page, @"(Marked helpful|Helpful) \(\d+\)").Count;
        Assert.Equal(0, Buttons(answered));                                                       // a guest has none
        Assert.Equal(3, Buttons(await Get(shopper, "/product/velvet-matte-lipstick")));            // the writer has one on each of the 3 seeded reviews, not on their own
        var other = await ShopperAsync();
        Assert.Equal(4, Buttons(await Get(other, "/product/velvet-matte-lipstick")));              // another shopper has one on all 4
        var tapped = await Post(other, "/product/velvet-matte-lipstick", "/reviews/helpful", ("reviewId", mineId.ToString()), ("helpful", "true"), ("returnTo", "/product/velvet-matte-lipstick"));
        Assert.EndsWith("#reviews", tapped);
        Assert.Contains("Marked helpful (1)", await Get(other, "/product/velvet-matte-lipstick"));
        Assert.Contains("1 person found this helpful", await Get(guest, "/product/velvet-matte-lipstick"));
        await Post(other, "/product/velvet-matte-lipstick", "/reviews/helpful", ("reviewId", mineId.ToString()), ("helpful", "false"), ("returnTo", "/product/velvet-matte-lipstick"));
        Assert.DoesNotContain("Marked helpful", await Get(other, "/product/velvet-matte-lipstick"));
        var asGuest = await Post(guest, "/product/velvet-matte-lipstick", "/reviews/helpful", ("reviewId", mineId.ToString()), ("helpful", "true"), ("returnTo", "/product/velvet-matte-lipstick"));
        Assert.StartsWith("/account/sign-in?returnTo=", asGuest);
    }

    [Fact]
    public async Task A_sold_out_shade_takes_a_back_in_stock_request_and_the_email_has_a_working_unsubscribe()
    {
        var lipstick = await Product("velvet-matte-lipstick");
        var plum = lipstick.Variants.Single(v => v.Values[0].Value == "Plum Noir");
        var ruby = lipstick.Variants.Single(v => v.Values[0].Value == "Ruby Red");
        var guest = await BrowserAsync();
        var email = $"waiting-{Guid.NewGuid():N}@example.com";

        // The form is on the sold-out shade only.
        Assert.Contains("Tell me when it is back", await Get(guest, $"/product/velvet-matte-lipstick?variant={plum.ProductId}"));
        Assert.DoesNotContain("Tell me when it is back", await Get(guest, $"/product/velvet-matte-lipstick?variant={ruby.ProductId}"));

        var back = $"/product/velvet-matte-lipstick?variant={plum.ProductId}";
        var asked = await Post(guest, back, "/stock-alert/subscribe", ("productId", plum.ProductId.ToString()), ("email", email), ("returnTo", back));
        Assert.Contains("email you once", await Get(guest, asked));
        // Asking again gives the same answer; a bad address is explained; one that can be bought now is refused.
        Assert.Contains("email you once", await Get(guest, await Post(guest, back, "/stock-alert/subscribe", ("productId", plum.ProductId.ToString()), ("email", email), ("returnTo", back))));
        Assert.Contains("class=\"notice error\"", await Get(guest, await Post(guest, back, "/stock-alert/subscribe", ("productId", plum.ProductId.ToString()), ("email", "nope"), ("returnTo", back))));
        Assert.Contains("class=\"notice error\"", await Get(guest, await Post(guest, back, "/stock-alert/subscribe", ("productId", ruby.ProductId.ToString()), ("email", email), ("returnTo", back))));
        using (var scope = factory.Services.CreateScope())
            Assert.Single((await scope.ServiceProvider.GetRequiredService<IStockAlertService>().ListAsync(plum.ProductId, 1, 20)).Items, s => s.Email == email);

        // Stock arrives; the one email carries an unsubscribe link that opens a page asking first, and the button removes it.
        using (var scope = factory.Services.CreateScope())
        {
            await scope.ServiceProvider.GetRequiredService<IStockService>().AdjustAsync(plum.ProductId, new StockAdjustmentInput { Delta = 5, Reason = "Delivery" }, "test");
            await scope.ServiceProvider.GetRequiredService<IStockAlertNotifier>().RunOnceAsync();
        }
        var mail = Assert.Single(factory.Email.To(email));
        var token = (string)mail.Variables["unsubscribeToken"]!;
        var unsubscribePage = await Get(guest, $"/stock-alert/unsubscribe?token={token}");
        Assert.Contains("Yes, stop the email", unsubscribePage);

        // Opening the link changed nothing; pressing the button does, once.
        Assert.Equal(HttpStatusCode.OK, (await guest.GetAsync($"/stock-alert/unsubscribe?token={token}")).StatusCode);
        var done = await Post(guest, $"/stock-alert/unsubscribe?token={token}", "/stock-alert/unsubscribe", ("token", token));
        Assert.Contains("will not email you", await Get(guest, done));
        var used = await Post(guest, $"/stock-alert/unsubscribe?token={token}", "/stock-alert/unsubscribe", ("token", token));
        Assert.Contains("already been used", await Get(guest, used));

        // Put the stock back as it was, so the sold-out shade stays sold out for the other tests.
        using var scope2 = factory.Services.CreateScope();
        await scope2.ServiceProvider.GetRequiredService<IStockService>().AdjustAsync(plum.ProductId, new StockAdjustmentInput { Delta = -5, Reason = "Test" }, "test");
    }

    [Fact]
    public async Task The_basket_suggests_what_staff_chose_to_go_with_it_but_never_what_is_already_in_it()
    {
        var lipstick = await Product("velvet-matte-lipstick");
        var liner = await Product("precision-lip-liner");
        var browser = await BrowserAsync();
        var ruby = lipstick.Variants.Single(v => v.Values[0].Value == "Ruby Red");

        await Post(browser, "/product/velvet-matte-lipstick", "/basket/add", ("productId", ruby.ProductId.ToString()), ("quantity", "1"), ("returnTo", "/product/velvet-matte-lipstick"));
        var basket = await Get(browser, "/basket");
        Assert.Contains("Goes well with", basket);
        Assert.Contains("Precision Lip Liner", basket);
        Assert.Contains("Hydra Shine Lip Gloss", basket);

        // With the liner in the basket it is no longer suggested.
        await Post(browser, "/basket", "/basket/add", ("productId", liner.Variants[0].ProductId.ToString()), ("quantity", "1"), ("returnTo", "/basket"));
        var after = (await Get(browser, "/basket")).Split("Goes well with")[1];
        Assert.DoesNotContain("Precision Lip Liner", after);
        Assert.Contains("Hydra Shine Lip Gloss", after);
    }
}
