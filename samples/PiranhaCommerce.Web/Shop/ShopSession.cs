using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.DataProtection;

namespace PiranhaCommerce.Web.Shop;

/// <summary>A message and the form values to put back, carried to the next page as one tamper-proof, short-lived token in the address (<c>?f=</c>).</summary>
public sealed record Flash(string? Error, string? Info = null, Dictionary<string, string>? Values = null);

/// <summary>What a guest has told checkout so far: where the order goes. Kept in a protected cookie between the two checkout steps.</summary>
public sealed record CheckoutDetails(string Email, string? Recipient, string Line1, string? Line2, string City, string? Region, string? PostalCode, string CountryCode);

public static class ShopCountries
{
    public static readonly IReadOnlyList<(string Code, string Name)> All =
        [("GB", "United Kingdom"), ("IE", "Ireland"), ("FR", "France"), ("DE", "Germany"), ("US", "United States")];

    public static string NameOf(string code) => All.FirstOrDefault(c => c.Code.Equals(code, StringComparison.OrdinalIgnoreCase)).Name ?? code.ToUpperInvariant();
}

/// <summary>
/// The shopper's browser state for the sample shop: which basket is theirs, where they are sending an order, which order they just placed, and the
/// messages carried between pages. Knowing the basket id is the whole proof for a guest basket, so it lives in an HttpOnly cookie and is checked
/// against the database on every use (see <see cref="ShopFront"/>). Everything else is encrypted and signed with the data protection keys.
/// </summary>
public sealed class ShopSession(IHttpContextAccessor accessor, IDataProtectionProvider protection)
{
    private const string BasketCookie = ".Petalwick.Basket";
    private const string CheckoutCookie = ".Petalwick.Checkout";
    private const string OrderCookie = ".Petalwick.Order";

    private readonly IDataProtector _data = protection.CreateProtector("PiranhaCommerce.Sample.Shop.v1");
    private HttpContext Http => accessor.HttpContext ?? throw new InvalidOperationException("The shop needs an HTTP request.");

    private CookieOptions Options(TimeSpan? lifetime, string path = "/") => new()
    {
        HttpOnly = true, SameSite = SameSiteMode.Lax, Secure = Http.Request.IsHttps, Path = path, IsEssential = true,
        MaxAge = lifetime,
    };

    // Signing in. The shop's cookie is asked for by name; the admin's is a different one.
    public Task<AuthenticateResult> AuthenticateAsync() => Http.AuthenticateAsync(ShopAuth.Scheme);
    public Task SignInAsync(ClaimsPrincipal principal) => Http.SignInAsync(ShopAuth.Scheme, principal, new AuthenticationProperties { IsPersistent = true });
    public Task SignOutAsync() => Http.SignOutAsync(ShopAuth.Scheme);

    // The basket.
    public Guid? BasketId => Guid.TryParse(Http.Request.Cookies[BasketCookie], out var id) ? id : null;
    public void SetBasketId(Guid id) => Http.Response.Cookies.Append(BasketCookie, id.ToString(), Options(TimeSpan.FromDays(60)));
    public void ForgetBasket() => Http.Response.Cookies.Delete(BasketCookie, Options(null));

    // Where the order goes, between the two checkout steps.
    public CheckoutDetails? Details => Read<CheckoutDetails>(CheckoutCookie);
    public void SetDetails(CheckoutDetails details) => Write(CheckoutCookie, details, TimeSpan.FromHours(2), "/");
    public void ForgetDetails() => Http.Response.Cookies.Delete(CheckoutCookie, Options(null));

    // The order just placed, so the confirmation page can show it without trusting an id in the address.
    public Guid? LastOrderId => Read<Guid?>(OrderCookie);
    public void SetLastOrder(Guid orderId) => Write(OrderCookie, orderId, TimeSpan.FromHours(1), "/");

    // Recovery codes, shown once: handed from the form that made them to the page that shows them, in a protected cookie that lives five minutes.
    private const string RecoveryCookie = "shop_recovery";
    public void SetRecoveryCodes(IReadOnlyList<string> codes) => Write(RecoveryCookie, codes, TimeSpan.FromMinutes(5), "/account");
    public IReadOnlyList<string>? TakeRecoveryCodes()
    {
        var codes = Read<List<string>>(RecoveryCookie);
        if (codes is not null) Http.Response.Cookies.Delete(RecoveryCookie, Options(null, "/account"));
        return codes;
    }

    // The nonce asked of the social provider: it comes back inside the signed token, and the token is refused if it is not this one.
    private const string NonceCookie = "shop_social_nonce";
    public string NewSocialNonce()
    {
        var nonce = Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(16));
        Write(NonceCookie, nonce, TimeSpan.FromMinutes(10), "/account");
        return nonce;
    }
    public string? TakeSocialNonce()
    {
        var nonce = Read<string>(NonceCookie);
        if (nonce is not null) Http.Response.Cookies.Delete(NonceCookie, Options(null, "/account"));
        return nonce;
    }

    // Messages.
    public string Protect(Flash flash) => _data.ToTimeLimitedDataProtector().Protect(JsonSerializer.Serialize(flash), TimeSpan.FromMinutes(10));

    public Flash? Open(string? token)
    {
        if (string.IsNullOrEmpty(token)) return null;
        try { return JsonSerializer.Deserialize<Flash>(_data.ToTimeLimitedDataProtector().Unprotect(token)); }
        catch (Exception) { return null; } // tampered, expired or from another key: no message
    }

    /// <summary>The address to go to with a message: only a path on this site is ever used.</summary>
    public string Back(string? path, Flash flash, string? anchor = null) =>
        SafePath(path, "/basket") + "?f=" + Uri.EscapeDataString(Protect(flash)) + (anchor is null ? "" : "#" + anchor);

    /// <summary>A path on this site (no query), or the fallback: never an address on another site.</summary>
    public static string SafePath(string? path, string fallback)
    {
        var safe = path is { Length: > 0 } && path[0] == '/' && !path.StartsWith("//", StringComparison.Ordinal) && !path.Contains('\\') ? path : fallback;
        var cut = safe.IndexOf('?');
        return cut >= 0 ? safe[..cut] : safe;
    }

    private T? Read<T>(string cookie)
    {
        if (Http.Request.Cookies[cookie] is not { Length: > 0 } text) return default;
        try { return JsonSerializer.Deserialize<T>(_data.ToTimeLimitedDataProtector().Unprotect(text)); }
        catch (Exception) { return default; }
    }

    private void Write<T>(string cookie, T value, TimeSpan lifetime, string path) =>
        Http.Response.Cookies.Append(cookie, _data.ToTimeLimitedDataProtector().Protect(JsonSerializer.Serialize(value), lifetime), Options(lifetime, path));
}
