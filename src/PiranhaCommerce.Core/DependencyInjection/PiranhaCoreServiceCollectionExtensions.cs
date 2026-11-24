using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using PiranhaCommerce.Core.Email;
using PiranhaCommerce.Core.Files;
using PiranhaCommerce.Core.Plugins;

namespace PiranhaCommerce.Core.DependencyInjection;

public static class PiranhaCoreServiceCollectionExtensions
{
    public static PiranhaBuilder AddPiranhaCommerceCore(this IServiceCollection services)
    {
        var builder = new PiranhaBuilder(services);
        services.TryAddSingleton(builder);
        services.TryAddSingleton<IPluginCatalog, PluginCatalog>();
        services.AddPiranhaCommerceEmailDefaults(); // an email plugin registers its own IEmailSender afterwards and wins
        services.AddPiranhaCommerceFileDefaults(); // a storage plugin registers its own IFileStore afterwards and wins
        return builder;
    }
}
