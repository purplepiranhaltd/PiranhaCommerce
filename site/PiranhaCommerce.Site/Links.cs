using Microsoft.Extensions.Options;

namespace PiranhaCommerce.Site;

public static class Links
{
    /// <summary>The docs address for links within this host: empty on the docs host, "/docs" elsewhere.</summary>
    public static string DocsPrefix(HttpContext? context) => context is not null && SiteHost.IsDocsHost(context.Request.Host.Host) ? "" : "/docs";

    /// <summary>Where "Docs" in the navigation goes: the configured docs host, else this host's docs.</summary>
    public static string Docs(HttpContext? context)
    {
        var configured = context?.RequestServices.GetService<IOptions<SiteOptions>>()?.Value.DocsBaseUrl;
        if (!string.IsNullOrWhiteSpace(configured)) return configured.TrimEnd('/');
        var prefix = DocsPrefix(context);
        return prefix.Length == 0 ? "/" : prefix;
    }
}
