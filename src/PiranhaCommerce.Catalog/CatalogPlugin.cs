using Microsoft.Extensions.DependencyInjection;
using PiranhaCommerce.Api;
using PiranhaCommerce.Core.Plugins;
using PiranhaCommerce.Data;

namespace PiranhaCommerce.Catalog;

/// <summary>Shared product catalogue used by every sales channel (Ecommerce today, EPOS later).</summary>
public sealed class CatalogPlugin : IPiranhaPlugin
{
    public const string SystemName = "PiranhaCommerce.Catalog";

    public PluginDescriptor Descriptor { get; } = new(SystemName, "Catalog", "0.1.0", "Products shared by all sales channels.");

    public void ConfigureServices(IServiceCollection services)
    {
        services.AddSingleton<IModelContributor, CatalogModelContributor>();
        services.AddSingleton<IApiEndpointContributor, CatalogApiEndpoints>();
        services.AddScoped<IProductService, ProductService>();
    }
}
