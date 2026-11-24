using Microsoft.Extensions.DependencyInjection;

namespace PiranhaCommerce.Core.Plugins;

/// <summary>
/// A PiranhaCommerce plugin. Plugins are ordinary NuGet packages referenced at build time and
/// registered in code with <c>AddPlugin&lt;T&gt;()</c>. There is deliberately no runtime
/// upload or assembly scanning: only code built and shipped by the development team is loaded.
/// </summary>
public interface IPiranhaPlugin
{
    PluginInfo Info { get; }

    /// <summary>
    /// Plugins this one needs, as instances of types from packages this package references. They are registered
    /// automatically (once, and before this plugin), so hosts never have to list or order them.
    /// </summary>
    IReadOnlyList<IPiranhaPlugin> Requires => [];

    /// <summary>Register services, options, EF model contributors, API/admin contributors.</summary>
    void ConfigureServices(IServiceCollection services);
}

/// <param name="Id">Stable identifier; by convention the NuGet package id.</param>
public sealed record PluginInfo(string Id, string Name, string Version, string? Description = null);
