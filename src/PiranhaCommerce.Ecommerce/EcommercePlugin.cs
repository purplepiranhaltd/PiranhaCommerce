using Microsoft.Extensions.DependencyInjection;
using PiranhaCommerce.Api;
using PiranhaCommerce.Core.Admin;
using PiranhaCommerce.Core.Plugins;
using PiranhaCommerce.Data;

namespace PiranhaCommerce.Ecommerce;

/// <summary>The ecommerce package is itself just a plugin on top of Core. A future PiranhaCommerce.Epos would be a sibling.</summary>
public sealed class EcommercePlugin : IPiranhaPlugin
{
    public PluginDescriptor Descriptor { get; } = new("PiranhaCommerce.Ecommerce", "Ecommerce", "0.1.0", "Catalog, cart, checkout and orders.");

    public void ConfigureServices(IServiceCollection services)
    {
        services.AddSingleton<IModelContributor, EcommerceModelContributor>();
        services.AddSingleton<IApiEndpointContributor, EcommerceApiEndpoints>();
        services.AddScoped<Catalog.IProductService, Catalog.ProductService>();
    }
}
