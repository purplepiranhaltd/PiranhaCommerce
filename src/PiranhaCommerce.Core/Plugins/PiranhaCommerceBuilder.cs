using Microsoft.Extensions.DependencyInjection;

namespace PiranhaCommerce.Core.Plugins;

/// <summary>Fluent builder returned by <c>AddPiranhaCommerceCore()</c>; packages add extension methods to it.</summary>
public sealed class PiranhaCommerceBuilder
{
    private readonly List<IPiranhaCommercePlugin> _plugins = [];
    private readonly HashSet<string> _registering = new(StringComparer.OrdinalIgnoreCase);

    public PiranhaCommerceBuilder(IServiceCollection services) => Services = services;

    public IServiceCollection Services { get; }

    /// <summary>Registered plugins in registration order (required plugins come before the plugins that need them).</summary>
    public IReadOnlyList<IPiranhaCommercePlugin> Plugins => _plugins;

    public PiranhaCommerceBuilder AddPlugin<TPlugin>() where TPlugin : IPiranhaCommercePlugin, new() => AddPlugin(new TPlugin());

    /// <summary>
    /// Registers a plugin after the plugins it <see cref="IPiranhaCommercePlugin.Requires"/>. Registering a plugin that is
    /// already registered (directly, or because another plugin required it) does nothing.
    /// </summary>
    public PiranhaCommerceBuilder AddPlugin(IPiranhaCommercePlugin plugin)
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
