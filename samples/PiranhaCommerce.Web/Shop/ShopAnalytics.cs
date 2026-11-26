namespace PiranhaCommerce.Web.Shop;

/// <summary>
/// Whether a shop page may load the analytics script. The script always reports the page's whole address, and some shop addresses carry things that
/// must not leave the shop (a password reset or unsubscribe token, an email address, a form's saved values or a two-factor sign-in step in <c>?f=</c>).
/// So a page is reported only when every query parameter is known to be harmless: a parameter added later is left out until it is listed here.
/// </summary>
public static class ShopAnalytics
{
    private static readonly HashSet<string> Harmless = new(StringComparer.OrdinalIgnoreCase)
    {
        "brand", "spec", "tag", "stock", "sort", "page", "q", "variant", "setup", "returnTo",
        "ref", "source", "utm_source", "utm_medium", "utm_campaign", "utm_content", "utm_term",
    };

    public static bool MayReport(IQueryCollection query, ShopSession session)
    {
        foreach (var (name, value) in query)
        {
            if (Harmless.Contains(name)) continue;
            // A message to show on the next page is harmless (and encrypted). One that carries values back to a form, or can no longer be read, is not.
            if (name == "f" && value.Count == 1 && session.Open(value[0]) is { Values: null or { Count: 0 } }) continue;
            return false;
        }
        return true;
    }
}
