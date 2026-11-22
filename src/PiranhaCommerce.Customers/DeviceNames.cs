namespace PiranhaCommerce.Customers;

/// <summary>Turns a User-Agent header into something a shopper can recognise ("Chrome on Windows") and keeps what is stored small and clean.</summary>
internal static class DeviceNames
{
    private const int MaxStored = 200;

    /// <summary>Control characters out, whitespace tidied, cut to the stored length. Null for nothing useful.</summary>
    public static string? Clean(string? userAgent)
    {
        if (string.IsNullOrWhiteSpace(userAgent)) return null;
        var chars = userAgent.Where(c => !char.IsControl(c)).ToArray();
        var s = new string(chars).Trim();
        if (s.Length == 0) return null;
        return s.Length > MaxStored ? s[..MaxStored] : s;
    }

    /// <summary>"Chrome on Windows", "Safari on iPhone"; an app's own name when it is not a browser; "Unknown device" when nothing was sent.</summary>
    public static string Describe(string? userAgent)
    {
        var ua = Clean(userAgent);
        if (ua is null) return "Unknown device";

        // Order matters: Edge and Opera also say Chrome, Chrome also says Safari, and iPhone also says Mac OS X.
        var os =
            Has(ua, "iPhone") ? "iPhone" :
            Has(ua, "iPad") ? "iPad" :
            Has(ua, "Android") ? "Android" :
            Has(ua, "Windows") ? "Windows" :
            Has(ua, "Mac OS X") || Has(ua, "Macintosh") ? "Mac" :
            Has(ua, "CrOS") ? "ChromeOS" :
            Has(ua, "Linux") ? "Linux" : null;
        var browser =
            Has(ua, "Edg/") || Has(ua, "EdgA/") || Has(ua, "EdgiOS/") ? "Edge" :
            Has(ua, "OPR/") ? "Opera" :
            Has(ua, "Firefox/") || Has(ua, "FxiOS/") ? "Firefox" :
            Has(ua, "Chrome/") || Has(ua, "CriOS/") ? "Chrome" :
            Has(ua, "Safari/") ? "Safari" : null;

        if (browser is not null) return os is null ? browser : $"{browser} on {os}";

        // Not a browser: probably the shop's own app ("MyShop/2.1 (iOS 18)"), which is the best name there is.
        var name = ua.Length > 60 ? ua[..60] : ua;
        return name;
    }

    private static bool Has(string s, string part) => s.Contains(part, StringComparison.OrdinalIgnoreCase);
}
