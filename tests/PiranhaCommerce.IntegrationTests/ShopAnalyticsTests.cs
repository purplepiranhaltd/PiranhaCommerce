using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using PiranhaCommerce.Web.Shop;

namespace PiranhaCommerce.IntegrationTests;

/// <summary>The analytics script sends a page's whole address away, so the shop must leave it off any page whose address is not safe to share.</summary>
public class ShopAnalyticsTests(PiranhaCommerceFactory factory) : IClassFixture<PiranhaCommerceFactory>
{
    private const string Tag = "<script defer data-domain=\"demo.example.com\" data-api=\"https://pa.example.com/api/event\" src=\"https://pa.example.com/js/script.js\"></script>";

    private WebApplicationFactory<Program> WithAnalytics() => factory.WithWebHostBuilder(h =>
    {
        h.UseSetting("Analytics:PlausibleDomain", "demo.example.com");
        h.UseSetting("Analytics:PlausibleHost", "https://pa.example.com");
    });

    [Fact]
    public async Task No_script_unless_a_domain_is_configured()
    {
        var html = await factory.CreateClient().GetStringAsync("/");
        Assert.DoesNotContain("data-domain", html);
    }

    [Theory]
    [InlineData("/", true)]
    [InlineData("/shop?tag=vegan&sort=price&page=2&utm_source=news", true)]
    [InlineData("/account/sign-in?returnTo=%2Fcheckout", true)]
    [InlineData("/account/reset?email=a%40example.com&token=secret", false)]
    [InlineData("/stock-alert/unsubscribe?token=secret", false)]
    [InlineData("/shop?something-new=1", false)]
    [InlineData("/basket?f=unreadable", false)]
    public async Task Only_pages_with_a_harmless_address_load_it(string path, bool loads)
    {
        using var shop = WithAnalytics();
        var html = await shop.CreateClient().GetStringAsync(path);
        Assert.Equal(loads, html.Contains(Tag));
        Assert.Contains("<meta name=\"referrer\" content=\"strict-origin\"", html); // so the next page is not told this one's address
    }

    [Fact]
    public async Task A_page_message_is_reported_but_one_carrying_form_values_or_a_sign_in_step_is_not()
    {
        using var shop = WithAnalytics();
        using var scope = shop.Services.CreateScope();
        var session = ActivatorUtilities.CreateInstance<ShopSession>(scope.ServiceProvider);
        var client = shop.CreateClient();
        async Task<bool> Loads(Flash flash) => (await client.GetStringAsync("/basket?f=" + Uri.EscapeDataString(session.Protect(flash)))).Contains(Tag);

        Assert.True(await Loads(new Flash(null, "Added to your basket.")));
        Assert.False(await Loads(new Flash("Check the code.", null, new() { ["challenge"] = "secret" })));
    }
}
