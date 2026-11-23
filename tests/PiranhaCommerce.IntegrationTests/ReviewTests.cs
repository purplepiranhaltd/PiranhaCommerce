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

/// <summary>Product reviews: written by signed-in shoppers, approved by staff, read by anyone. Reviews are on the parent product, and verified purchases are worked out, not claimed.</summary>
[Collection(OrderSettings.Name)]
public class ReviewTests(PiranhaFactory factory) : IClassFixture<PiranhaFactory>
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };
    private static string Tag() => Guid.NewGuid().ToString("N")[..8];
    private static async Task<T> Read<T>(HttpResponseMessage r) { Assert.True(r.IsSuccessStatusCode, r.StatusCode + " " + await r.Content.ReadAsStringAsync()); return (await r.Content.ReadFromJsonAsync<T>(Json))!; }

    private async Task<(HttpClient Client, Guid Id, string Email)> ShopperAsync(string? first = null, string? last = null)
    {
        var email = $"rev-{Tag()}@example.com";
        var client = factory.CreateClient();
        var registered = await Read<RegisteredDto>(await client.PostAsJsonAsync("/api/v1/storefront/auth/register",
            new RegisterInput { Email = email, Password = "a-long-enough-password", FirstName = first, LastName = last }));
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", registered.Token.AccessToken);
        return (client, registered.Profile.Id, email);
    }

    private static async Task<ProductDto> Product(HttpClient staff, bool published = true)
    {
        var p = await Read<ProductDto>(await staff.PostAsJsonAsync("/api/v1/products", new ProductInput { Name = "Rev " + Tag(), Sku = "RV-" + Tag(), Price = 10m }));
        await staff.PutAsJsonAsync($"/api/v1/products/{p.Id}/listing", new ProductListingInput { Published = published });
        await staff.PostAsJsonAsync($"/api/v1/products/{p.Id}/stock/adjustments", new StockAdjustmentInput { Delta = 20, Reason = "Delivery" });
        return p;
    }

    private static Task<HttpResponseMessage> Write(HttpClient shopper, Guid productId, int rating, string? body = "Lovely", string? name = null) =>
        shopper.PostAsJsonAsync("/api/v1/storefront/reviews", new ReviewInput { ProductId = productId, Rating = rating, Title = "T", Body = body, DisplayName = name }, Json);

    private static Task<HttpResponseMessage> Approve(HttpClient staff, Guid reviewId, ReviewStatus status = ReviewStatus.Approved) =>
        staff.PutAsJsonAsync($"/api/v1/reviews/{reviewId}/status", new ReviewStatusInput { Status = status }, Json);

    private static async Task<PublicReviewsDto> Public(HttpClient anonymous, Guid productId) =>
        await Read<PublicReviewsDto>(await anonymous.GetAsync($"/api/v1/storefront/products/{productId}/reviews"));

    private static Task<HttpResponseMessage> Reply(HttpClient staff, Guid reviewId, string body) =>
        staff.PutAsJsonAsync($"/api/v1/reviews/{reviewId}/reply", new ReviewReplyInput { Body = body }, Json);

    private static async Task<ReviewHelpfulDto> Helpful(HttpClient shopper, Guid reviewId, bool on = true) =>
        await Read<ReviewHelpfulDto>(on ? await shopper.PutAsync($"/api/v1/storefront/reviews/{reviewId}/helpful", null) : await shopper.DeleteAsync($"/api/v1/storefront/reviews/{reviewId}/helpful"));

    [Fact]
    public async Task Signed_in_shoppers_mark_other_peoples_approved_reviews_helpful_once_each_and_can_take_it_back()
    {
        var staff = await factory.CreateAuthenticatedApiClientAsync();
        var anonymous = factory.CreateClient();
        var author = await ShopperAsync("Ada", "Lovelace");
        var bea = await ShopperAsync("Bea", "Brown");
        var cal = await ShopperAsync("Cal", "Clark");
        var product = await Product(staff);
        var review = await Read<ReviewDto>(await Write(author.Client, product.Id, 4, "Good"));

        // Not until staff approve it.
        Assert.Equal(HttpStatusCode.NotFound, (await bea.Client.PutAsync($"/api/v1/storefront/reviews/{review.Id}/helpful", null)).StatusCode);
        await Approve(staff, review.Id);
        var fresh = (await Public(anonymous, product.Id)).Reviews.Items.Single();
        Assert.Equal((0, false), (fresh.HelpfulCount, fresh.VotedByMe));

        // One vote each, however many times they tap; the mark is theirs alone to see.
        Assert.Equal((1, true), await Helpful(bea.Client, review.Id) is { } a ? (a.HelpfulCount, a.VotedByMe) : default);
        Assert.Equal((1, true), await Helpful(bea.Client, review.Id) is { } b ? (b.HelpfulCount, b.VotedByMe) : default);
        Assert.Equal((2, true), await Helpful(cal.Client, review.Id) is { } c ? (c.HelpfulCount, c.VotedByMe) : default);
        var asBea = await Read<PublicReviewsDto>(await bea.Client.GetAsync($"/api/v1/storefront/products/{product.Id}/reviews"));
        Assert.Equal((2, true), (asBea.Reviews.Items.Single().HelpfulCount, asBea.Reviews.Items.Single().VotedByMe));
        var asAnonymous = (await Public(anonymous, product.Id)).Reviews.Items.Single();
        Assert.Equal((2, false), (asAnonymous.HelpfulCount, asAnonymous.VotedByMe));
        Assert.False((await Read<PublicReviewsDto>(await author.Client.GetAsync($"/api/v1/storefront/products/{product.Id}/reviews"))).Reviews.Items.Single().VotedByMe);

        // Taking it back, twice, is fine.
        Assert.Equal((1, false), await Helpful(bea.Client, review.Id, on: false) is { } d ? (d.HelpfulCount, d.VotedByMe) : default);
        Assert.Equal(1, (await Helpful(bea.Client, review.Id, on: false)).HelpfulCount);

        // Not your own, not as a guest, not an unknown review.
        Assert.Equal(HttpStatusCode.BadRequest, (await author.Client.PutAsync($"/api/v1/storefront/reviews/{review.Id}/helpful", null)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.PutAsync($"/api/v1/storefront/reviews/{review.Id}/helpful", null)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await bea.Client.PutAsync($"/api/v1/storefront/reviews/{Guid.NewGuid()}/helpful", null)).StatusCode);

        // A review that is withdrawn from view cannot be marked, and a withdrawn product's reviews are gone with it.
        await Approve(staff, review.Id, ReviewStatus.Rejected);
        Assert.Equal(HttpStatusCode.NotFound, (await bea.Client.PutAsync($"/api/v1/storefront/reviews/{review.Id}/helpful", null)).StatusCode);
        await Approve(staff, review.Id);
        await staff.PutAsJsonAsync($"/api/v1/products/{product.Id}/listing", new ProductListingInput { Published = false });
        Assert.Equal(HttpStatusCode.NotFound, (await bea.Client.PutAsync($"/api/v1/storefront/reviews/{review.Id}/helpful", null)).StatusCode);
        await staff.PutAsJsonAsync($"/api/v1/products/{product.Id}/listing", new ProductListingInput { Published = true });

        // The votes follow the person: exported with them, deleted when they are erased (the count falls), and gone with a deleted review.
        Assert.Contains("helpfulVotes", await (await staff.GetAsync($"/api/v1/customers/{cal.Id}/export")).Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.OK, (await staff.PostAsJsonAsync($"/api/v1/customers/{cal.Id}/erase", new EraseCustomerInput { ConfirmEmail = cal.Email })).StatusCode);
        Assert.Equal(0, (await Public(anonymous, product.Id)).Reviews.Items.Single().HelpfulCount);
        await Helpful(bea.Client, review.Id);
        Assert.Equal(HttpStatusCode.NoContent, (await staff.DeleteAsync($"/api/v1/reviews/{review.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await bea.Client.PutAsync($"/api/v1/storefront/reviews/{review.Id}/helpful", null)).StatusCode);
    }

    [Fact]
    public async Task Staff_reply_to_a_review_and_shoppers_see_the_reply_only_once_the_review_is_approved()
    {
        var staff = await factory.CreateAuthenticatedApiClientAsync();
        var anonymous = factory.CreateClient();
        var (shopper, _, shopperEmail) = await ShopperAsync("Ada", "Lovelace");
        var product = await Product(staff);
        var review = await Read<ReviewDto>(await Write(shopper, product.Id, 2, "Arrived late"));
        List<PiranhaCommerce.Core.Email.EmailMessage> Told() => factory.Email.To(shopperEmail).Where(m => m.TemplateKey == EcommerceEmailTemplates.ReviewReplied).ToList();

        // A reply can be written while the review waits, but nobody else sees it until the review is approved.
        var replied = await Read<ReviewDto>(await Reply(staff, review.Id, "  Sorry about that: we have changed courier. <b>Thank you</b>  "));
        Assert.Equal("Sorry about that: we have changed courier. <b>Thank you</b>", replied.Reply);     // trimmed, stored as plain text
        Assert.NotNull(replied.RepliedUtc);
        Assert.Equal(ReviewStatus.Pending, replied.Status);
        Assert.Empty((await Public(anonymous, product.Id)).Reviews.Items);

        // The reviewer is emailed the first time (and told it is not public yet), but not again when the reply is changed.
        var email = Assert.Single(Told());
        Assert.Equal(("Ada L.", false, product.Name), (email.Variables["displayName"], email.Variables["published"], email.Variables["productName"]));
        Assert.Equal(replied.Reply, email.Variables["reply"]);

        await Approve(staff, review.Id);
        var shown = (await Public(anonymous, product.Id)).Reviews.Items.Single();
        Assert.Equal(("Sorry about that: we have changed courier. <b>Thank you</b>", true), (shown.Reply, shown.RepliedUtc is not null));

        // The reviewer sees it on their own review too; another reply replaces the first.
        Assert.Equal(shown.Reply, (await Read<List<ReviewDto>>(await shopper.GetAsync("/api/v1/storefront/reviews"))).Single().Reply);
        await Reply(staff, review.Id, "Update: it is fixed.");
        Assert.Equal("Update: it is fixed.", (await Public(anonymous, product.Id)).Reviews.Items.Single().Reply);
        Assert.Single(Told());

        // The shopper editing their review sends it back for approval; the reply is kept for when it returns.
        await shopper.PutAsJsonAsync($"/api/v1/storefront/reviews/{review.Id}", new ReviewUpdateInput { Rating = 3, Body = "Better now" }, Json);
        Assert.Empty((await Public(anonymous, product.Id)).Reviews.Items);
        await Approve(staff, review.Id);
        Assert.Equal("Update: it is fixed.", (await Public(anonymous, product.Id)).Reviews.Items.Single().Reply);

        // An empty or too long reply is refused and changes nothing; an unknown review is not found.
        Assert.Equal(HttpStatusCode.BadRequest, (await Reply(staff, review.Id, "   ")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await Reply(staff, review.Id, new string('x', 2001))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await Reply(staff, Guid.NewGuid(), "Hello")).StatusCode);
        Assert.Equal("Update: it is fixed.", (await Public(anonymous, product.Id)).Reviews.Items.Single().Reply);

        // Only staff with write access can reply or remove one.
        Assert.Equal(HttpStatusCode.Unauthorized, (await Reply(anonymous, review.Id, "Hi")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.DeleteAsync($"/api/v1/reviews/{review.Id}/reply")).StatusCode);
        Assert.Contains((int)(await Reply(shopper, review.Id, "I am not staff")).StatusCode, new[] { 401, 403 });

        // Removing it, twice, is fine; the review stays.
        Assert.Null((await Read<ReviewDto>(await staff.DeleteAsync($"/api/v1/reviews/{review.Id}/reply"))).Reply);
        Assert.Null((await Read<ReviewDto>(await staff.DeleteAsync($"/api/v1/reviews/{review.Id}/reply"))).Reply);
        Assert.Null((await Public(anonymous, product.Id)).Reviews.Items.Single().Reply);
        Assert.Equal(HttpStatusCode.NotFound, (await staff.DeleteAsync($"/api/v1/reviews/{Guid.NewGuid()}/reply")).StatusCode);

        // Answering again after removing it counts as a first reply; and a broken mail server never stops a reply being saved.
        await Reply(staff, review.Id, "Back again.");
        Assert.Equal(2, Told().Count);
        await Reply(staff, review.Id, "x"); await staff.DeleteAsync($"/api/v1/reviews/{review.Id}/reply");
        factory.Email.Fail = true;
        try { Assert.Equal("Saved anyway.", (await Read<ReviewDto>(await Reply(staff, review.Id, "Saved anyway."))).Reply); }
        finally { factory.Email.Fail = false; }
    }

    [Fact]
    public async Task A_review_is_pending_until_staff_approve_it_and_only_approved_ones_are_public_and_counted()
    {
        var staff = await factory.CreateAuthenticatedApiClientAsync();
        var p = await Product(staff);
        var ada = await ShopperAsync("Ada", "Lovelace");
        var bob = await ShopperAsync();
        var anonymous = factory.CreateClient();

        var first = await Read<ReviewDto>(await Write(ada.Client, p.Id, 5, "Brilliant <b>mug</b>"));
        var second = await Read<ReviewDto>(await Write(bob.Client, p.Id, 2));
        Assert.Equal((ReviewStatus.Pending, "Ada L.", "A customer"), (first.Status, first.DisplayName, second.DisplayName));
        Assert.Equal("Brilliant <b>mug</b>", first.Body); // kept exactly as written: whoever shows it escapes it

        var before = await Public(anonymous, p.Id);
        Assert.Equal((0, 0m), (before.Summary.Count, before.Summary.Average));
        Assert.Empty(before.Reviews.Items);

        Assert.Equal(HttpStatusCode.OK, (await Approve(staff, first.Id)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await Approve(staff, second.Id)).StatusCode);
        var after = await Public(anonymous, p.Id);
        Assert.Equal((2, 3.5m), (after.Summary.Count, after.Summary.Average));
        Assert.Equal([0, 1, 0, 0, 1], after.Summary.Distribution);
        Assert.Equal(2, after.Reviews.TotalCount);
        var json = await (await anonymous.GetAsync($"/api/v1/storefront/products/{p.Id}/reviews")).Content.ReadAsStringAsync();
        Assert.DoesNotContain(ada.Email, json); // never the email or the customer id
        Assert.DoesNotContain(ada.Id.ToString(), json);

        // Rejecting takes one out of the list and the average; the summary endpoint agrees.
        Assert.Equal(HttpStatusCode.OK, (await Approve(staff, second.Id, ReviewStatus.Rejected)).StatusCode);
        var summary = await Read<ReviewSummaryDto>(await anonymous.GetAsync($"/api/v1/storefront/products/{p.Id}/review-summary"));
        Assert.Equal((1, 5m), (summary.Count, summary.Average));
        Assert.Equal(HttpStatusCode.OK, (await Approve(staff, second.Id)).StatusCode);

        // Staff see everything, filtered by state.
        var pending = await Read<PagedResult<ReviewDto>>(await staff.GetAsync($"/api/v1/reviews?status=Pending&productId={p.Id}"));
        Assert.Empty(pending.Items);
        var all = await Read<PagedResult<ReviewDto>>(await staff.GetAsync($"/api/v1/reviews?productId={p.Id}"));
        Assert.Equal(2, all.TotalCount);
        Assert.All(all.Items, r => Assert.Equal(p.Name, r.ProductName));
    }

    [Fact]
    public async Task One_review_per_customer_and_product_which_they_can_change_and_remove_but_nobody_elses()
    {
        var staff = await factory.CreateAuthenticatedApiClientAsync();
        var p = await Product(staff);
        var ada = await ShopperAsync();
        var bob = await ShopperAsync();
        var anonymous = factory.CreateClient();

        var mine = await Read<ReviewDto>(await Write(ada.Client, p.Id, 4));
        Assert.Equal(HttpStatusCode.Conflict, (await Write(ada.Client, p.Id, 1)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await Approve(staff, mine.Id)).StatusCode);

        // Changing an approved review puts it back to pending, so a changed text is never shown unchecked.
        var changed = await Read<ReviewDto>(await ada.Client.PutAsJsonAsync($"/api/v1/storefront/reviews/{mine.Id}", new ReviewUpdateInput { Rating = 3, Body = "Changed my mind" }, Json));
        Assert.Equal((ReviewStatus.Pending, 3, "Changed my mind"), (changed.Status, changed.Rating, changed.Body));
        Assert.Equal(0, (await Public(anonymous, p.Id)).Summary.Count);

        // Someone else's review is invisible and untouchable: 404, as if it did not exist.
        Assert.Equal(HttpStatusCode.NotFound, (await bob.Client.PutAsJsonAsync($"/api/v1/storefront/reviews/{mine.Id}", new ReviewUpdateInput { Rating = 1 }, Json)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await bob.Client.DeleteAsync($"/api/v1/storefront/reviews/{mine.Id}")).StatusCode);
        Assert.Empty(await Read<List<ReviewDto>>(await bob.Client.GetAsync("/api/v1/storefront/reviews")));
        Assert.Single(await Read<List<ReviewDto>>(await ada.Client.GetAsync("/api/v1/storefront/reviews")));

        Assert.Equal(HttpStatusCode.NoContent, (await ada.Client.DeleteAsync($"/api/v1/storefront/reviews/{mine.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await Write(ada.Client, p.Id, 5)).StatusCode); // free to review again
    }

    [Fact]
    public async Task Reviews_need_a_signed_in_shopper_and_a_published_product_and_a_rating_from_one_to_five()
    {
        var staff = await factory.CreateAuthenticatedApiClientAsync();
        var live = await Product(staff);
        var hidden = await Product(staff, published: false);
        var ada = await ShopperAsync();
        var anonymous = factory.CreateClient();

        Assert.Equal(HttpStatusCode.Unauthorized, (await Write(anonymous, live.Id, 5)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await staff.PostAsJsonAsync("/api/v1/storefront/reviews", new ReviewInput { ProductId = live.Id, Rating = 5 }, Json)).StatusCode); // a staff token is not a shopper
        Assert.Equal(HttpStatusCode.BadRequest, (await Write(ada.Client, live.Id, 0)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await Write(ada.Client, live.Id, 6)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await Write(ada.Client, hidden.Id, 5)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await Write(ada.Client, Guid.NewGuid(), 5)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await anonymous.GetAsync($"/api/v1/storefront/products/{hidden.Id}/reviews")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await anonymous.GetAsync($"/api/v1/storefront/products/{Guid.NewGuid()}/review-summary")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await anonymous.GetAsync($"/api/v1/storefront/products/{live.Id}/reviews")).StatusCode);

        // Staff moderation is for staff: a read-only user can look but not change.
        var review = await Read<ReviewDto>(await Write(ada.Client, live.Id, 5));
        var (email, password) = await factory.CreateReadOnlyUserAsync();
        var reader = await factory.CreateAuthenticatedApiClientAsync(email, password);
        Assert.Equal(HttpStatusCode.OK, (await reader.GetAsync("/api/v1/reviews")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await Approve(reader, review.Id)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await reader.DeleteAsync($"/api/v1/reviews/{review.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await ada.Client.GetAsync("/api/v1/reviews")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await Approve(staff, Guid.NewGuid())).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await staff.DeleteAsync($"/api/v1/reviews/{review.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await staff.DeleteAsync($"/api/v1/reviews/{review.Id}")).StatusCode);
    }

    [Fact]
    public async Task A_variant_is_reviewed_through_its_parent_and_a_purchase_of_any_variant_makes_the_review_verified()
    {
        var staff = await factory.CreateAuthenticatedApiClientAsync();
        var parent = await Product(staff);
        Assert.True((await staff.PostAsJsonAsync($"/api/v1/products/{parent.Id}/options", new ProductOptionInput { Name = "Size", Values = ["S", "M"] })).IsSuccessStatusCode);
        var set = await Read<ProductVariantsDto>(await staff.PostAsJsonAsync($"/api/v1/products/{parent.Id}/variants/generate", new GenerateVariantsInput { Price = 12m }));
        var small = set.Variants[0].ProductId;
        await staff.PutAsJsonAsync($"/api/v1/products/{small}/listing", new ProductListingInput { Published = true });
        await staff.PostAsJsonAsync($"/api/v1/products/{small}/stock/adjustments", new StockAdjustmentInput { Delta = 5, Reason = "Delivery" });

        var buyer = await ShopperAsync();
        var browser = await ShopperAsync();
        var basket = await Read<BasketDto>(await buyer.Client.PostAsync("/api/v1/storefront/baskets", null));
        Assert.True((await buyer.Client.PutAsJsonAsync($"/api/v1/storefront/baskets/{basket.Id}/lines/{small}", new BasketLineInput { Quantity = 1 }, Json)).IsSuccessStatusCode);
        var order = await Read<OrderDto>(await buyer.Client.PostAsJsonAsync($"/api/v1/storefront/baskets/{basket.Id}/checkout", new CheckoutInput
        {
            Email = "", ShippingAddress = new AddressInput { Line1 = "1 High St", City = "Leeds", CountryCode = "GB" },
        }, Json));

        // The buyer reviews the other variant's id; it is kept against the parent, and counts as verified.
        var bought = await Read<ReviewDto>(await Write(buyer.Client, set.Variants[1].ProductId, 5));
        var notBought = await Read<ReviewDto>(await Write(browser.Client, parent.Id, 4));
        Assert.Equal((parent.Id, true, false), (bought.ProductId, bought.VerifiedPurchase, notBought.VerifiedPurchase));
        Assert.Equal(HttpStatusCode.Conflict, (await Write(buyer.Client, parent.Id, 1)).StatusCode); // same product, whichever id is given

        // A cancelled order does not count.
        var cancelBuyer = await ShopperAsync();
        var basket2 = await Read<BasketDto>(await cancelBuyer.Client.PostAsync("/api/v1/storefront/baskets", null));
        await cancelBuyer.Client.PutAsJsonAsync($"/api/v1/storefront/baskets/{basket2.Id}/lines/{small}", new BasketLineInput { Quantity = 1 }, Json);
        var order2 = await Read<OrderDto>(await cancelBuyer.Client.PostAsJsonAsync($"/api/v1/storefront/baskets/{basket2.Id}/checkout", new CheckoutInput
        {
            Email = "", ShippingAddress = new AddressInput { Line1 = "1 High St", City = "Leeds", CountryCode = "GB" },
        }, Json));
        Assert.True((await staff.PostAsJsonAsync($"/api/v1/orders/{order2.Id}/stage", new OrderStageInput { Stage = OrderStage.Cancelled }, Json)).IsSuccessStatusCode);
        Assert.False((await Read<ReviewDto>(await Write(cancelBuyer.Client, parent.Id, 3))).VerifiedPurchase);
        Assert.NotEqual(Guid.Empty, order.Id);
    }

    [Fact]
    public async Task Reviews_are_exported_with_the_customer_and_deleted_when_they_are_erased()
    {
        var staff = await factory.CreateAuthenticatedApiClientAsync();
        var p = await Product(staff);
        var ada = await ShopperAsync("Ada", "Lovelace");
        var review = await Read<ReviewDto>(await Write(ada.Client, p.Id, 5, "Erase me"));
        await Approve(staff, review.Id);

        var export = await (await staff.GetAsync($"/api/v1/customers/{ada.Id}/export")).Content.ReadAsStringAsync();
        Assert.Contains("\"reviews\"", export);
        Assert.Contains("Erase me", export);

        Assert.Equal(HttpStatusCode.OK, (await staff.PostAsJsonAsync($"/api/v1/customers/{ada.Id}/erase", new EraseCustomerInput { ConfirmEmail = ada.Email })).StatusCode);
        Assert.Equal(0, (await Public(factory.CreateClient(), p.Id)).Summary.Count);
        var left = await Read<PagedResult<ReviewDto>>(await staff.GetAsync($"/api/v1/reviews?productId={p.Id}"));
        Assert.Empty(left.Items);
    }

    [Fact]
    public async Task With_approval_switched_off_reviews_show_at_once()
    {
        var staff = await factory.CreateAuthenticatedApiClientAsync();
        var p = await Product(staff);
        var ada = await ShopperAsync();
        using var open = factory.WithWebHostBuilder(b => b.UseSetting("Piranha:Ecommerce:ReviewsNeedApproval", "false"));
        var shopper = open.CreateClient();
        shopper.DefaultRequestHeaders.Authorization = ada.Client.DefaultRequestHeaders.Authorization;
        var review = await Read<ReviewDto>(await Write(shopper, p.Id, 4));
        Assert.Equal(ReviewStatus.Approved, review.Status);
        Assert.Equal(1, (await Public(open.CreateClient(), p.Id)).Summary.Count);
    }
}
