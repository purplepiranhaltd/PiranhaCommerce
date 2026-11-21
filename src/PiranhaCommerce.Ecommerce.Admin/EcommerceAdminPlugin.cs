using Microsoft.Extensions.DependencyInjection;
using PiranhaCommerce.Catalog.Admin;
using PiranhaCommerce.Core.Admin;
using PiranhaCommerce.Core.Plugins;

namespace PiranhaCommerce.Ecommerce.Admin;

/// <summary>Adds the "Online store" panel to the shared product edit page through an admin slot, and the order pages.</summary>
public sealed class EcommerceAdminPlugin : IPiranhaPlugin
{
    public PluginInfo Info { get; } = new(
        "PiranhaCommerce.Ecommerce.Admin", "Ecommerce Admin", "0.1.0", "Online-store settings and orders in the admin.");

    // The panel lives on a Catalog.Admin page, so that page's plugin is a real requirement.
    public IReadOnlyList<IPiranhaPlugin> Requires { get; } = [new EcommercePlugin(), new CatalogAdminPlugin()];

    public void ConfigureServices(IServiceCollection services)
    {
        services.AddSingleton(new AdminPageAssembly(typeof(EcommerceAdminPlugin).Assembly));
        services.AddSingleton<IAdminMenuContributor, EcommerceAdminMenu>();
        services.AddSingleton<IAdminSlotContributor, EcommerceSlotItems>();
    }
}

internal sealed class EcommerceAdminMenu : IAdminMenuContributor
{
    public IEnumerable<AdminMenuItem> GetMenuItems() =>
    [
        new("orders", "Orders", Order: 15),
        new("orders.list", "All orders", "/admin/orders", Order: 16, ParentKey: "orders"),
        new("orders.shipping", "Shipping methods", "/admin/orders/shipping", Order: 17, ParentKey: "orders"),
        new("orders.tax", "Tax", "/admin/orders/tax", Order: 18, ParentKey: "orders"),
        new("orders.promotions", "Promotions", "/admin/orders/promotions", Order: 19, ParentKey: "orders"),
        new("orders.vouchers", "Gift vouchers", "/admin/orders/vouchers", Order: 20, ParentKey: "orders"),
        new("orders.unreconciled", "Unreconciled payments", "/admin/orders/unreconciled", Order: 21, ParentKey: "orders"),
    ];
}

internal sealed class EcommerceSlotItems : IAdminSlotContributor
{
    public IEnumerable<AdminSlotItem> GetItems() =>
        [new(AdminSlots.ProductEditPanels, typeof(ProductListingPanel), Order: 10)];
}
