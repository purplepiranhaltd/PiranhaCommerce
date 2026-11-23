using Microsoft.Extensions.DependencyInjection;
using PiranhaCommerce.Api;
using PiranhaCommerce.Core.Plugins;
using PiranhaCommerce.Data;

namespace PiranhaCommerce.Catalog;

/// <summary>Shared product catalogue used by every sales channel (Ecommerce today, EPOS later).</summary>
public sealed class CatalogPlugin : IPiranhaPlugin
{
    public PluginInfo Info { get; } = new("PiranhaCommerce.Catalog", "Catalog", "0.1.0", "Products shared by all sales channels.");

    public void ConfigureServices(IServiceCollection services)
    {
        services.AddSingleton<IModelContributor, CatalogModelContributor>();
        services.AddSingleton<IApiEndpointContributor, CatalogApiEndpoints>();
        services.AddSingleton<IApiEndpointContributor, CategoryApiEndpoints>();
        services.AddScoped<IProductService, ProductService>();
        services.AddSingleton<IApiEndpointContributor, BarcodeApiEndpoints>();
        services.AddSingleton<IApiEndpointContributor, StockApiEndpoints>();
        services.AddScoped<ICategoryService, CategoryService>();
        services.AddScoped<IBrandService, BrandService>();
        services.AddSingleton<IApiEndpointContributor, BrandApiEndpoints>();
        services.AddScoped<ISpecificationService, SpecificationService>();
        services.AddSingleton<IApiEndpointContributor, SpecificationApiEndpoints>();
        services.AddScoped<IStockService, StockService>();
        services.AddScoped<IStockLocationService, StockLocationService>();
        services.AddOptions<StockOptions>().BindConfiguration(StockOptions.SectionName);
        services.AddScoped<IStaffLocationService, StaffLocationService>();
        services.AddScoped<IStockTransferService, StockTransferService>();
        services.AddSingleton<IApiEndpointContributor, StockTransferApiEndpoints>();
        services.AddScoped<IBarcodeService, BarcodeService>();
        services.AddScoped<IProductVariantService, ProductVariantService>();
        services.AddOptions<ImageOptions>().BindConfiguration(ImageOptions.SectionName).ValidateDataAnnotations();
        services.AddScoped<IProductImageService, ProductImageService>();
        services.AddOptions<CatalogImportOptions>().BindConfiguration(CatalogImportOptions.SectionName).ValidateDataAnnotations();
        services.AddScoped<ICatalogTransferService, CatalogTransferService>();
        services.AddSingleton<IApiEndpointContributor, ImportExportEndpoints>();
        services.AddSingleton<IApiEndpointContributor, ImageApiEndpoints>();
        services.AddSingleton<IStorefrontEndpointContributor, ImageStorefrontEndpoints>();
        services.AddSingleton<IApiEndpointContributor, VariantApiEndpoints>();
    }
}
