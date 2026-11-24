using System.Net;
using Microsoft.AspNetCore.Mvc.Testing;
using System.Text.RegularExpressions;

namespace PiranhaCommerce.IntegrationTests;

/// <summary>The licence asks for "Built on PiranhaCommerce" on every page; the admin and the sample shop show it, and a Commercial Licence holder can switch it off.</summary>
public class AttributionTests(PiranhaCommerceFactory factory) : IClassFixture<PiranhaCommerceFactory>
{
    private const string Notice = ">Built on PiranhaCommerce</a>";
    private const string Link = "href=\"https://piranhacommerce.com\"";

    [Fact]
    public async Task The_shop_the_sign_in_page_and_the_admin_show_the_notice_with_its_link()
    {
        var shop = await (await factory.CreateClient().GetAsync("/")).Content.ReadAsStringAsync();
        Assert.Contains(Notice, shop);
        Assert.Contains(Link, shop);
        Assert.Matches("<a[^>]*href=\"https://piranhacommerce.com\"[^>]*target=\"_blank\"[^>]*rel=\"noopener\"", shop); // opens in a new tab, safely

        var login = await (await factory.CreateClient().GetAsync("/admin/login")).Content.ReadAsStringAsync();
        Assert.Contains(Notice, login);
        Assert.Contains(Link, login);

        var browser = factory.CreateClient(new() { AllowAutoRedirect = false, HandleCookies = true });
        var form = await (await browser.GetAsync("/admin/login")).Content.ReadAsStringAsync(); // same client, so the antiforgery cookie matches
        var token = Regex.Match(form, "<input[^>]*name=\"__RequestVerificationToken\"[^>]*value=\"([^\"]+)\"").Groups[1].Value;
        await browser.PostAsync("/admin/login", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["__RequestVerificationToken"] = token, ["_handler"] = "login",
            ["Input.Email"] = PiranhaCommerceFactory.AdminEmail, ["Input.Password"] = PiranhaCommerceFactory.AdminPassword,
        }));
        var dashboard = await browser.GetAsync("/admin");
        Assert.Equal(HttpStatusCode.OK, dashboard.StatusCode);
        var html = await dashboard.Content.ReadAsStringAsync();
        Assert.Contains(Notice, html);
        Assert.Contains(Link, html);
    }

    [Fact]
    public async Task A_host_with_a_commercial_licence_can_turn_it_off()
    {
        using var host = factory.WithWebHostBuilder(b => b.UseSetting("PiranhaCommerce:Branding:ShowAttribution", "false"));
        Assert.DoesNotContain("Built on PiranhaCommerce", await (await host.CreateClient().GetAsync("/")).Content.ReadAsStringAsync());
        Assert.DoesNotContain("Built on PiranhaCommerce", await (await host.CreateClient().GetAsync("/admin/login")).Content.ReadAsStringAsync());
    }

    [Fact]
    public void The_notice_text_and_html_match_what_the_licence_says()
    {
        Assert.Equal("Built on PiranhaCommerce", PiranhaCommerce.Core.Identity.PiranhaCommerceAttribution.Text);
        Assert.Equal("https://piranhacommerce.com", PiranhaCommerce.Core.Identity.PiranhaCommerceAttribution.Url);
        Assert.Contains(Notice, PiranhaCommerce.Core.Identity.PiranhaCommerceAttribution.Html);
        Assert.Contains("target=\"_blank\" rel=\"noopener\"", PiranhaCommerce.Core.Identity.PiranhaCommerceAttribution.Html);
        var licence = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "LICENSE"));
        Assert.Contains("Built on PiranhaCommerce", licence);
        Assert.Contains("https://piranhacommerce.com", licence);
    }
}
