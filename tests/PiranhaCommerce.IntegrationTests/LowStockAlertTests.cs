using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.DependencyInjection;
using PiranhaCommerce.Catalog;
using PiranhaCommerce.Core.Email;
using PiranhaCommerce.Ecommerce;

namespace PiranhaCommerce.IntegrationTests;

public sealed class LowStockFactory : PiranhaFactory
{
    protected override string? AlertEmail => "stock@example.com, Second@Example.com ,stock@example.com";
    protected override IReadOnlyDictionary<string, string?> ExtraSettings => new Dictionary<string, string?> { ["PiranhaCommerce:Ecommerce:LowStockAlertLevel"] = "5", ["PiranhaCommerce:Ecommerce:LowStockAlertPollSeconds"] = "0" };
}

/// <summary>The shop's alert address is emailed one digest of the products on sale that have newly run low, once per dip.</summary>
[Collection(OrderSettings.Name)]
public class LowStockAlertTests(LowStockFactory factory) : IClassFixture<LowStockFactory>
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };
    private static string Tag() => Guid.NewGuid().ToString("N")[..8];

    private static async Task<ProductDto> Product(HttpClient staff, string name, int stock, bool published = true)
    {
        var p = (await (await staff.PostAsJsonAsync("/api/v1/products", new ProductInput { Name = name, Sku = "LS-" + Tag(), Price = 10m })).Content.ReadFromJsonAsync<ProductDto>(Json))!;
        await staff.PutAsJsonAsync($"/api/v1/products/{p.Id}/listing", new ProductListingInput { Published = published });
        if (stock != 0) await Adjust(staff, p.Id, stock);
        return p;
    }

    private static Task Adjust(HttpClient staff, Guid id, int delta) =>
        staff.PostAsJsonAsync($"/api/v1/products/{id}/stock/adjustments", new StockAdjustmentInput { Delta = delta, Reason = "Test" });

    private async Task<int> Run()
    {
        await using var scope = factory.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<ILowStockAlerter>().RunOnceAsync();
    }

    private List<EmailMessage> Digests(string address = "stock@example.com") => factory.Email.To(address).Where(m => m.TemplateKey == EcommerceEmailTemplates.LowStock).ToList();

    private static List<string> Names(EmailMessage m) => ((List<object?>)m.Variables["products"]!).Select(p => ((Dictionary<string, object?>)p!)["name"]!.ToString()!).ToList();

    [Fact]
    public async Task Newly_low_products_on_sale_are_sent_in_one_digest_once_per_dip_and_a_failed_send_is_tried_again()
    {
        var staff = await factory.CreateAuthenticatedApiClientAsync();
        var tag = Tag();
        await Run(); // whatever else the shared database holds is told about now, so what follows is only this test's products
        var before = Digests().Count;

        var a = await Product(staff, "Low A " + tag, 3);
        var b = await Product(staff, "Plenty B " + tag, 20);
        var c = await Product(staff, "Draft C " + tag, 1, published: false);
        var d = await Product(staff, "Low D " + tag, 0);   // nothing on hand at all: low too

        Assert.Equal(2, await Run());
        var first = Digests();
        Assert.Equal(before + 1, first.Count);
        var digest = first[^1];
        Assert.Equal(["Low A " + tag, "Low D " + tag], Names(digest).Order());                // not the well-stocked one, not the draft
        Assert.Equal(("5", 2), (digest.Variables["level"], digest.Variables["count"]));
        Assert.Equal(first.Count, Digests("second@example.com").Count);                       // every address, once, whatever its letter case

        // Told once: nothing changes, nothing is sent.
        Assert.Equal(0, await Run());
        Assert.Equal(before + 1, Digests().Count);

        // Restock A above the level, then let it run short again: that is a new alert, for A alone (D was never restocked).
        await Adjust(staff, a.Id, 20);
        Assert.Equal(0, await Run());
        await Adjust(staff, a.Id, -21);
        Assert.Equal(1, await Run());
        Assert.Equal(["Low A " + tag], Names(Digests()[^1]));
        Assert.Equal(before + 2, Digests().Count);

        // Putting a draft on sale while it is low tells the shop then.
        await staff.PutAsJsonAsync($"/api/v1/products/{c.Id}/listing", new ProductListingInput { Published = true });
        Assert.Equal(1, await Run());
        Assert.Contains("Draft C " + tag, Names(Digests()[^1]));

        // A failed send is not forgotten: it is tried again at the next check.
        var e = await Product(staff, "Low E " + tag, 2);
        factory.Email.Fail = true;
        try { Assert.Equal(0, await Run()); } finally { factory.Email.Fail = false; }
        Assert.Equal(before + 3, Digests().Count);
        Assert.Equal(1, await Run());
        Assert.Equal(["Low E " + tag], Names(Digests()[^1]));
        Assert.DoesNotContain(Digests().SelectMany(Names), n => n == "Plenty B " + tag);
    }
}
