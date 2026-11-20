using Microsoft.Extensions.DependencyInjection;
using PiranhaCommerce.Api;
using PiranhaCommerce.Catalog;
using PiranhaCommerce.Core.Plugins;
using PiranhaCommerce.Data;

namespace PiranhaCommerce.Ecommerce;

/// <summary>Online-store behaviour layered on the shared catalogue. A future PiranhaCommerce.Epos is a sibling.</summary>
public sealed class EcommercePlugin : IPiranhaPlugin
{
    public const string SystemName = "PiranhaCommerce.Ecommerce";

    public PluginDescriptor Descriptor { get; } = new(
        SystemName, "Ecommerce", "0.1.0", "Online listings, cart, checkout and orders.",
        DependsOn: [CatalogPlugin.SystemName]);

    public void ConfigureServices(IServiceCollection services)
    {
        services.AddSingleton<IModelContributor, EcommerceModelContributor>();
        services.AddSingleton<IApiEndpointContributor, EcommerceApiEndpoints>();
        services.AddScoped<IProductListingService, ProductListingService>();
    }
}
