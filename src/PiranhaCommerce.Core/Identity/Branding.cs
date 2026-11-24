namespace PiranhaCommerce.Core.Identity;

/// <summary>The attribution notice the licence asks every page to show. See the LICENSE file and the licensing guide.</summary>
public static class PiranhaCommerceAttribution
{
    public const string Text = "Built on PiranhaCommerce";
    public const string Url = "https://piranhacommerce.com";

    /// <summary>The notice as HTML, for a shop front that is not built with Razor components. It opens in a new tab, so a shopper does not lose their place in the shop.</summary>
    public const string Html = "<a class=\"pc-attribution\" href=\"" + Url + "\" target=\"_blank\" rel=\"noopener\">" + Text + "</a>";
}

/// <summary>Settings under <c>PiranhaCommerce:Branding</c>.</summary>
public sealed class BrandingOptions
{
    public const string SectionName = "PiranhaCommerce:Branding";

    /// <summary>
    /// Show the attribution notice in the admin and the sample shop. On by default, and the licence requires it unless you hold a Commercial Licence.
    /// A shop front you build yourself shows the notice itself.
    /// </summary>
    public bool ShowAttribution { get; set; } = true;
}
