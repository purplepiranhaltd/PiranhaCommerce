using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using PiranhaCommerce.Catalog;
using PiranhaCommerce.Ecommerce;

namespace PiranhaCommerce.IntegrationTests;

/// <summary>Catalogue import and export as CSV: creating and updating products, variants, categories, barcodes, stock and the online listing, with checking before saving.</summary>
public class ImportExportTests(PiranhaCommerceFactory factory) : IClassFixture<PiranhaCommerceFactory>
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };
    private static string Tag() => Guid.NewGuid().ToString("N")[..8];
    private static async Task<T> Read<T>(HttpResponseMessage r) => (await r.Content.ReadFromJsonAsync<T>(Json))!;

    private static Task<HttpResponseMessage> Post(HttpClient c, string csv, string query = "")
    {
        var form = new MultipartFormDataContent();
        var file = new ByteArrayContent(Encoding.UTF8.GetBytes(csv));
        file.Headers.ContentType = new MediaTypeHeaderValue("text/csv");
        form.Add(file, "file", "products.csv");
        return c.PostAsync("/api/v1/products/import" + query, form);
    }

    private static async Task<ProductImportResultDto> Import(HttpClient c, string csv, string query = "")
    {
        var r = await Post(c, csv, query);
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        return await Read<ProductImportResultDto>(r);
    }

    private static async Task<ProductDto?> Find(HttpClient c, string sku) =>
        (await Read<PiranhaCommerce.Core.Paging.PagedResult<ProductDto>>(await c.GetAsync($"/api/v1/products?search={Uri.EscapeDataString(sku)}&pageSize=100"))).Items
            .FirstOrDefault(p => string.Equals(p.Sku, sku, StringComparison.OrdinalIgnoreCase));

    private static async Task<decimal> Stock(HttpClient c, Guid id) => (await Read<StockLevelDto>(await c.GetAsync($"/api/v1/products/{id}/stock"))).Quantity;

    private static async Task<string> Export(HttpClient c, string search)
    {
        var r = await c.GetAsync($"/api/v1/products/export?search={Uri.EscapeDataString(search)}");
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        Assert.Equal("text/csv", r.Content.Headers.ContentType?.MediaType);
        Assert.Contains("attachment", r.Content.Headers.ContentDisposition?.ToString());
        return Encoding.UTF8.GetString(await r.Content.ReadAsByteArrayAsync()).TrimStart('﻿');
    }

    [Fact]
    public async Task A_file_creates_products_with_categories_barcodes_stock_and_online_listing_and_a_dry_run_saves_nothing()
    {
        var staff = await factory.CreateAuthenticatedApiClientAsync();
        var t = Tag(); var sku = "IMP-" + t; var tax = "ImpTax" + t;
        await staff.PostAsJsonAsync("/api/v1/tax-classes", new TaxClassInput { Name = tax });
        var taxClass = (await Read<List<TaxClassDto>>(await staff.GetAsync("/api/v1/tax-classes"))).Single(c => c.Name == tax);
        var csv = "sku,name,price,description,slug,categories,barcodes,stock,published,tax_class,weight_grams\r\n" +
                  $"{sku},Blue tee,12.50,A tee,imp-{t},Clothing {t} > Tees | Sale {t},BC{t},7,true,{tax},250\r\n";

        var dry = await Import(staff, csv, "?dryRun=true");
        Assert.Equal((true, false, 1, 0), (dry.DryRun, dry.Applied, dry.Created, dry.Failed));
        Assert.Null(await Find(staff, sku));
        Assert.DoesNotContain(await Read<List<CategoryDto>>(await staff.GetAsync("/api/v1/categories")), c => c.Name == $"Clothing {t}"); // categories made during the check are undone too

        var done = await Import(staff, csv);
        Assert.Equal((true, 1, 0, 0), (done.Applied, done.Created, done.Updated, done.Failed));
        var p = (await Find(staff, sku))!;
        Assert.Equal(("Blue tee", 12.5m, "A tee", $"imp-{t}"), (p.Name, p.Price, p.Description, p.Slug));
        Assert.Equal(p.Id, done.Results.Single().ProductId);

        var cats = await Read<List<CategoryDto>>(await staff.GetAsync("/api/v1/categories"));
        var ids = (await Read<ProductCategoriesDto>(await staff.GetAsync($"/api/v1/products/{p.Id}/categories"))).CategoryIds;
        var tees = cats.Single(c => c.Id != Guid.Empty && c.Name == "Tees" && ids.Contains(c.Id));
        Assert.Equal($"Clothing {t}", cats.Single(c => c.Id == tees.ParentId).Name);
        Assert.Contains(cats.Single(c => c.Name == $"Sale {t}").Id, ids);
        Assert.Equal([$"BC{t}"], (await Read<ProductBarcodesDto>(await staff.GetAsync($"/api/v1/products/{p.Id}/barcodes"))).Barcodes.Select(b => b.Code));
        Assert.Equal(7m, await Stock(staff, p.Id));
        var movement = (await Read<PiranhaCommerce.Core.Paging.PagedResult<StockMovementDto>>(await staff.GetAsync($"/api/v1/products/{p.Id}/stock/movements"))).Items.Single();
        Assert.Equal((7m, "Import"), (movement.Delta, movement.Reason));
        Assert.StartsWith("import-", movement.Reference);
        var listing = await Read<ProductListingDto>(await staff.GetAsync($"/api/v1/products/{p.Id}/listing"));
        Assert.Equal((true, taxClass.Id, 250), (listing.Published, listing.TaxClassId, listing.WeightGrams));
    }

    [Fact]
    public async Task A_second_file_updates_only_what_it_says_blank_leaves_alone_clear_empties_and_stock_is_a_target()
    {
        var staff = await factory.CreateAuthenticatedApiClientAsync();
        var t = Tag(); var sku = "UPD-" + t;
        await Import(staff, $"sku,name,price,description,stock\n{sku},Original,10,Some words,8\n");
        var p = (await Find(staff, sku))!;

        var r = await Import(staff, $"sku,price,description,stock\n{sku},15,[clear],3\n"); // name column absent
        Assert.Equal((0, 1), (r.Created, r.Updated));
        var after = (await Find(staff, sku))!;
        Assert.Equal(("Original", 15m), (after.Name, after.Price));
        Assert.Null(after.Description);
        Assert.Equal(3m, await Stock(staff, p.Id));

        await Import(staff, $"sku,name,price,description,stock\n{sku},,,,3\n"); // blank cells: nothing changes, and the same target moves no stock
        Assert.Equal(("Original", 15m), ((await Find(staff, sku))!.Name, (await Find(staff, sku))!.Price));
        var moves = (await Read<PiranhaCommerce.Core.Paging.PagedResult<StockMovementDto>>(await staff.GetAsync($"/api/v1/products/{p.Id}/stock/movements"))).Items;
        Assert.Equal([-5m, 8m], moves.Select(m => m.Delta).Order());

        // Matched by id: the SKU can be changed.
        var renamed = "UPD2-" + t;
        await Import(staff, $"id,sku\n{p.Id},{renamed}\n");
        Assert.Equal(p.Id, (await Find(staff, renamed))!.Id);
        Assert.Null(await Find(staff, sku));
    }

    [Fact]
    public async Task Variant_rows_point_at_their_parent_and_make_its_options_whatever_order_the_file_is_in()
    {
        var staff = await factory.CreateAuthenticatedApiClientAsync();
        var t = Tag(); var parent = "PAR-" + t;
        var head = "sku,parent_sku,name,price,option1_name,option1_value,option2_name,option2_value,stock\n";
        var csv = head +
                  $"{parent}-S,{parent},,9.99,Size,S,Colour,Red,4\n" +
                  $"{parent}-M,{parent},,10.99,Size,M,Colour,Red,5\n" + // the parent comes last in the file
                  $"{parent},,Tee {t},8,,,,,\n";
        var r = await Import(staff, csv);
        Assert.Equal((3, 0, 0), (r.Created, r.Updated, r.Failed));

        var p = (await Find(staff, parent))!;
        var set = await Read<ProductVariantsDto>(await staff.GetAsync($"/api/v1/products/{p.Id}/variants"));
        Assert.Equal(["Colour", "Size"], set.Options.Select(o => o.Name).Order());
        Assert.Equal([(9.99m, $"{parent}-S"), (10.99m, $"{parent}-M")], set.Variants.Select(v => (v.Price, v.Sku)).OrderBy(v => v.Price));
        Assert.Equal($"Tee {t} - S / Red", set.Variants.Single(v => v.Sku == $"{parent}-S").Name);
        Assert.Equal(4m, await Stock(staff, set.Variants.Single(v => v.Sku == $"{parent}-S").ProductId));

        // Again: the same variants are updated, not doubled.
        var again = await Import(staff, csv.Replace("9.99", "7.77"));
        Assert.Equal((0, 3, 0), (again.Created, again.Updated, again.Failed));
        var after = await Read<ProductVariantsDto>(await staff.GetAsync($"/api/v1/products/{p.Id}/variants"));
        Assert.Equal(2, after.Variants.Count);
        Assert.Equal(7.77m, after.Variants.Single(v => v.Sku == $"{parent}-S").Price);

        var bad = await Import(staff, head +
            $"{parent}-S,{parent},,,Size,M,Colour,Red,\n" +       // would change what the variant is
            $"{parent}-X,{parent},,,Size,S,Colour,Red,\n" +       // a combination that already exists
            $"{parent}-Y,{parent},,,Size,L,,,\n" +                // misses an option the parent has
            $"{parent}-Z,{parent},,,Size,L,Colour,Red,\n" +      // fine: a new size
            $"NOPARENT-{t},NOSUCH-{t},,,Size,S,,,\n");
        Assert.Equal(["failed", "failed", "failed", "created", "failed"], bad.Results.Select(x => x.Action));
        Assert.Contains("cannot be changed", bad.Results[0].Errors.Single());
        Assert.Contains("Give a value", bad.Results[2].Errors.Single());
        Assert.Contains("no product", bad.Results[4].Errors.Single(), StringComparison.OrdinalIgnoreCase);
        Assert.False(bad.Applied); // there were bad rows, so even the good one was not saved
        Assert.Null(await Find(staff, $"{parent}-Z"));
    }

    [Fact]
    public async Task One_bad_row_means_nothing_is_saved_unless_the_good_rows_are_asked_for()
    {
        var staff = await factory.CreateAuthenticatedApiClientAsync();
        var t = Tag(); var good = "GOOD-" + t; var bad = "BAD-" + t;
        var csv = "sku,name,price\n" +
                  $"{good},Good one,5\n" +
                  $"{bad},Bad price,abc\n" +
                  $"NONAME-{t},,5\n" +
                  $"{good},Same sku again,5\n" +
                  $"TAXLESS-{t},Fine,-1\n";
        var r = await Import(staff, csv);
        Assert.Equal((false, 1, 4), (r.Applied, r.Created, r.Failed));
        Assert.Equal(["created", "failed", "failed", "failed", "failed"], r.Results.Select(x => x.Action));
        Assert.Contains("not a number", r.Results[1].Errors.Single());
        Assert.Contains("needs a name", r.Results[2].Errors.Single());
        Assert.Contains($"already on row 2", r.Results[3].Errors.Single());
        Assert.Equal([2, 3, 4, 5, 6], r.Results.Select(x => x.Row)); // spreadsheet rows: the header is row 1
        Assert.Null(await Find(staff, good));

        var some = await Import(staff, csv, "?skipInvalid=true");
        Assert.Equal((true, 1, 4), (some.Applied, some.Created, some.Failed));
        Assert.NotNull(await Find(staff, good));
        Assert.Null(await Find(staff, bad));
    }

    [Fact]
    public async Task A_file_saved_by_excel_imports_with_semicolons_a_byte_order_mark_and_windows_line_breaks()
    {
        var staff = await factory.CreateAuthenticatedApiClientAsync();
        var sku = "XLS-" + Tag();
        var r = await Import(staff, "\uFEFFsku;name;price;description\r\n" + $"{sku};\"Name; with a semicolon\";4.5;\"Line one\r\nline two\"\r\n\r\n");
        Assert.Equal((1, 0), (r.Created, r.Failed));
        var p = (await Find(staff, sku))!;
        Assert.Equal(("Name; with a semicolon", 4.5m, "Line one\r\nline two"), (p.Name, p.Price, p.Description));
    }

    [Fact]
    public async Task A_file_that_cannot_be_used_at_all_is_refused_with_a_reason()
    {
        var staff = await factory.CreateAuthenticatedApiClientAsync();
        foreach (var (csv, reason) in new[]
        {
            ("", "empty"),
            ("sku,nmae,price\nA,B,1\n", "Unknown column"),
            ("name,price\nA,1\n", "sku column"),
            ("sku,sku,name\nA,A,B\n", "twice"),
            ("sku,name\n\"A,B\n", "readable"),
        })
        {
            var r = await Post(staff, csv);
            Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);
            Assert.Contains(reason, await r.Content.ReadAsStringAsync(), StringComparison.OrdinalIgnoreCase);
        }

        var reader = await factory.CreateReadOnlyUserAsync();
        var readOnly = await factory.CreateAuthenticatedApiClientAsync(reader.Email, reader.Password);
        Assert.Equal(HttpStatusCode.Forbidden, (await Post(readOnly, "sku,name,price\nA,B,1\n")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await readOnly.GetAsync("/api/v1/products/export?search=nothing-matches")).StatusCode); // reading is allowed
    }

    [Fact]
    public async Task An_export_read_back_in_changes_nothing_and_awkward_text_survives_the_trip()
    {
        var staff = await factory.CreateAuthenticatedApiClientAsync();
        var t = Tag(); var sku = "RT-" + t;
        var awkward = $"Tee, \"special\" {t}\nsecond line";
        var csv = "sku,name,price,description,categories,barcodes,stock,published,weight_grams,option1_name,option1_value,parent_sku\n" +
                  $"{sku},\"{awkward.Replace("\"", "\"\"")}\",20,'=1+1,Round trip {t} > Deep,RTB{t},12,true,300,,,\n" +
                  $"{sku}-L,,22,,,,3,,,Size,L,{sku}\n" +
                  $"{sku}-S,,21,,,,2,,,Size,S,{sku}\n";
        var first = await Import(staff, csv);
        Assert.Equal((3, 0), (first.Created, first.Failed));
        var p = (await Find(staff, sku))!;
        Assert.Equal((awkward, "=1+1"), (p.Name, p.Description)); // the apostrophe only protected the cell in the file

        var exported = await Export(staff, sku);
        Assert.StartsWith("id,sku,parent_id,parent_sku,name,description,price", exported);
        Assert.Contains("'=1+1", exported); // a spreadsheet would run it as a formula otherwise
        Assert.Contains("published,tax_class,weight_grams", exported.Split("\r\n")[0]);
        Assert.Equal(3, exported.Split("\r\n", StringSplitOptions.RemoveEmptyEntries).Length - 1); // a header and three rows: the line break inside the name is not a row break

        var back = await Import(staff, exported);
        Assert.Equal((0, 3, 0), (back.Created, back.Updated, back.Failed));
        Assert.Equal(exported, await Export(staff, sku)); // nothing drifted, including the order and the variants
        Assert.Equal(12m, await Stock(staff, p.Id));
        Assert.Equal(2, (await Read<ProductVariantsDto>(await staff.GetAsync($"/api/v1/products/{p.Id}/variants"))).Variants.Count);
    }
}
