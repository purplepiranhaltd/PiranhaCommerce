using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using PiranhaCommerce.Catalog;
using SkiaSharp;

namespace PiranhaCommerce.IntegrationTests;

/// <summary>Product pictures: upload checks, sizes made at upload, the gallery order, variants, and the slugs and meta text a shop front needs.</summary>
public class ImageTests(PiranhaCommerceFactory factory) : IClassFixture<PiranhaCommerceFactory>
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };
    private static string Tag() => Guid.NewGuid().ToString("N")[..8];
    private static async Task<T> Read<T>(HttpResponseMessage r) => (await r.Content.ReadFromJsonAsync<T>(Json))!;

    internal static byte[] Png(int width, int height)
    {
        using var bitmap = new SKBitmap(width, height);
        bitmap.Erase(SKColors.CornflowerBlue);
        using var image = SKImage.FromBitmap(bitmap);
        return image.Encode(SKEncodedImageFormat.Png, 100).ToArray();
    }

    /// <summary>A JPEG carrying the camera's orientation tag (6: the photo must be turned a quarter clockwise to be upright).</summary>
    private static byte[] RotatedJpeg(int width, int height)
    {
        using var bitmap = new SKBitmap(width, height);
        bitmap.Erase(SKColors.Orange);
        using var image = SKImage.FromBitmap(bitmap);
        var jpeg = image.Encode(SKEncodedImageFormat.Jpeg, 90).ToArray();
        byte[] exif =
        [
            0xFF, 0xE1, 0x00, 0x22, (byte)'E', (byte)'x', (byte)'i', (byte)'f', 0, 0,
            (byte)'I', (byte)'I', 0x2A, 0x00, 0x08, 0x00, 0x00, 0x00,
            0x01, 0x00, 0x12, 0x01, 0x03, 0x00, 0x01, 0x00, 0x00, 0x00, 0x06, 0x00, 0x00, 0x00,
            0x00, 0x00, 0x00, 0x00,
        ];
        return [.. jpeg[..2], .. exif, .. jpeg[2..]];
    }

    private static (int Width, int Height) SizeOf(byte[] bytes)
    {
        using var codec = SKCodec.Create(new SKMemoryStream(bytes))!;
        return (codec.Info.Width, codec.Info.Height);
    }

    internal static MultipartFormDataContent Form(byte[] bytes, string fileName, string contentType = "image/png", string? alt = null)
    {
        var form = new MultipartFormDataContent();
        var file = new ByteArrayContent(bytes);
        file.Headers.ContentType = new MediaTypeHeaderValue(contentType);
        form.Add(file, "file", fileName);
        if (alt is not null) form.Add(new StringContent(alt), "altText");
        return form;
    }

    private static async Task<ProductDto> NewProduct(HttpClient staff) =>
        await Read<ProductDto>(await staff.PostAsJsonAsync("/api/v1/products", new ProductInput { Name = "Img " + Tag(), Sku = "IMG-" + Tag(), Price = 5m }));

    private static async Task<ProductImageDto> Upload(HttpClient staff, ProductDto p, byte[] bytes, string name = "photo.png", string type = "image/png", string? alt = null)
    {
        var r = await staff.PostAsync($"/api/v1/products/{p.Id}/images", Form(bytes, name, type, alt));
        Assert.Equal(HttpStatusCode.Created, r.StatusCode);
        return await Read<ProductImageDto>(r);
    }

    [Fact]
    public async Task An_uploaded_picture_is_checked_sized_and_served_to_anyone_with_caching()
    {
        var staff = await factory.CreateAuthenticatedApiClientAsync();
        var anon = factory.CreateClient();
        var p = await NewProduct(staff);
        var original = Png(1200, 600);
        var img = await Upload(staff, p, original, "../../etc/Big Photo.png", alt: "A blue square");

        Assert.Equal((1200, 600, "image/png", "A blue square", 0), (img.Width, img.Height, img.ContentType, img.AltText, img.Position));
        Assert.Equal("Big Photo.png", img.FileName); // a path in the name is dropped
        Assert.Equal([160, 480, 1024], img.Sizes);

        var full = await anon.GetAsync(img.Url);
        Assert.Equal(HttpStatusCode.OK, full.StatusCode);
        Assert.Equal("image/png", full.Content.Headers.ContentType?.MediaType);
        Assert.Equal(original, await full.Content.ReadAsByteArrayAsync());
        Assert.Contains("max-age", full.Headers.CacheControl?.ToString());

        // The smallest made size at least as wide as asked; bigger than any made size gives the original; a made size is never an enlargement.
        Assert.Equal((480, 240), SizeOf(await (await anon.GetAsync(img.Url + "?width=300")).Content.ReadAsByteArrayAsync()));
        Assert.Equal((160, 80), SizeOf(await (await anon.GetAsync(img.Url + "?width=100")).Content.ReadAsByteArrayAsync()));
        Assert.Equal((1200, 600), SizeOf(await (await anon.GetAsync(img.Url + "?width=2000")).Content.ReadAsByteArrayAsync()));

        var again = new HttpRequestMessage(HttpMethod.Get, img.Url);
        again.Headers.IfNoneMatch.Add(full.Headers.ETag!);
        Assert.Equal(HttpStatusCode.NotModified, (await anon.SendAsync(again)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await anon.GetAsync($"/api/v1/storefront/guest/product-images/{Guid.NewGuid()}")).StatusCode);
    }

    [Fact]
    public async Task A_photo_is_turned_upright_from_its_camera_orientation_and_a_small_picture_is_not_enlarged()
    {
        var staff = await factory.CreateAuthenticatedApiClientAsync();
        var anon = factory.CreateClient();
        var p = await NewProduct(staff);
        var photo = await Upload(staff, p, RotatedJpeg(400, 200), "cam.jpg", "image/jpeg");
        Assert.Equal((200, 400, "image/jpeg"), (photo.Width, photo.Height, photo.ContentType)); // reported as it will be seen
        Assert.Equal([160], photo.Sizes); // 480 and 1024 would enlarge a picture 400 on its longest side
        var small = SizeOf(await (await anon.GetAsync(photo.Url + "?width=100")).Content.ReadAsByteArrayAsync());
        Assert.Equal((80, 160), small); // upright: taller than wide

        var tiny = await Upload(staff, p, Png(100, 50));
        Assert.Empty(tiny.Sizes); // nothing smaller than 160 to make from 100 wide
    }

    [Fact]
    public async Task Only_real_pictures_are_accepted()
    {
        var staff = await factory.CreateAuthenticatedApiClientAsync();
        var p = await NewProduct(staff);
        var url = $"/api/v1/products/{p.Id}/images";

        // By the file's own bytes, whatever its name or claimed type says.
        Assert.Equal(HttpStatusCode.BadRequest, (await staff.PostAsync(url, Form("<svg xmlns='http://www.w3.org/2000/svg'><script>alert(1)</script></svg>"u8.ToArray(), "x.png"))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await staff.PostAsync(url, Form("just some text"u8.ToArray(), "x.jpg", "image/jpeg"))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await staff.PostAsync(url, Form([], "empty.png"))).StatusCode);
        var truncated = Png(300, 300)[..20]; // starts like a PNG but is cut off inside its header
        Assert.Equal(HttpStatusCode.BadRequest, (await staff.PostAsync(url, Form(truncated, "cut.png"))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await staff.PostAsync($"/api/v1/products/{Guid.NewGuid()}/images", Form(Png(10, 10), "a.png"))).StatusCode);

        var reader = await factory.CreateReadOnlyUserAsync();
        var readOnly = await factory.CreateAuthenticatedApiClientAsync(reader.Email, reader.Password);
        Assert.Equal(HttpStatusCode.Forbidden, (await readOnly.PostAsync(url, Form(Png(10, 10), "a.png"))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await readOnly.GetAsync(url)).StatusCode);
    }

    private sealed class SmallLimitsFactory : PiranhaCommerceFactory
    {
        protected override IReadOnlyDictionary<string, string?> ExtraSettings => new Dictionary<string, string?>
        {
            ["PiranhaCommerce:Images:MaxBytes"] = "2000", ["PiranhaCommerce:Images:MaxPerProduct"] = "2", ["PiranhaCommerce:Images:Sizes"] = "64",
        };
    }

    [Fact]
    public async Task The_size_and_count_limits_come_from_settings_and_the_sizes_made_too()
    {
        await using var small = new SmallLimitsFactory();
        var staff = await small.CreateAuthenticatedApiClientAsync();
        var p = await NewProduct(staff);
        var url = $"/api/v1/products/{p.Id}/images";
        var noisy = NoisyPng(120, 120);
        Assert.True(noisy.Length > 2000);
        Assert.Equal(HttpStatusCode.BadRequest, (await staff.PostAsync(url, Form(noisy, "noisy.png"))).StatusCode);

        var a = await Upload(staff, p, Png(200, 100));
        Assert.Equal([64], a.Sizes);
        await Upload(staff, p, Png(200, 100));
        Assert.Equal(HttpStatusCode.BadRequest, (await staff.PostAsync(url, Form(Png(200, 100), "third.png"))).StatusCode);
    }

    private static byte[] NoisyPng(int w, int h)
    {
        using var bitmap = new SKBitmap(w, h);
        var rnd = new Random(7);
        for (var x = 0; x < w; x++) for (var y = 0; y < h; y++) bitmap.SetPixel(x, y, new SKColor((byte)rnd.Next(256), (byte)rnd.Next(256), (byte)rnd.Next(256)));
        using var image = SKImage.FromBitmap(bitmap);
        return image.Encode(SKEncodedImageFormat.Png, 100).ToArray();
    }

    [Fact]
    public async Task The_gallery_has_an_order_with_a_main_picture_alt_text_and_deleting_removes_the_files()
    {
        var staff = await factory.CreateAuthenticatedApiClientAsync();
        var anon = factory.CreateClient();
        var p = await NewProduct(staff);
        var a = await Upload(staff, p, Png(300, 300));
        var b = await Upload(staff, p, Png(310, 310));
        var c = await Upload(staff, p, Png(320, 320));
        Assert.Equal([0, 1, 2], new[] { a, b, c }.Select(i => i.Position));

        var order = await staff.PutAsJsonAsync($"/api/v1/products/{p.Id}/images/order", new ProductImageOrderInput { ImageIds = [c.Id, a.Id, b.Id] });
        Assert.Equal([c.Id, a.Id, b.Id], (await Read<ProductImagesDto>(order)).Images.Select(i => i.Id));
        Assert.Equal(HttpStatusCode.BadRequest, (await staff.PutAsJsonAsync($"/api/v1/products/{p.Id}/images/order", new ProductImageOrderInput { ImageIds = [c.Id, a.Id] })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await staff.PutAsJsonAsync($"/api/v1/products/{p.Id}/images/order", new ProductImageOrderInput { ImageIds = [c.Id, c.Id, a.Id] })).StatusCode);

        var alt = await Read<ProductImageDto>(await staff.PutAsJsonAsync($"/api/v1/products/{p.Id}/images/{b.Id}", new ProductImageInput { AltText = "  Side view " }));
        Assert.Equal("Side view", alt.AltText);

        Assert.Equal(HttpStatusCode.NoContent, (await staff.DeleteAsync($"/api/v1/products/{p.Id}/images/{a.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await staff.DeleteAsync($"/api/v1/products/{p.Id}/images/{a.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await anon.GetAsync(a.Url)).StatusCode);
        Assert.False(Directory.Exists(Path.Combine(factory.FilesFolder, "products", p.Id.ToString(), a.Id.ToString())));
        Assert.Equal([c.Id, b.Id], (await Read<ProductImagesDto>(await staff.GetAsync($"/api/v1/products/{p.Id}/images"))).Images.Select(i => i.Id));

        // Deleting the product takes all its files with it.
        Assert.True(Directory.Exists(Path.Combine(factory.FilesFolder, "products", p.Id.ToString())));
        Assert.Equal(HttpStatusCode.NoContent, (await staff.DeleteAsync($"/api/v1/products/{p.Id}")).StatusCode);
        Assert.False(Directory.Exists(Path.Combine(factory.FilesFolder, "products", p.Id.ToString())));
        Assert.Equal(HttpStatusCode.NotFound, (await anon.GetAsync(b.Url)).StatusCode);
    }

    [Fact]
    public async Task A_variant_shows_its_own_pictures_or_else_its_parents()
    {
        var staff = await factory.CreateAuthenticatedApiClientAsync();
        var parent = await NewProduct(staff);
        await staff.PostAsJsonAsync($"/api/v1/products/{parent.Id}/options", new ProductOptionInput { Name = "Size", Values = ["S", "L"] });
        var variants = (await Read<ProductVariantsDto>(await staff.PostAsJsonAsync($"/api/v1/products/{parent.Id}/variants/generate", new GenerateVariantsInput()))).Variants;
        var shared = await Upload(staff, parent, Png(300, 300));

        var inherited = await Read<ProductImagesDto>(await staff.GetAsync($"/api/v1/products/{variants[0].ProductId}/images"));
        Assert.True(inherited.Inherited);
        Assert.Equal([shared.Id], inherited.Images.Select(i => i.Id));

        var own = await Upload(staff, new ProductDto(variants[0].ProductId, "", null, null, 0, default, null), Png(310, 310));
        var after = await Read<ProductImagesDto>(await staff.GetAsync($"/api/v1/products/{variants[0].ProductId}/images"));
        Assert.False(after.Inherited);
        Assert.Equal([own.Id], after.Images.Select(i => i.Id));
        Assert.True((await Read<ProductImagesDto>(await staff.GetAsync($"/api/v1/products/{variants[1].ProductId}/images"))).Inherited);
    }

    [Fact]
    public async Task Products_and_categories_have_unique_slugs_and_meta_text_and_can_be_found_by_slug()
    {
        var staff = await factory.CreateAuthenticatedApiClientAsync();
        var slug = "blue-tee-" + Tag();
        var p = await Read<ProductDto>(await staff.PostAsJsonAsync("/api/v1/products", new ProductInput
        {
            Name = "Slug " + Tag(), Price = 1m, Slug = "  " + slug.ToUpperInvariant() + " ", MetaTitle = " Blue tee ", MetaDescription = "A tee that is blue.",
        }));
        Assert.Equal((slug, "Blue tee", "A tee that is blue."), (p.Slug, p.MetaTitle, p.MetaDescription));
        Assert.Equal(p.Id, (await Read<ProductDto>(await staff.GetAsync($"/api/v1/products/by-slug/{slug.ToUpperInvariant()}"))).Id);
        Assert.Equal(HttpStatusCode.NotFound, (await staff.GetAsync("/api/v1/products/by-slug/no-such-product")).StatusCode);

        Assert.Equal(HttpStatusCode.BadRequest, (await staff.PostAsJsonAsync("/api/v1/products", new ProductInput { Name = "Dup " + Tag(), Slug = slug })).StatusCode);
        foreach (var bad in new[] { "two words", "under_score", "-lead", "trail-", "double--hyphen", "café" })
            Assert.Equal(HttpStatusCode.BadRequest, (await staff.PostAsJsonAsync("/api/v1/products", new ProductInput { Name = "Bad " + Tag(), Slug = bad })).StatusCode);
        // Two products with no slug are fine, and a product keeps its own slug when saved again.
        Assert.Equal(HttpStatusCode.Created, (await staff.PostAsJsonAsync("/api/v1/products", new ProductInput { Name = "None " + Tag() })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await staff.PutAsJsonAsync($"/api/v1/products/{p.Id}", new ProductInput { Name = p.Name, Slug = slug })).StatusCode);
        var cleared = await Read<ProductDto>(await staff.PutAsJsonAsync($"/api/v1/products/{p.Id}", new ProductInput { Name = p.Name }));
        Assert.Null(cleared.Slug);

        var catSlug = "tees-" + Tag();
        var cat = await Read<CategoryDto>(await staff.PostAsJsonAsync("/api/v1/categories", new CategoryInput { Name = "Cat " + Tag(), Slug = catSlug, MetaTitle = "Tees" }));
        Assert.Equal(cat.Id, (await Read<CategoryDto>(await staff.GetAsync($"/api/v1/categories/by-slug/{catSlug}"))).Id);
        Assert.Equal(HttpStatusCode.BadRequest, (await staff.PostAsJsonAsync("/api/v1/categories", new CategoryInput { Name = "Dup " + Tag(), Slug = catSlug })).StatusCode);
    }
}
