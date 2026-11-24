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
}
