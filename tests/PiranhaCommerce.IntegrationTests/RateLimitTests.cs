using System.Net;
using System.Net.Http.Json;
using PiranhaCommerce.Api.Auth;

namespace PiranhaCommerce.IntegrationTests;

/// <summary>A host that allows only 5 sign-in attempts a minute per client.</summary>
public sealed class StrictRateLimitFactory : PiranhaCommerceFactory
{
    protected override int SignInAttempts => 5;
}

public class RateLimitTests(StrictRateLimitFactory factory) : IClassFixture<StrictRateLimitFactory>
{
    [Fact]
    public async Task Token_endpoint_refuses_a_client_that_keeps_guessing_even_at_different_accounts()
    {
        // The host sees the test client as one address, so this is one client trying a different account each time.
        var client = factory.CreateClient();
        for (var i = 0; i < 5; i++)
        {
            var guess = await client.PostAsJsonAsync("/api/v1/auth/token", new TokenRequest($"nobody{i}@piranhacommerce.local", "guess"));
            Assert.Equal(HttpStatusCode.Unauthorized, guess.StatusCode);
        }

        var refused = await client.PostAsJsonAsync("/api/v1/auth/token", new TokenRequest("nobody-else@piranhacommerce.local", "guess"));
        Assert.Equal(HttpStatusCode.TooManyRequests, refused.StatusCode);
        Assert.True(refused.Headers.Contains("Retry-After"));

        // Once limited, even the right password waits: the limit applies to the client, not the account.
        var right = await client.PostAsJsonAsync("/api/v1/auth/token", new TokenRequest(PiranhaCommerceFactory.AdminEmail, PiranhaCommerceFactory.AdminPassword));
        Assert.Equal(HttpStatusCode.TooManyRequests, right.StatusCode);
    }

}

// Its own class, so its own host and its own limit bucket (the test client is one address for every request).
public class AdminRateLimitTests(StrictRateLimitFactory factory) : IClassFixture<StrictRateLimitFactory>
{
    [Fact]
    public async Task Admin_login_posts_are_limited_too_but_reading_pages_is_not()
    {
        var browser = factory.CreateClient(new() { AllowAutoRedirect = false, HandleCookies = true });
        var form = new Dictionary<string, string> { ["_handler"] = "login", ["Input.Email"] = "x@piranhacommerce.local", ["Input.Password"] = "x" };

        HttpStatusCode last = default;
        for (var i = 0; i < 6; i++)
            last = (await browser.PostAsync("/admin/login", new FormUrlEncodedContent(form))).StatusCode;
        Assert.Equal(HttpStatusCode.TooManyRequests, last);

        Assert.Equal(HttpStatusCode.OK, (await browser.GetAsync("/admin/login")).StatusCode);
    }
}
