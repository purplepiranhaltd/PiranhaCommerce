using Microsoft.Extensions.DependencyInjection;

namespace PiranhaCommerce.Core.Plugins;

/// <summary>Fluent builder returned by <c>AddPiranhaCore()</c>; packages add extension methods to it.</summary>
public sealed class PiranhaBuilder
{
    private readonly List<IPiranhaPlugin> _plugins = [];
    private readonly HashSet<string> _registering = new(StringComparer.OrdinalIgnoreCase);

    public PiranhaBuilder(IServiceCollection services) => Services = services;

    public IServiceCollection Services { get; }

    /// <summary>Registered plugins in registration order (required plugins come before the plugins that need them).</summary>
    public IReadOnlyList<IPiranhaPlugin> Plugins => _plugins;

    public PiranhaBuilder AddPlugin<TPlugin>() where TPlugin : IPiranhaPlugin, new() => AddPlugin(new TPlugin());

    /// <summary>
    /// Registers a plugin after the plugins it <see cref="IPiranhaPlugin.Requires"/>. Registering a plugin that is
    /// already registered (directly, or because another plugin required it) does nothing.
    /// </summary>
    public PiranhaBuilder AddPlugin(IPiranhaPlugin plugin)
    {
        var id = plugin.Info.Id;
        if (_plugins.Any(p => p.Info.Id.Equals(id, StringComparison.OrdinalIgnoreCase)))
            return this;

        if (!_registering.Add(id))
            throw new InvalidOperationException($"Plugin '{id}' requires itself through a circular dependency.");
        try
        {
            foreach (var required in plugin.Requires)
                AddPlugin(required);

            _plugins.Add(plugin);
            plugin.ConfigureServices(Services);
        }
        finally
        {
            _registering.Remove(id);
        }
        return this;
    }
}
