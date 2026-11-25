using System.Net;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using PiranhaCommerce.Site.Docs;

namespace PiranhaCommerce.Site.Tests;

/// <summary>The promotional site and the documentation: every page renders, every internal link and heading anchor resolves, and the docs host serves the docs at its root.</summary>
public class SiteTests(WebApplicationFactory<Program> factory) : IClassFixture<WebApplicationFactory<Program>>
{
    private static HttpClient Client(WebApplicationFactory<Program> f, string? host = null)
    {
        var c = f.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        if (host is not null) c.DefaultRequestHeaders.Host = host;
        return c;
    }

    private DocsLibrary Library => factory.Services.GetRequiredService<DocsLibrary>();

    [Fact]
    public async Task The_secondary_domain_redirects_to_the_main_one_keeping_path_and_query()
    {
        using var configured = factory.WithWebHostBuilder(b =>
        {
            b.UseSetting("Hosting:RedirectFrom", "piranhacommerce.co.uk");
            b.UseSetting("Hosting:RedirectTo", "piranhacommerce.com");
        });
        var client = Client(configured, "demo.staging.piranhacommerce.co.uk");

        var response = await client.GetAsync("/docs/x?a=1");
        Assert.Equal(HttpStatusCode.MovedPermanently, response.StatusCode);
        Assert.Equal("https://demo.staging.piranhacommerce.com/docs/x?a=1", response.Headers.Location!.ToString());

        var main = await Client(configured, "piranhacommerce.com").GetAsync("/");
        Assert.NotEqual(HttpStatusCode.MovedPermanently, main.StatusCode);
    }

    [Fact]
    public void The_docs_load_with_no_broken_links_anchors_or_missing_front_matter()
    {
        Assert.NotEmpty(Library.Pages);
        Assert.True(Library.Problems.Count == 0, string.Join(Environment.NewLine, Library.Problems));
    }

    [Fact]
    public async Task Every_docs_page_renders_with_its_title_and_the_navigation()
    {
        var client = Client(factory);
        foreach (var page in Library.Pages)
        {
            var response = await client.GetAsync("/docs/" + page.Slug);
            var html = await response.Content.ReadAsStringAsync();
            Assert.True(response.StatusCode == HttpStatusCode.OK, $"{page.Slug}: {response.StatusCode}");
            Assert.Contains($"<h1>{WebUtility.HtmlEncode(page.Title)}</h1>", html);
            Assert.Contains("aria-label=\"Documentation\"", html);
            Assert.DoesNotContain(DocsLibrary.LinkToken, html); // every internal link was given a real address
        }
    }

    [Fact]
    public async Task Internal_links_in_a_rendered_page_lead_to_pages_that_exist()
    {
        var client = Client(factory);
        var checkedLinks = 0;
        foreach (var page in Library.Pages)
        {
            var html = await (await client.GetAsync("/docs/" + page.Slug)).Content.ReadAsStringAsync();
            foreach (Match m in Regex.Matches(html, "href=\"(/docs[^\"#]*)"))
            {
                var path = WebUtility.HtmlDecode(m.Groups[1].Value);
                if (path.StartsWith("/docs/search")) continue;
                var response = await client.GetAsync(path);
                Assert.True(response.StatusCode == HttpStatusCode.OK, $"{page.Slug} links to {path}: {response.StatusCode}");
                checkedLinks++;
            }
        }
        Assert.True(checkedLinks > 20);
    }

    [Fact]
    public async Task The_docs_host_serves_the_docs_at_its_root_with_links_that_need_no_prefix()
    {
        var client = Client(factory, "docs.example.test");
        var home = await client.GetAsync("/");
        var html = await home.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.OK, home.StatusCode);
        Assert.Contains("<h1>PiranhaCommerce documentation</h1>", html);
        Assert.Contains("href=\"/getting-started/installation\"", html);
        Assert.DoesNotContain("href=\"/docs/", html);

        var page = await client.GetAsync("/guides/pricing");
        Assert.Equal(HttpStatusCode.OK, page.StatusCode);
        Assert.Contains("<h1>Prices, tax and discounts</h1>", await page.Content.ReadAsStringAsync());

        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/search?q=voucher")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/site.css")).StatusCode); // files are not rewritten
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/nothing-here")).StatusCode);
    }

    [Fact]
    public async Task The_promotional_home_page_renders_and_points_at_the_docs()
    {
        var client = Client(factory, "www.example.test");
        var response = await client.GetAsync("/");
        var html = await response.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("Commerce for .NET", html);
        Assert.Contains("href=\"/docs/getting-started/installation\"", html);
    }

    [Fact]
    public async Task Search_finds_pages_by_what_they_say_and_says_so_when_nothing_matches()
    {
        var client = Client(factory);
        var found = await (await client.GetAsync("/docs/search?q=gift+voucher")).Content.ReadAsStringAsync();
        Assert.Contains("Payments", found);
        Assert.DoesNotContain("no-results", found);
        var none = await (await client.GetAsync("/docs/search?q=zzzxqzzz")).Content.ReadAsStringAsync();
        Assert.Contains("no-results", none);
    }

    [Fact]
    public void Only_the_public_docs_are_published()
    {
        Assert.Null(Library.Find("roadmap"));
        Assert.Null(Library.Find("vocabulary"));
        Assert.DoesNotContain(Library.Pages, p => p.PlainText.Contains("docs/roadmap.md"));
    }

    [Fact]
    public void The_feature_guides_the_readme_used_to_hold_are_all_here()
    {
        // The README's feature text was moved into these pages; losing one would lose documentation.
        foreach (var slug in new[] { "guides/catalogue", "guides/shoppers", "guides/baskets-orders", "guides/pricing", "guides/shipping", "guides/payments", "guides/email", "guides/engagement", "guides/operations", "advanced/plugins", "advanced/migrations" })
            Assert.NotNull(Library.Find(slug));
        var text = string.Join(" ", Library.Pages.Select(p => p.PlainText));
        foreach (var topic in new[] { "Gift vouchers", "Webhooks", "Audit log", "Discounts", "Stock transfers", "Product reviews", "Wishlists", "Shipping zones and carriers", "Stripe" })
            Assert.Contains(topic, text);
    }

    [Theory]
    [InlineData(1, 400)]
    [InlineData(4, 1600)]
    [InlineData(5, 1960)]    // the fifth domain is 10% off
    [InlineData(9, 3400)]
    [InlineData(10, 3720)]   // the tenth is 20% off
    [InlineData(12, 4360)]
    [InlineData(20, 6920)]
    public void Licences_are_priced_by_band(int domains, int total) => Assert.Equal(total, LicencePricing.Total(domains));

    [Fact]
    public void Buying_one_more_domain_never_costs_more_than_the_one_before()
    {
        var previous = 0m;
        decimal lastStep = decimal.MaxValue;
        for (var n = 1; n <= 60; n++)
        {
            var total = LicencePricing.Total(n);
            var step = total - previous;
            Assert.True(step <= lastStep, $"Domain {n} costs {step}, more than the one before it ({lastStep}).");
            Assert.True(step > 0);
            (previous, lastStep) = (total, step);
        }
        Assert.Throws<ArgumentOutOfRangeException>(() => LicencePricing.Total(0));
    }

    [Fact]
    public async Task The_pricing_page_shows_the_price_the_bands_and_a_calculator_that_works_without_scripts()
    {
        var html = await (await Client(factory).GetAsync("/pricing")).Content.ReadAsStringAsync();
        Assert.Contains("£400", html);
        Assert.Contains("£360", html);
        Assert.Contains("£320", html);
        Assert.Contains("every subdomain", html.Replace("all of its subdomains", "every subdomain"));
        Assert.Contains("<form method=\"get\"", html);

        var calc = await (await Client(factory).GetAsync("/pricing?domains=12")).Content.ReadAsStringAsync();
        Assert.Contains("12 domains: £4,360", calc);
        Assert.Contains("1 domain: £400", await (await Client(factory).GetAsync("/pricing?domains=1")).Content.ReadAsStringAsync());
        Assert.Contains("from 1 to 1000", await (await Client(factory).GetAsync("/pricing?domains=0")).Content.ReadAsStringAsync());
        Assert.Contains("from 1 to 1000", await (await Client(factory).GetAsync("/pricing?domains=5000")).Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.OK, (await Client(factory).GetAsync("/pricing?domains=abc")).StatusCode); // junk in the address is not an error page
    }

    [Fact]
    public async Task The_email_link_appears_only_when_an_address_is_configured_and_the_menu_links_to_pricing()
    {
        Assert.Contains("mailto:hello@purplepiranha.co.uk", await (await Client(factory).GetAsync("/pricing")).Content.ReadAsStringAsync()); // the shipped default
        using var none = factory.WithWebHostBuilder(b => b.UseSetting("Site:LicenceEmail", ""));
        Assert.DoesNotContain("mailto:", await (await Client(none).GetAsync("/pricing")).Content.ReadAsStringAsync());
        using var configured = factory.WithWebHostBuilder(b => b.UseSetting("Site:LicenceEmail", "licences@example.com"));
        var html = await (await Client(configured).GetAsync("/pricing")).Content.ReadAsStringAsync();
        Assert.Contains("mailto:licences@example.com", html);
        Assert.Contains("href=\"/pricing\"", html); // in the main menu
    }

    [Fact]
    public void The_docs_and_the_commercial_licence_state_the_same_prices_as_the_pricing_page()
    {
        var root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
        var docs = File.ReadAllText(Path.Combine(root, "docs", "public", "getting-started", "licensing.md"));
        Assert.Contains("£400 per domain", docs);
        Assert.Contains("£360 each (10% off)", docs);
        Assert.Contains("£320 each (20% off)", docs);
        Assert.Contains($"12 domains cost £{LicencePricing.Total(12):N0}", docs);
        var terms = File.ReadAllText(Path.Combine(root, "COMMERCIAL-LICENSE.md"));
        Assert.Contains("domains 1 to 4 at the standard price, domains 5 to 9 at 10% off", terms);
        Assert.Contains("10th domain and any after it at 20% off", terms);
    }
}
