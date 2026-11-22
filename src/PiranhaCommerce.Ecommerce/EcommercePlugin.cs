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
        services.AddSingleton<PiranhaCommerce.Core.Email.IEmailTemplateContributor, EcommerceEmailContributor>();
        services.AddScoped<IPromotionService, PromotionService>();
        services.AddScoped<IVoucherService, VoucherService>();
        services.AddScoped<IGatewayService, GatewayService>();
        services.AddScoped<IUnreconciledPaymentService, UnreconciledPaymentService>();
        services.AddSingleton<IApiEndpointContributor, EcommerceApiEndpoints>();
        services.AddSingleton<IStorefrontEndpointContributor, EcommerceStorefrontEndpoints>();
        services.AddOptions<EcommerceOptions>().BindConfiguration(EcommerceOptions.SectionName);
        services.AddScoped<IProductListingService, ProductListingService>();
        services.AddScoped<IBasketService, BasketService>();
        services.AddScoped<IGroupPricingService, GroupPricingService>();
        services.AddScoped<ISaleService, SaleService>();
        services.AddSingleton<IApiEndpointContributor, SaleEndpoints>();
        services.AddSingleton<IStorefrontEndpointContributor, SaleStorefrontEndpoints>();
        services.AddScoped<IGuestBasketService>(sp => (BasketService)sp.GetRequiredService<IBasketService>());
        services.AddSingleton<IStorefrontEndpointContributor, GuestStorefrontEndpoints>();
        services.AddSingleton<IApiEndpointContributor, GroupPricingEndpoints>();
        services.AddSingleton<IStorefrontEndpointContributor, GroupPricingStorefrontEndpoints>();
        services.AddScoped<IBasketHousekeeper, BasketHousekeeper>();
        services.AddHostedService<BasketHousekeepingService>();
        services.AddScoped<OrderMailer>();
        services.AddScoped<PiranhaCommerce.Customers.ICustomerDataContributor, EcommerceCustomerData>();
        services.AddScoped<IOrderService, OrderService>();
        services.AddScoped<IUnpaidOrderExpiry>(sp => (OrderService)sp.GetRequiredService<IOrderService>());
        services.AddHostedService<UnpaidOrderExpiryService>();
        services.AddScoped<IFulfilmentService, FulfilmentService>();
        services.AddScoped<IReturnRequestService, ReturnRequestService>();
        services.AddScoped<IDisputeService, DisputeService>();
        services.AddScoped<IShippingService, ShippingService>();
        services.AddScoped<ITaxService, TaxService>();
        services.AddScoped<IPaymentService, PaymentService>();
        services.AddSingleton<IPaymentProvider, ManualPaymentProvider>();
    }
}
