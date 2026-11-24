using System.Net;
using System.Net.Http.Json;
using PiranhaCommerce.Catalog;
using PiranhaCommerce.Core.Validation;

namespace PiranhaCommerce.IntegrationTests;

public class BarcodeTests(PiranhaCommerceFactory factory) : IClassFixture<PiranhaCommerceFactory>
{
    private static string Tag() => Guid.NewGuid().ToString("N")[..10];

    /// <summary>A random valid UPC-A (12 digits) with a correct check digit; unique per call so runs don't collide.</summary>
    private static string NewUpcA()
    {
        var body = "0" + string.Concat(Enumerable.Range(0, 10).Select(_ => Random.Shared.Next(10)));
        var sum = 0;
        for (var i = 0; i < 11; i++) sum += (body[i] - '0') * (i % 2 == 0 ? 3 : 1);
        return body + (10 - sum % 10) % 10;
    }

    private async Task<ProductDto> NewProduct(HttpClient c) =>
        (await (await c.PostAsJsonAsync("/api/v1/products", new ProductInput { Name = "BC " + Tag(), Price = 1 })).Content.ReadFromJsonAsync<ProductDto>())!;

    [Theory]
    [InlineData("012345678905", "00012345678905")]    // UPC-A
    [InlineData("0012345678905", "00012345678905")]   // same product as EAN-13
    [InlineData("96385074", "00000096385074")]        // EAN-8
    [InlineData("  abc-123 ", "ABC-123")]             // non-GTIN: trimmed and upper-cased
    [InlineData("1234567", "1234567")]                // 7 digits is not a GTIN length
    public void Normalizes_equivalent_forms(string input, string expected) =>
        Assert.Equal(expected, BarcodeNormalizer.Normalize(input));

    [Theory]
    [InlineData("012345678906")]
    [InlineData("96385075")]
    [InlineData("")]
    [InlineData("   ")]
    public void Rejects_bad_codes(string input) =>
        Assert.Throws<PiranhaCommerceValidationException>(() => BarcodeNormalizer.Normalize(input));

    [Fact]
    public async Task Set_get_and_lookup_by_any_equivalent_form()
    {
        var c = await factory.CreateAuthenticatedApiClientAsync();
        var p = await NewProduct(c);
        var upc = NewUpcA();
        var custom = "SHELF/" + Tag();

        var set = await c.PutAsJsonAsync($"/api/v1/products/{p.Id}/barcodes", new ProductBarcodesInput { Codes = [upc, custom] });
        Assert.Equal(HttpStatusCode.OK, set.StatusCode);
        Assert.Equal(2, (await set.Content.ReadFromJsonAsync<ProductBarcodesDto>())!.Barcodes.Count);

        foreach (var scanned in new[] { upc, "0" + upc }) // UPC-A and its EAN-13 form
        {
            var found = await c.GetFromJsonAsync<ProductDto>($"/api/v1/products/lookup?barcode={Uri.EscapeDataString(scanned)}");
            Assert.Equal(p.Id, found!.Id);
        }
        // a code containing '/' and lower-case still resolves
        var viaCustom = await c.GetFromJsonAsync<ProductDto>($"/api/v1/products/lookup?barcode={Uri.EscapeDataString(custom.ToLowerInvariant())}");
        Assert.Equal(p.Id, viaCustom!.Id);

        Assert.Equal(HttpStatusCode.NotFound, (await c.GetAsync($"/api/v1/products/lookup?barcode={Uri.EscapeDataString(NewUpcA())}")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await c.GetAsync("/api/v1/products/lookup?barcode=012345678906")).StatusCode);
    }

    [Fact]
    public async Task A_code_cannot_belong_to_two_products_even_in_another_gtin_form()
    {
        var c = await factory.CreateAuthenticatedApiClientAsync();
        var a = await NewProduct(c);
        var b = await NewProduct(c);
        var upc = NewUpcA();

        await c.PutAsJsonAsync($"/api/v1/products/{a.Id}/barcodes", new ProductBarcodesInput { Codes = [upc] });

        var clash = await c.PutAsJsonAsync($"/api/v1/products/{b.Id}/barcodes", new ProductBarcodesInput { Codes = ["0" + upc] });
        Assert.Equal(HttpStatusCode.Conflict, clash.StatusCode);
        Assert.Empty((await c.GetFromJsonAsync<ProductBarcodesDto>($"/api/v1/products/{b.Id}/barcodes"))!.Barcodes);

        // the owner may re-save its own code, and replacing the set frees the old one
        Assert.Equal(HttpStatusCode.OK, (await c.PutAsJsonAsync($"/api/v1/products/{a.Id}/barcodes", new ProductBarcodesInput { Codes = [upc, "0" + upc] })).StatusCode);
        Assert.Single((await c.GetFromJsonAsync<ProductBarcodesDto>($"/api/v1/products/{a.Id}/barcodes"))!.Barcodes);
        await c.PutAsJsonAsync($"/api/v1/products/{a.Id}/barcodes", new ProductBarcodesInput { Codes = [] });
        Assert.Equal(HttpStatusCode.OK, (await c.PutAsJsonAsync($"/api/v1/products/{b.Id}/barcodes", new ProductBarcodesInput { Codes = [upc] })).StatusCode);
    }

    [Fact]
    public async Task Concurrent_claims_of_one_code_let_exactly_one_product_win()
    {
        var c = await factory.CreateAuthenticatedApiClientAsync();
        var products = await Task.WhenAll(Enumerable.Range(0, 6).Select(_ => NewProduct(c)));
        var upc = NewUpcA();

        var results = await Task.WhenAll(products.Select(p =>
            c.PutAsJsonAsync($"/api/v1/products/{p.Id}/barcodes", new ProductBarcodesInput { Codes = [upc] })));

        Assert.Equal(1, results.Count(r => r.StatusCode == HttpStatusCode.OK));
        Assert.Equal(5, results.Count(r => r.StatusCode == HttpStatusCode.Conflict));
    }

    [Fact]
    public async Task Invalid_barcodes_and_missing_products()
    {
        var c = await factory.CreateAuthenticatedApiClientAsync();
        var p = await NewProduct(c);
        Assert.Equal(HttpStatusCode.BadRequest, (await c.PutAsJsonAsync($"/api/v1/products/{p.Id}/barcodes", new ProductBarcodesInput { Codes = ["012345678906"] })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await c.PutAsJsonAsync($"/api/v1/products/{Guid.NewGuid()}/barcodes", new ProductBarcodesInput())).StatusCode);

        // deleting the product removes its barcodes, freeing the code
        var upc = NewUpcA();
        await c.PutAsJsonAsync($"/api/v1/products/{p.Id}/barcodes", new ProductBarcodesInput { Codes = [upc] });
        await c.DeleteAsync($"/api/v1/products/{p.Id}");
        Assert.Equal(HttpStatusCode.NotFound, (await c.GetAsync($"/api/v1/products/lookup?barcode={upc}")).StatusCode);
    }
}
