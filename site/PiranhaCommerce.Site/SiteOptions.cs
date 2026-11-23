namespace PiranhaCommerce.Site;

/// <summary>Settings under "Site". All optional: the site works as it is on one host, with the docs under /docs.</summary>
public sealed class SiteOptions
{
    public const string SectionName = "Site";

    /// <summary>Where the documentation lives when it is on its own host (for example https://docs.piranhacommerce.com). Empty serves it under /docs on this host.</summary>
    public string? DocsBaseUrl { get; set; }

    /// <summary>The promotional site's address, for the link back from the docs host. Empty hides the link.</summary>
    public string? HomeUrl { get; set; }

    /// <summary>The address of a running sample shop to link to ("See the demo"). Empty hides the link.</summary>
    public string? DemoUrl { get; set; }

    /// <summary>Where the docs Markdown is. Empty finds the "docs" folder beside the app, or the repository's docs/public.</summary>
    public string? DocsPath { get; set; }
}

public static class SiteHost
{
    /// <summary>True for docs.example.com and the like: the host that serves the documentation at its root.</summary>
    public static bool IsDocsHost(string? host) => host is not null && host.StartsWith("docs.", StringComparison.OrdinalIgnoreCase);
}

internal static class DocsLocator
{
    public static string Find(IHostEnvironment env, IConfiguration config)
    {
        var configured = config[$"{SiteOptions.SectionName}:{nameof(SiteOptions.DocsPath)}"];
        if (!string.IsNullOrWhiteSpace(configured)) return configured;
        foreach (var candidate in new[]
                 {
                     Path.Combine(AppContext.BaseDirectory, "docs"),
                     Path.Combine(env.ContentRootPath, "docs"),
                     Path.GetFullPath(Path.Combine(env.ContentRootPath, "..", "..", "docs", "public")),
                 })
            if (File.Exists(Path.Combine(candidate, "sections.json"))) return candidate;
        return Path.Combine(AppContext.BaseDirectory, "docs");
    }
}
