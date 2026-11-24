using Microsoft.Extensions.DependencyInjection;
using PiranhaCommerce.Catalog.Admin;
using PiranhaCommerce.Core.Admin;
using PiranhaCommerce.Core.Plugins;

namespace PiranhaCommerce.Ecommerce.Admin;

/// <summary>Adds the "Online store" panel to the shared product edit page through an admin slot, and the order pages.</summary>
public sealed class EcommerceAdminPlugin : IPiranhaCommercePlugin
{
    public PluginInfo Info { get; } = new(
        "PiranhaCommerce.Ecommerce.Admin", "Ecommerce Admin", "0.1.0", "Online-store settings and orders in the admin.");

    // The panel lives on a Catalog.Admin page, so that page's plugin is a real requirement.
    public IReadOnlyList<IPiranhaCommercePlugin> Requires { get; } = [new EcommercePlugin(), new CatalogAdminPlugin()];

    public void ConfigureServices(IServiceCollection services)
    {
        services.AddSingleton(new AdminPageAssembly(typeof(EcommerceAdminPlugin).Assembly));
        services.AddSingleton<IAdminMenuContributor, EcommerceAdminMenu>();
        services.AddSingleton<IAdminBadgeProvider, UnreconciledBadge>();
        services.AddSingleton<IAdminBadgeProvider, DisputesBadge>();
        services.AddSingleton<IAdminBadgeProvider, ReturnRequestsBadge>();
        services.AddSingleton<IAdminSlotContributor, EcommerceSlotItems>();
    }
}

internal sealed class EcommerceAdminMenu : IAdminMenuContributor
{
    public IEnumerable<AdminMenuItem> GetMenuItems() =>
    [
        new("orders", "Orders", Icon: "cart", Order: 15),
        new("orders.list", "All orders", "/admin/orders", Order: 16, ParentKey: "orders"),
        new("reports.orders", "Orders", "/admin/orders/reports", Order: 30, ParentKey: "reports"),
        new("orders.shipping", "Shipping methods", "/admin/orders/shipping", Order: 17, ParentKey: "orders"),
        new("orders.shippingzones", "Shipping zones", "/admin/orders/shipping-zones", Order: 17, ParentKey: "orders"),
        new("orders.shippingcarriers", "Shipping carriers", "/admin/orders/shipping-carriers", Order: 17, ParentKey: "orders"),
        new("orders.tax", "Tax", "/admin/orders/tax", Order: 18, ParentKey: "orders"),
        new("orders.promotions", "Promotions", "/admin/orders/promotions", Order: 19, ParentKey: "orders"),
        new("orders.discounts", "Discounts", "/admin/orders/discounts", Order: 19, ParentKey: "orders"),
        new("orders.sales", "Sales", "/admin/orders/sales", Order: 19, ParentKey: "orders"),
        new("orders.group-discounts", "Group category discounts", "/admin/orders/group-discounts", Order: 19, ParentKey: "orders"),
        new("orders.reviews", "Product reviews", "/admin/orders/reviews", Order: 20, ParentKey: "orders"),
        new("orders.stockalerts", "Back-in-stock requests", "/admin/orders/stock-alerts", Order: 20, ParentKey: "orders"),
        new("orders.wishlists", "Wishlists", "/admin/orders/wishlists", Order: 20, ParentKey: "orders"),
        new("orders.vouchers", "Gift vouchers", "/admin/orders/vouchers", Order: 20, ParentKey: "orders"),
        new("orders.unreconciled", "Unreconciled payments", "/admin/orders/unreconciled", Order: 21, ParentKey: "orders", BadgeKey: "unreconciled"),
        new("orders.disputes", "Disputes", "/admin/orders/disputes", Order: 22, ParentKey: "orders", BadgeKey: "disputes"),
        new("orders.returns", "Return requests", "/admin/orders/return-requests", Order: 23, ParentKey: "orders", BadgeKey: "return-requests"),
    ];
}

internal sealed class EcommerceSlotItems : IAdminSlotContributor
{
    public IEnumerable<AdminSlotItem> GetItems() =>
        [new(AdminSlots.ProductEditPanels, typeof(ProductListingPanel), Order: 10), new(AdminSlots.ProductEditPanels, typeof(ProductGroupPricesPanel), Order: 11), new(AdminSlots.ProductEditPanels, typeof(ProductPriceBreaksPanel), Order: 12), new(AdminSlots.ProductEditPanels, typeof(ProductRelationsPanel), Order: 13)];
}
