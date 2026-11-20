using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using PiranhaCommerce.Core.Plugins;

namespace PiranhaCommerce.Core.DependencyInjection;

public static class PiranhaCoreServiceCollectionExtensions
{
    public static PiranhaBuilder AddPiranhaCore(this IServiceCollection services)
    {
        var builder = new PiranhaBuilder(services);
        services.TryAddSingleton(builder);
        services.TryAddSingleton<IPluginCatalog, PluginCatalog>();
        return builder;
    }
}
