using Microsoft.AspNetCore.Mvc.Testing;

namespace PiranhaCommerce.IntegrationTests;

/// <summary>What the shop asks of search engines: nothing by default except to leave the admin out, and to list nothing when Hosting:NoIndex is set.</summary>
public class SearchEngineTests(PiranhaCommerceFactory factory) : IClassFixture<PiranhaCommerceFactory>
{
    private static async Task<bool> Hidden(WebApplicationFactory<Program> shop, string path)
    {
        var client = shop.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        return (await client.GetAsync(path)).Headers.TryGetValues("X-Robots-Tag", out var values) && values.Single() == "noindex, nofollow";
    }

    [Theory]
    [InlineData("/admin")]
    [InlineData("/admin/login")]
    public async Task The_admin_is_never_listed(string path) => Assert.True(await Hidden(factory, path));

    [Theory]
    [InlineData("/")]
    [InlineData("/shop")]
    [InlineData("/shop.css")]
    [InlineData("/_framework/blazor.web.js")]
    [InlineData("/no-such-page")]
    public async Task Everything_else_is_hidden_only_when_set_to_be(string path)
    {
        Assert.False(await Hidden(factory, path));

        using var hidden = factory.WithWebHostBuilder(h => h.UseSetting("Hosting:NoIndex", "true"));
        Assert.True(await Hidden(hidden, path));
    }
}
