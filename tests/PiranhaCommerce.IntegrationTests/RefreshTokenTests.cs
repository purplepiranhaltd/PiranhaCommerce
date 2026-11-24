using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using PiranhaCommerce.Api.Auth;
using PiranhaCommerce.Customers;
using PiranhaCommerce.Data;

namespace PiranhaCommerce.IntegrationTests;

/// <summary>Refresh tokens: single use, rotated, family revoked when one is replayed, and ended by anything that ends sessions.</summary>
public class RefreshTokenTests(PiranhaCommerceFactory factory) : IClassFixture<PiranhaCommerceFactory>
{
    private const string Password = "a-long-enough-password";
    private static string Tag() => Guid.NewGuid().ToString("N")[..8];
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private async Task<(ProfileDto Profile, TokenResponse Token)> Register()
    {
        var r = await factory.CreateClient().PostAsJsonAsync("/api/v1/storefront/auth/register", new RegisterInput { Email = $"rt-{Tag()}@example.com", Password = Password });
        Assert.Equal(HttpStatusCode.Created, r.StatusCode);
        var reg = (await r.Content.ReadFromJsonAsync<RegisteredDto>(Json))!;
        return (reg.Profile, reg.Token);
    }

    private async Task<HttpResponseMessage> Refresh(string? token) => await factory.CreateClient().PostAsJsonAsync("/api/v1/storefront/auth/refresh", new RefreshTokenInput { RefreshToken = token ?? "" });
    private static async Task<TokenResponse> Tok(HttpResponseMessage r) => (await r.Content.ReadFromJsonAsync<TokenResponse>(Json))!;

    private async Task<HttpStatusCode> Me(string accessToken)
    {
        var c = factory.CreateClient();
        c.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        return (await c.GetAsync("/api/v1/storefront/me")).StatusCode;
    }

    [Fact]
    public async Task Signing_in_gives_a_refresh_token_and_each_use_swaps_it_for_a_new_pair()
    {
        var (profile, registered) = await Register();
        Assert.False(string.IsNullOrEmpty(registered.RefreshToken));

        var signedIn = await Tok(await factory.CreateClient().PostAsJsonAsync("/api/v1/storefront/auth/token", new SignInInput(profile.Email, Password)));
        Assert.False(string.IsNullOrEmpty(signedIn.RefreshToken));
        Assert.NotEqual(registered.RefreshToken, signedIn.RefreshToken); // each sign-in is its own device

        var first = await Tok(await Refresh(signedIn.RefreshToken));
        Assert.NotEqual(signedIn.RefreshToken, first.RefreshToken);
        Assert.Equal(HttpStatusCode.OK, await Me(first.AccessToken)); // the new access token works
        var second = await Tok(await Refresh(first.RefreshToken));
        Assert.Equal(HttpStatusCode.OK, await Me(second.AccessToken));
    }

    [Fact]
    public async Task Using_a_token_twice_ends_that_sign_in_but_not_the_shoppers_other_devices()
    {
        var (profile, laptop) = await Register();
        var phone = await Tok(await factory.CreateClient().PostAsJsonAsync("/api/v1/storefront/auth/token", new SignInInput(profile.Email, Password)));

        var next = await Tok(await Refresh(laptop.RefreshToken));
        // The old token comes back (it was copied): it is refused, and the newer one it was swapped for dies with it.
        Assert.Equal(HttpStatusCode.Unauthorized, (await Refresh(laptop.RefreshToken)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await Refresh(next.RefreshToken)).StatusCode);

        Assert.Equal(HttpStatusCode.OK, (await Refresh(phone.RefreshToken)).StatusCode);
    }

    [Fact]
    public async Task Two_refreshes_at_once_with_the_same_token_let_nobody_keep_a_working_one()
    {
        var (_, token) = await Register();
        var results = await Task.WhenAll(Refresh(token.RefreshToken), Refresh(token.RefreshToken));
        Assert.Contains(results, r => r.StatusCode == HttpStatusCode.Unauthorized);
        var winners = results.Where(r => r.StatusCode == HttpStatusCode.OK).ToList();
        Assert.True(winners.Count <= 1);
        // Whoever won, a replay-detected family is revoked, so its newest token no longer works either.
        foreach (var w in winners) Assert.Equal(HttpStatusCode.Unauthorized, (await Refresh((await Tok(w)).RefreshToken)).StatusCode);
    }

    [Fact]
    public async Task Anything_that_is_not_a_live_token_is_the_same_401_and_logging_out_revokes_the_family()
    {
        var (_, token) = await Register();
        Assert.Equal(HttpStatusCode.Unauthorized, (await Refresh(null)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await Refresh("not-a-token")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await Refresh(new string('x', 500))).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await Refresh(token.AccessToken)).StatusCode); // an access token is not a refresh token

        var anon = factory.CreateClient();
        Assert.Equal(HttpStatusCode.NoContent, (await anon.PostAsJsonAsync("/api/v1/storefront/auth/logout", new RefreshTokenInput { RefreshToken = "nonsense" })).StatusCode); // says nothing either way
        Assert.Equal(HttpStatusCode.NoContent, (await anon.PostAsJsonAsync("/api/v1/storefront/auth/logout", new RefreshTokenInput { RefreshToken = token.RefreshToken! })).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await Refresh(token.RefreshToken)).StatusCode);
    }

    [Fact]
    public async Task Ending_sessions_kills_refresh_tokens_signing_out_everywhere_changing_the_password_and_switching_the_customer_off()
    {
        var staff = await factory.CreateAuthenticatedApiClientAsync();

        var (a, aToken) = await Register();
        Assert.Equal(HttpStatusCode.NoContent, (await staff.PostAsync($"/api/v1/customers/{a.Id}/sign-in/sign-out", null)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await Refresh(aToken.RefreshToken)).StatusCode);

        var (b, bToken) = await Register();
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", bToken.AccessToken);
        var changed = await client.PostAsJsonAsync("/api/v1/storefront/me/password", new ChangePasswordInput { CurrentPassword = Password, NewPassword = "another-long-password" });
        Assert.Equal(HttpStatusCode.OK, changed.StatusCode);
        var fresh = await Tok(changed);
        Assert.Equal(HttpStatusCode.Unauthorized, (await Refresh(bToken.RefreshToken)).StatusCode); // the old device is out
        Assert.Equal(HttpStatusCode.OK, (await Refresh(fresh.RefreshToken)).StatusCode);          // the device that changed it carries on

        var (c, cToken) = await Register();
        var customer = await (await staff.GetAsync($"/api/v1/customers/{c.Id}")).Content.ReadFromJsonAsync<CustomerDto>(Json);
        var off = await staff.PutAsJsonAsync($"/api/v1/customers/{c.Id}", new CustomerInput { Email = customer!.Email, Active = false });
        Assert.Equal(HttpStatusCode.OK, off.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await Refresh(cToken.RefreshToken)).StatusCode);
    }

    [Fact]
    public async Task Only_a_hash_is_stored_an_expired_token_is_refused_and_the_family_has_a_lifetime_cap()
    {
        var (profile, token) = await Register();
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PiranhaCommerceDbContext>();
        var row = await db.Set<CustomerRefreshToken>().AsNoTracking().SingleAsync(t => t.CustomerId == profile.Id);
        Assert.NotEqual(token.RefreshToken, row.TokenHash);
        Assert.Equal(64, row.TokenHash.Length);
        Assert.DoesNotContain(token.RefreshToken!, row.TokenHash);
        Assert.True(row.ExpiresUtc > DateTimeOffset.UtcNow.AddDays(29));

        await db.Set<CustomerRefreshToken>().Where(t => t.Id == row.Id).ExecuteUpdateAsync(s => s.SetProperty(t => t.ExpiresUtc, DateTimeOffset.UtcNow.AddMinutes(-1)));
        Assert.Equal(HttpStatusCode.Unauthorized, (await Refresh(token.RefreshToken)).StatusCode);

        // A family that began long ago cannot be extended past the cap, however recently it was used.
        var (p2, t2) = await Register();
        await db.Set<CustomerRefreshToken>().Where(t => t.CustomerId == p2.Id).ExecuteUpdateAsync(s => s.SetProperty(t => t.FamilyStartedUtc, DateTimeOffset.UtcNow.AddDays(-179.5)));
        Assert.Equal(HttpStatusCode.OK, (await Refresh(t2.RefreshToken)).StatusCode);
        var capped = await db.Set<CustomerRefreshToken>().AsNoTracking().Where(t => t.CustomerId == p2.Id && t.UsedUtc == null).SingleAsync();
        Assert.True(capped.ExpiresUtc < DateTimeOffset.UtcNow.AddDays(1));
    }

    private HttpClient As(string accessToken)
    {
        var c = factory.CreateClient();
        c.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        return c;
    }

    private static async Task<List<ShopperSessionDto>> Sessions(HttpClient c) =>
        (await (await c.GetAsync("/api/v1/storefront/me/sessions")).Content.ReadFromJsonAsync<List<ShopperSessionDto>>(Json))!;

    [Fact]
    public async Task A_shopper_can_list_and_end_their_own_devices()
    {
        var (profile, laptop) = await Register();
        var phone = await Tok(await factory.CreateClient().PostAsJsonAsync("/api/v1/storefront/auth/token", new SignInInput(profile.Email, Password)));
        var me = As(laptop.AccessToken);
        var sessions = await Sessions(me);
        Assert.Equal(2, sessions.Count);

        // Refreshing keeps it one device, not two.
        await Refresh(laptop.RefreshToken);
        var after = await Sessions(me);
        Assert.Equal(2, after.Count);
        Assert.Equivalent(sessions.Select(s => s.Id).Order(), after.Select(s => s.Id).Order());

        // Someone else cannot end it; the owner can, once.
        var (_, stranger) = await Register();
        var target = after[0].Id;
        Assert.Equal(HttpStatusCode.NotFound, (await As(stranger.AccessToken).DeleteAsync($"/api/v1/storefront/me/sessions/{target}")).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await me.DeleteAsync($"/api/v1/storefront/me/sessions/{target}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await me.DeleteAsync($"/api/v1/storefront/me/sessions/{target}")).StatusCode);
        Assert.Single(await Sessions(me));
        Assert.DoesNotContain(await Sessions(me), s => s.Id == target);
    }

    [Fact]
    public async Task Signing_out_everywhere_ends_every_other_device_and_keeps_the_one_that_asked()
    {
        var (profile, laptop) = await Register();
        var phone = await Tok(await factory.CreateClient().PostAsJsonAsync("/api/v1/storefront/auth/token", new SignInInput(profile.Email, Password)));

        var r = await As(laptop.AccessToken).PostAsync("/api/v1/storefront/me/sign-out-everywhere", null);
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        var fresh = await Tok(r);

        Assert.Equal(HttpStatusCode.Unauthorized, await Me(phone.AccessToken));
        Assert.Equal(HttpStatusCode.Unauthorized, await Me(laptop.AccessToken));
        Assert.Equal(HttpStatusCode.Unauthorized, (await Refresh(phone.RefreshToken)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, await Me(fresh.AccessToken));
        Assert.Single(await Sessions(As(fresh.AccessToken)));
    }

    private async Task<TokenResponse> SignInWith(string email, string? userAgent)
    {
        var c = factory.CreateClient();
        if (userAgent is not null) c.DefaultRequestHeaders.UserAgent.ParseAdd(userAgent);
        return await Tok(await c.PostAsJsonAsync("/api/v1/storefront/auth/token", new SignInInput(email, Password)));
    }

    [Fact]
    public async Task Devices_are_named_from_the_user_agent_and_the_current_one_is_marked()
    {
        var (profile, _) = await Register();
        var chrome = await SignInWith(profile.Email, "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/130.0.0.0 Safari/537.36");
        var phone = await SignInWith(profile.Email, "Mozilla/5.0 (iPhone; CPU iPhone OS 18_0 like Mac OS X) AppleWebKit/605.1.15 (KHTML, like Gecko) Version/18.0 Mobile/15E148 Safari/604.1");
        var app = await SignInWith(profile.Email, "MyShopApp/2.1");

        var seenByChrome = await Sessions(As(chrome.AccessToken));
        Assert.Contains(seenByChrome, s => s.Device == "Chrome on Windows" && s.Current);
        Assert.Contains(seenByChrome, s => s.Device == "Safari on iPhone" && !s.Current);
        Assert.Contains(seenByChrome, s => s.Device == "MyShopApp/2.1" && !s.Current);
        Assert.Single(seenByChrome, s => s.Current);

        // Refreshing keeps the name and the mark: the new access token carries the same session id.
        var refreshed = await Tok(await Refresh(phone.RefreshToken));
        var seenByPhone = await Sessions(As(refreshed.AccessToken));
        Assert.Single(seenByPhone, s => s.Current);
        Assert.Equal("Safari on iPhone", seenByPhone.Single(s => s.Current).Device);

        // Ending the device you are on, from that device, is allowed and signs it out of refreshing.
        var mine = seenByPhone.Single(s => s.Current).Id;
        Assert.Equal(HttpStatusCode.NoContent, (await As(refreshed.AccessToken).DeleteAsync($"/api/v1/storefront/me/sessions/{mine}")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await Refresh(refreshed.RefreshToken)).StatusCode);
        Assert.NotNull(app.RefreshToken);
    }

    [Fact]
    public async Task No_user_agent_is_an_unknown_device_and_a_huge_or_dirty_one_is_stored_cleaned_and_short()
    {
        var (profile, _) = await Register();
        var none = await SignInWith(profile.Email, null);
        Assert.Contains(await Sessions(As(none.AccessToken)), s => s.Current && s.Device == "Unknown device");

        var c = factory.CreateClient();
        c.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", "Edg-test/" + new string('x', 400));
        var big = await Tok(await c.PostAsJsonAsync("/api/v1/storefront/auth/token", new SignInInput(profile.Email, Password)));
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PiranhaCommerceDbContext>();
        var stored = await db.Set<CustomerRefreshToken>().AsNoTracking().Where(t => t.CustomerId == profile.Id && t.UserAgent != null).Select(t => t.UserAgent!).ToListAsync();
        Assert.All(stored, s => Assert.True(s.Length <= 200));
        Assert.NotNull(big.RefreshToken);
    }
}
