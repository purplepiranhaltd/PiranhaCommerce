namespace PiranhaCommerce.Core.Plugins;

/// <summary>Read-only view of the plugins registered in this application.</summary>
public interface IPluginCatalog
{
    IReadOnlyList<PluginInfo> Plugins { get; }
}

internal sealed class PluginCatalog(PiranhaCommerceBuilder builder) : IPluginCatalog
{
    public IReadOnlyList<PluginInfo> Plugins => builder.Plugins.Select(p => p.Info).ToList();
}
