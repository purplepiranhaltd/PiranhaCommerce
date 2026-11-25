using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;

namespace PiranhaCommerce.IntegrationTests;

/// <summary>A new project has no API signing key. In Development that must still work; anywhere else it must be found at startup.</summary>
public class SigningKeyTests(PiranhaCommerceFactory factory) : IClassFixture<PiranhaCommerceFactory>
{
    private const string Setting = "PiranhaCommerce:Jwt:SigningKey";

    [Fact]
    public async Task Development_without_a_key_makes_a_temporary_one_so_sign_in_and_API_calls_work()
    {
        var (email, password) = await factory.CreateReadOnlyUserAsync();
        using var keyless = factory.WithWebHostBuilder(b => b.UseSetting(Setting, ""));
        var client = keyless.CreateClient();

        var token = await client.PostAsJsonAsync("/api/v1/auth/token", new { email, password });
        Assert.Equal(HttpStatusCode.OK, token.StatusCode);
        var jwt = (await token.Content.ReadFromJsonAsync<Dictionary<string, object>>())!["accessToken"]!.ToString();

        // The same temporary key signs and checks, so the token it issued opens the API.
        client.DefaultRequestHeaders.Authorization = new("Bearer", jwt);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/v1/products")).StatusCode);
    }

    [Theory]
    [InlineData("")]
    [InlineData("too-short")]
    public void Outside_Development_a_missing_or_short_key_stops_the_app_at_startup_with_a_clear_message(string key)
    {
        using var production = factory.WithWebHostBuilder(b => b.UseEnvironment("Production").UseSetting(Setting, key));
        var error = Assert.ThrowsAny<Exception>(() => production.CreateClient());
        Assert.Contains("SigningKey must be set to a secret of 32 or more characters", error.ToString());
    }
}
