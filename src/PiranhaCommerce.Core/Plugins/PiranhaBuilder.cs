using Microsoft.Extensions.DependencyInjection;

namespace PiranhaCommerce.Core.Plugins;

/// <summary>Fluent builder returned by <c>AddPiranhaCore()</c>; packages add extension methods to it.</summary>
public sealed class PiranhaBuilder
{
    private readonly List<IPiranhaPlugin> _plugins = [];

    public PiranhaBuilder(IServiceCollection services) => Services = services;

    public IServiceCollection Services { get; }
    public IReadOnlyList<IPiranhaPlugin> Plugins => _plugins;

    public PiranhaBuilder AddPlugin<TPlugin>() where TPlugin : IPiranhaPlugin, new() => AddPlugin(new TPlugin());

    public PiranhaBuilder AddPlugin(IPiranhaPlugin plugin)
    {
        if (_plugins.Any(p => p.Descriptor.SystemName.Equals(plugin.Descriptor.SystemName, StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException($"Plugin '{plugin.Descriptor.SystemName}' is already registered.");

        _plugins.Add(plugin);
        plugin.ConfigureServices(Services);
        return this;
    }
}
