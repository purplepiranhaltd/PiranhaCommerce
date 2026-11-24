using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.DependencyInjection;
using PiranhaCommerce.Catalog;
using PiranhaCommerce.Core.Files;
using PiranhaCommerce.Customers;
using PiranhaCommerce.Ecommerce;

namespace PiranhaCommerce.IntegrationTests;

/// <summary>Photos on reviews: shoppers attach them, staff see them before the public can, and they are as private as the review itself until it is approved.</summary>
[Collection(OrderSettings.Name)]
public class ReviewPhotoTests(PiranhaFactory factory) : IClassFixture<PiranhaFactory>
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };
    private static string Tag() => Guid.NewGuid().ToString("N")[..8];
    private static async Task<T> Read<T>(HttpResponseMessage r) { Assert.True(r.IsSuccessStatusCode, r.StatusCode + " " + await r.Content.ReadAsStringAsync()); return (await r.Content.ReadFromJsonAsync<T>(Json))!; }

    private async Task<HttpClient> ShopperAsync()
    {
        var client = factory.CreateClient();
        var registered = await Read<RegisteredDto>(await client.PostAsJsonAsync("/api/v1/storefront/auth/register",
            new RegisterInput { Email = $"ph-{Tag()}@example.com", Password = "a-long-enough-password", FirstName = "Pia", LastName = "Photo" }));
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", registered.Token.AccessToken);
        return client;
    }

    private static async Task<ProductDto> Product(HttpClient staff)
    {
        var p = await Read<ProductDto>(await staff.PostAsJsonAsync("/api/v1/products", new ProductInput { Name = "Photo " + Tag(), Sku = "PH-" + Tag(), Price = 10m }));
        await staff.PutAsJsonAsync($"/api/v1/products/{p.Id}/listing", new ProductListingInput { Published = true });
        await staff.PostAsJsonAsync($"/api/v1/products/{p.Id}/stock/adjustments", new StockAdjustmentInput { Delta = 5, Reason = "Delivery" });
        return p;
    }

    private static async Task<ReviewDto> Review(HttpClient shopper, Guid productId) =>
        await Read<ReviewDto>(await shopper.PostAsJsonAsync("/api/v1/storefront/reviews", new ReviewInput { ProductId = productId, Rating = 5, Title = "Look", Body = "See the photo" }, Json));

    private static Task<HttpResponseMessage> Upload(HttpClient shopper, Guid reviewId, byte[] bytes, string name = "me.png", string type = "image/png")
    {
        var form = new MultipartFormDataContent();
        var file = new ByteArrayContent(bytes);
        file.Headers.ContentType = new MediaTypeHeaderValue(type);
        form.Add(file, "file", name);
        return shopper.PostAsync($"/api/v1/storefront/reviews/{reviewId}/photos", form);
    }

    private static Task<HttpResponseMessage> SetStatus(HttpClient staff, Guid reviewId, ReviewStatus status) =>
        staff.PutAsJsonAsync($"/api/v1/reviews/{reviewId}/status", new ReviewStatusInput { Status = status }, Json);

    private static async Task<ReviewDto> Staff(HttpClient staff, Guid productId, Guid reviewId) =>
        (await Read<Core.Paging.PagedResult<ReviewDto>>(await staff.GetAsync($"/api/v1/reviews?productId={productId}&pageSize=100"))).Items.Single(r => r.Id == reviewId);

    private static async Task<List<ReviewPhotoDto>> PublicPhotos(HttpClient anonymous, Guid productId) =>
        (await Read<PublicReviewsDto>(await anonymous.GetAsync($"/api/v1/storefront/products/{productId}/reviews"))).Reviews.Items.SelectMany(r => r.Photos ?? []).ToList();

    [Fact]
    public async Task A_photo_is_hidden_from_the_public_until_the_review_is_approved_but_staff_and_its_writer_can_see_it()
    {
        var staff = await factory.CreateAuthenticatedApiClientAsync();
        var anonymous = factory.CreateClient();
        var shopper = await ShopperAsync();
        var product = await Product(staff);
        var review = await Review(shopper, product.Id);

        // A big picture is kept at 2000 px on its longest side, re-made rather than stored as sent, with smaller sizes beside it.
        var added = await Read<ReviewPhotoDto>(await Upload(shopper, review.Id, ImageTests.Png(3000, 1500), "C:\\fakepath\\me.png"));
        Assert.Equal((2000, 1000), (added.Width, added.Height));

        Assert.Empty(await PublicPhotos(anonymous, product.Id));                                                             // the review itself is not up yet
        Assert.Equal(HttpStatusCode.NotFound, (await anonymous.GetAsync(added.Url)).StatusCode);                             // and its photo cannot be fetched by its address
        Assert.Equal(HttpStatusCode.NotFound, (await factory.CreateClient().GetAsync(added.Url + "?width=160")).StatusCode);
        var own = await shopper.GetAsync($"/api/v1/storefront/reviews/{review.Id}/photos/{added.Id}");
        Assert.Equal("image/png", own.Content.Headers.ContentType!.MediaType);
        Assert.True(own.Headers.CacheControl!.NoStore && own.Headers.CacheControl.Private);
        var seen = await staff.GetAsync($"/api/v1/reviews/{review.Id}/photos/{added.Id}");
        Assert.Equal(HttpStatusCode.OK, seen.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await (await ShopperAsync()).GetAsync($"/api/v1/storefront/reviews/{review.Id}/photos/{added.Id}")).StatusCode);   // another shopper cannot
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync($"/api/v1/reviews/{review.Id}/photos/{added.Id}")).StatusCode);

        // The review shows its photos to its writer and to staff.
        Assert.Single((await Read<List<ReviewDto>>(await shopper.GetAsync("/api/v1/storefront/reviews"))).Single().Photos!);
        Assert.Single((await Staff(staff, product.Id, review.Id)).Photos!);

        // Approved: it is shown, at the sizes asked for, and cached.
        await SetStatus(staff, review.Id, ReviewStatus.Approved);
        var shown = Assert.Single(await PublicPhotos(anonymous, product.Id));
        Assert.Equal(added.Id, shown.Id);
        var full = await anonymous.GetAsync(shown.Url);
        Assert.Equal(("image/png", "nosniff"), (full.Content.Headers.ContentType!.MediaType, full.Headers.GetValues("X-Content-Type-Options").Single()));
        Assert.Contains("max-age", full.Headers.CacheControl!.ToString());
        var small = await anonymous.GetAsync(shown.Url + "?width=160");
        Assert.True((await small.Content.ReadAsByteArrayAsync()).Length < (await full.Content.ReadAsByteArrayAsync()).Length);

        // Rejected again: gone from view at once.
        await SetStatus(staff, review.Id, ReviewStatus.Rejected);
        Assert.Empty(await PublicPhotos(anonymous, product.Id));
        Assert.Equal(HttpStatusCode.NotFound, (await anonymous.GetAsync(shown.Url)).StatusCode);

        // A product taken off sale hides its reviews' photos too.
        await SetStatus(staff, review.Id, ReviewStatus.Approved);
        await staff.PutAsJsonAsync($"/api/v1/products/{product.Id}/listing", new ProductListingInput { Published = false });
        Assert.Equal(HttpStatusCode.NotFound, (await anonymous.GetAsync(shown.Url)).StatusCode);
    }

    [Fact]
    public async Task Photos_are_checked_limited_and_only_the_writer_can_add_them_and_a_new_one_sends_an_approved_review_back_to_waiting()
    {
        var staff = await factory.CreateAuthenticatedApiClientAsync();
        var shopper = await ShopperAsync();
        var product = await Product(staff);
        var review = await Review(shopper, product.Id);

        // What is not a picture is refused: by its bytes, not its name or what the browser says it is.
        Assert.Equal(HttpStatusCode.BadRequest, (await Upload(shopper, review.Id, Encoding.UTF8.GetBytes("not a picture"), "x.png")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await Upload(shopper, review.Id, Encoding.UTF8.GetBytes("<svg xmlns='http://www.w3.org/2000/svg'><script>alert(1)</script></svg>"), "x.svg", "image/svg+xml")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await Upload(shopper, review.Id, Encoding.UTF8.GetBytes("MZ pretend program"), "x.png", "image/png")).StatusCode);
        Assert.Empty((await Staff(staff, product.Id, review.Id)).Photos!);

        // Only its writer, only signed in, only a review that exists.
        Assert.Equal(HttpStatusCode.NotFound, (await Upload(await ShopperAsync(), review.Id, ImageTests.Png(40, 40))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await Upload(shopper, Guid.NewGuid(), ImageTests.Png(40, 40))).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await Upload(factory.CreateClient(), review.Id, ImageTests.Png(40, 40))).StatusCode);

        // Up to four a review.
        await SetStatus(staff, review.Id, ReviewStatus.Approved);
        var first = await Read<ReviewPhotoDto>(await Upload(shopper, review.Id, ImageTests.Png(40, 40)));
        Assert.Equal(ReviewStatus.Pending, (await Staff(staff, product.Id, review.Id)).Status);                                 // something new to read
        for (var i = 0; i < 3; i++) Assert.Equal(HttpStatusCode.Created, (await Upload(shopper, review.Id, ImageTests.Png(40 + i, 40))).StatusCode);
        var fifth = await Upload(shopper, review.Id, ImageTests.Png(40, 40));
        Assert.Equal(HttpStatusCode.BadRequest, fifth.StatusCode);
        Assert.Contains("at most 4", await fifth.Content.ReadAsStringAsync());

        // Taking one off needs no second look. Staff can take one off without rejecting the review.
        await SetStatus(staff, review.Id, ReviewStatus.Approved);
        Assert.Equal(HttpStatusCode.NoContent, (await shopper.DeleteAsync($"/api/v1/storefront/reviews/{review.Id}/photos/{first.Id}")).StatusCode);
        Assert.Equal(ReviewStatus.Approved, (await Staff(staff, product.Id, review.Id)).Status);
        Assert.Equal(HttpStatusCode.NotFound, (await shopper.DeleteAsync($"/api/v1/storefront/reviews/{review.Id}/photos/{first.Id}")).StatusCode);
        var remaining = (await Staff(staff, product.Id, review.Id)).Photos!;
        Assert.Equal(3, remaining.Count);
        Assert.Equal(HttpStatusCode.Unauthorized, (await factory.CreateClient().DeleteAsync($"/api/v1/reviews/{review.Id}/photos/{remaining[0].Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await staff.DeleteAsync($"/api/v1/reviews/{review.Id}/photos/{remaining[0].Id}")).StatusCode);
        Assert.Equal(ReviewStatus.Approved, (await Staff(staff, product.Id, review.Id)).Status);
        Assert.Equal(2, (await Staff(staff, product.Id, review.Id)).Photos!.Count);
    }

    [Fact]
    public async Task Deleting_a_review_or_a_photo_deletes_the_files_too()
    {
        var staff = await factory.CreateAuthenticatedApiClientAsync();
        var shopper = await ShopperAsync();
        var product = await Product(staff);
        var review = await Review(shopper, product.Id);
        var a = await Read<ReviewPhotoDto>(await Upload(shopper, review.Id, ImageTests.Png(900, 900)));
        var b = await Read<ReviewPhotoDto>(await Upload(shopper, review.Id, ImageTests.Png(900, 900)));

        async Task<bool> FileExists(Guid photoId, string name)
        {
            using var scope = factory.Services.CreateScope();
            await using var stream = await scope.ServiceProvider.GetRequiredService<IFileStore>().OpenAsync($"reviews/{review.Id}/{photoId}/{name}");
            return stream is not null;
        }

        Assert.True(await FileExists(a.Id, "original"));
        Assert.True(await FileExists(a.Id, "w480"));                                                                    // sizes were made beside it
        await shopper.DeleteAsync($"/api/v1/storefront/reviews/{review.Id}/photos/{a.Id}");
        Assert.False(await FileExists(a.Id, "original"));
        Assert.False(await FileExists(a.Id, "w480"));
        Assert.True(await FileExists(b.Id, "original"));                                                                // the other photo is untouched

        Assert.Equal(HttpStatusCode.NoContent, (await shopper.DeleteAsync($"/api/v1/storefront/reviews/{review.Id}")).StatusCode);
        Assert.False(await FileExists(b.Id, "original"));                                                               // deleting the review takes its photos with it

        // Staff deleting a review does the same.
        var other = await ShopperAsync();
        var second = await Review(other, product.Id);
        var c = await Read<ReviewPhotoDto>(await Upload(other, second.Id, ImageTests.Png(100, 100)));
        using (var scope = factory.Services.CreateScope())
        {
            await using var open = await scope.ServiceProvider.GetRequiredService<IFileStore>().OpenAsync($"reviews/{second.Id}/{c.Id}/original");
            Assert.NotNull(open);
        }
        var del = await staff.DeleteAsync($"/api/v1/reviews/{second.Id}");
        Assert.True(del.StatusCode == HttpStatusCode.NoContent, await del.Content.ReadAsStringAsync());
        using (var scope = factory.Services.CreateScope())
        {
            await using var gone = await scope.ServiceProvider.GetRequiredService<IFileStore>().OpenAsync($"reviews/{second.Id}/{c.Id}/original");
            Assert.Null(gone);
        }
    }
}
