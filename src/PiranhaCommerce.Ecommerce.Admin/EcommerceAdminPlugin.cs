using Microsoft.Extensions.DependencyInjection;
using PiranhaCommerce.Catalog.Admin;
using PiranhaCommerce.Core.Admin;
using PiranhaCommerce.Core.Plugins;

namespace PiranhaCommerce.Ecommerce.Admin;

/// <summary>Adds the "Online store" panel to the shared product edit page through an admin slot.</summary>
public sealed class EcommerceAdminPlugin : IPiranhaPlugin
{
    public PluginInfo Info { get; } = new(
        "PiranhaCommerce.Ecommerce.Admin", "Ecommerce Admin", "0.1.0", "Online-store settings in the admin.");

    // The panel lives on a Catalog.Admin page, so that page's plugin is a real requirement.
    public IReadOnlyList<IPiranhaPlugin> Requires { get; } = [new EcommercePlugin(), new CatalogAdminPlugin()];

    public void ConfigureServices(IServiceCollection services)
        => services.AddSingleton<IAdminSlotContributor, EcommerceSlotItems>();
}

internal sealed class EcommerceSlotItems : IAdminSlotContributor
{
    public IEnumerable<AdminSlotItem> GetItems() =>
        [new(AdminSlots.ProductEditPanels, typeof(ProductListingPanel), Order: 10)];
}
