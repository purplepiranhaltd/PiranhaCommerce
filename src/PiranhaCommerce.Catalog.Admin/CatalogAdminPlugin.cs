using Microsoft.Extensions.DependencyInjection;
using PiranhaCommerce.Catalog.Admin.Pages;
using PiranhaCommerce.Core.Admin;
using PiranhaCommerce.Core.Plugins;

namespace PiranhaCommerce.Catalog.Admin;

/// <summary>Admin pages for the shared catalogue. Reference example of a plugin contributing admin pages.</summary>
public sealed class CatalogAdminPlugin : IPiranhaPlugin
{
    public PluginInfo Info { get; } = new("PiranhaCommerce.Catalog.Admin", "Catalog Admin", "0.1.0", "Admin pages for products.");

    public IReadOnlyList<IPiranhaPlugin> Requires { get; } = [new CatalogPlugin()];

    public void ConfigureServices(IServiceCollection services)
    {
        // Lets the admin router and endpoint mapping find this assembly's @page components.
        services.AddSingleton(new AdminPageAssembly(typeof(CatalogAdminPlugin).Assembly));
        services.AddSingleton<IAdminMenuContributor, CatalogAdminMenu>();
        services.AddSingleton<IAdminSlotContributor, CatalogSlotItems>();
        services.AddSingleton<IAdminBadgeProvider, StockTransfersBadge>();
    }
}

internal sealed class CatalogAdminMenu : IAdminMenuContributor
{
    public IEnumerable<AdminMenuItem> GetMenuItems() =>
    [
        new("catalog", "Catalog", Order: 10),
        new("catalog.products", "Products", "/admin/catalog/products", Order: 11, ParentKey: "catalog"),
        new("catalog.categories", "Categories", "/admin/catalog/categories", Order: 12, ParentKey: "catalog"),
        new("catalog.negative-stock", "Negative stock", "/admin/catalog/negative-stock", Order: 13, ParentKey: "catalog"),
        new("catalog.stock-locations", "Stock locations", "/admin/catalog/stock-locations", Order: 14, ParentKey: "catalog"),
        new("catalog.stock-transfers", "Stock transfers", "/admin/catalog/stock-transfers", Order: 15, ParentKey: "catalog", BadgeKey: "stock-transfers"),
        new("catalog.staff-locations", "Staff locations", "/admin/catalog/staff-locations", Order: 16, ParentKey: "catalog"),
    ];
}

internal sealed class CatalogSlotItems : IAdminSlotContributor
{
    public IEnumerable<AdminSlotItem> GetItems() =>
        [
            new(AdminSlots.ProductEditPanels, typeof(ProductImagesPanel), Order: 3),
            new(AdminSlots.ProductEditPanels, typeof(ProductVariantsPanel), Order: 4),
            new(AdminSlots.ProductEditPanels, typeof(ProductCategoriesPanel), Order: 5),
            new(AdminSlots.ProductEditPanels, typeof(ProductBarcodesPanel), Order: 6),
            new(AdminSlots.ProductEditPanels, typeof(ProductStockPanel), Order: 7),
        ];
}

/// <summary>The number of transfers still in transit, shown beside the menu entry.</summary>
internal sealed class StockTransfersBadge : IAdminBadgeProvider
{
    public string Key => "stock-transfers";
    public Task<int> CountAsync(IServiceProvider services, CancellationToken ct = default) => services.GetRequiredService<IStockTransferService>().InTransitCountAsync(ct);
}
