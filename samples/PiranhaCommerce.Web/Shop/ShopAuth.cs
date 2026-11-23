using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using PiranhaCommerce.Customers;

namespace PiranhaCommerce.Web.Shop;

/// <summary>A signed-in shopper, as the cookie says.</summary>
public sealed record Shopper(Guid Id, string Email, string? Name);

/// <summary>
/// How shoppers sign in to the sample shop: a cookie of its own, separate from the admin's (staff and shoppers are different people with different
/// rights, and one must never be taken for the other). It is never the default scheme, so nothing but the shop's own code reads it, and it is asked
/// for by name. The cookie holds the customer's id and the version of their sign-in; on every request the version is compared with the current one,
/// so changing a password, or deactivating the customer, ends every shop session at once.
/// </summary>
public static class ShopAuth
{
    public const string Scheme = "Petalwick.Shopper";

    private const string VersionClaim = "sv";

    public static void Add(AuthenticationBuilder authentication) => authentication.AddCookie(Scheme, o =>
    {
        o.Cookie.Name = ".Petalwick.Shopper";
        o.Cookie.HttpOnly = true;
        o.Cookie.SameSite = SameSiteMode.Lax;
        o.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest; // the sample runs on http://localhost; a real shop is https
        o.ExpireTimeSpan = TimeSpan.FromDays(14);
        o.SlidingExpiration = true;
        o.Events.OnValidatePrincipal = async context =>
        {
            var accounts = context.HttpContext.RequestServices.GetRequiredService<ICustomerAccountService>();
            var ok = Guid.TryParse(context.Principal?.FindFirstValue(ClaimTypes.NameIdentifier), out var id)
                && int.TryParse(context.Principal?.FindFirstValue(VersionClaim), out var version)
                && await accounts.IsSessionCurrentAsync(id, version, context.HttpContext.RequestAborted);
            if (!ok)
            {
                context.RejectPrincipal();
                await context.HttpContext.SignOutAsync(Scheme);
            }
        };
    });

    public static ClaimsPrincipal Principal(SignInResult result, ProfileDto? profile)
    {
        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, result.CustomerId!.Value.ToString()),
            new(ClaimTypes.Email, result.Email ?? ""),
            new(VersionClaim, result.SessionVersion.ToString()),
        };
        var name = string.Join(' ', new[] { profile?.FirstName, profile?.LastName }.Where(n => !string.IsNullOrWhiteSpace(n)));
        if (name.Length > 0) claims.Add(new Claim(ClaimTypes.Name, name));
        return new ClaimsPrincipal(new ClaimsIdentity(claims, Scheme));
    }

    public static Shopper? From(ClaimsPrincipal? principal) =>
        Guid.TryParse(principal?.FindFirstValue(ClaimTypes.NameIdentifier), out var id)
            ? new Shopper(id, principal!.FindFirstValue(ClaimTypes.Email) ?? "", principal.FindFirstValue(ClaimTypes.Name))
            : null;
}
