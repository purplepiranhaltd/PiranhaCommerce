using Microsoft.Extensions.DependencyInjection;

namespace PiranhaCommerce.Core.Plugins;

/// <summary>
/// A Piranha Commerce plugin. Plugins are ordinary NuGet packages referenced at build time and
/// registered in code with <c>AddPlugin&lt;T&gt;()</c>. There is deliberately no runtime
/// upload or assembly scanning: only code built and shipped by the development team is loaded.
/// </summary>
public interface IPiranhaPlugin
{
    PluginDescriptor Descriptor { get; }

    /// <summary>Register services, options, EF model contributors, API/admin contributors.</summary>
    void ConfigureServices(IServiceCollection services);
}

public sealed record PluginDescriptor(string SystemName, string DisplayName, string Version, string? Description = null);
