namespace PiranhaCommerce.Core.Plugins;

/// <summary>Read-only view of the plugins registered in this application.</summary>
public interface IPluginCatalog
{
    IReadOnlyList<PluginDescriptor> Plugins { get; }
}

internal sealed class PluginCatalog(PiranhaBuilder builder) : IPluginCatalog
{
    public IReadOnlyList<PluginDescriptor> Plugins => builder.Plugins.Select(p => p.Descriptor).ToList();
}
