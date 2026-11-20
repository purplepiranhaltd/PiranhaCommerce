using Microsoft.Extensions.DependencyInjection;
using PiranhaCommerce.Core.Admin;
using PiranhaCommerce.Core.Plugins;

namespace PiranhaCommerce.Ecommerce.Admin;

/// <summary>Adds the "Online store" panel to the shared product edit page via a widget zone.</summary>
public sealed class EcommerceAdminPlugin : IPiranhaPlugin
{
    public PluginDescriptor Descriptor { get; } = new(
        "PiranhaCommerce.Ecommerce.Admin", "Ecommerce Admin", "0.1.0", "Online-store settings in the admin.",
        DependsOn: [EcommercePlugin.SystemName, "PiranhaCommerce.Catalog.Admin"]);

    public void ConfigureServices(IServiceCollection services)
        => services.AddSingleton<IAdminWidgetContributor, EcommerceWidgets>();
}

internal sealed class EcommerceWidgets : IAdminWidgetContributor
{
    public IEnumerable<AdminWidget> GetWidgets() =>
        [new(AdminWidgetZones.ProductEditTabs, typeof(ProductListingPanel), Order: 10)];
}
