using Microsoft.Extensions.DependencyInjection;
using PiranhaCommerce.Api;
using PiranhaCommerce.Catalog;
using PiranhaCommerce.Core.Plugins;
using PiranhaCommerce.Customers;
using PiranhaCommerce.Data;

namespace PiranhaCommerce.Ecommerce;

/// <summary>Online-store behaviour layered on the shared catalogue. A future PiranhaCommerce.Epos is a sibling.</summary>
public sealed class EcommercePlugin : IPiranhaPlugin
{
    public PluginInfo Info { get; } = new(
        "PiranhaCommerce.Ecommerce", "Ecommerce", "0.1.0", "Online listings, baskets, checkout and orders.");

    public IReadOnlyList<IPiranhaPlugin> Requires { get; } = [new CatalogPlugin(), new CustomersPlugin()];

    public void ConfigureServices(IServiceCollection services)
    {
        services.AddSingleton<IModelContributor, EcommerceModelContributor>();
        services.AddScoped<IPromotionService, PromotionService>();
        services.AddSingleton<IApiEndpointContributor, EcommerceApiEndpoints>();
        services.AddSingleton<IStorefrontEndpointContributor, EcommerceStorefrontEndpoints>();
        services.AddOptions<EcommerceOptions>().BindConfiguration(EcommerceOptions.SectionName);
        services.AddScoped<IProductListingService, ProductListingService>();
        services.AddScoped<IBasketService, BasketService>();
        services.AddScoped<IOrderService, OrderService>();
        services.AddScoped<IShippingService, ShippingService>();
        services.AddScoped<ITaxService, TaxService>();
        services.AddScoped<IPaymentService, PaymentService>();
        services.AddSingleton<IPaymentProvider, ManualPaymentProvider>();
    }
}
