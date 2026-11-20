using Microsoft.Extensions.DependencyInjection;
using PiranhaCommerce.Core.Admin;
using PiranhaCommerce.Core.Plugins;

namespace PiranhaCommerce.Ecommerce.Admin;

/// <summary>Admin UI for the ecommerce package. Doubles as the reference example of a plugin contributing admin pages.</summary>
public sealed class EcommerceAdminPlugin : IPiranhaPlugin
{
    public PluginDescriptor Descriptor { get; } =
        new("PiranhaCommerce.Ecommerce.Admin", "Ecommerce Admin", "0.1.0", "Admin pages for the catalog.");

    public void ConfigureServices(IServiceCollection services)
    {
        // Lets the admin router and endpoint mapping find this assembly's @page components.
        services.AddSingleton(new AdminPageAssembly(typeof(EcommerceAdminPlugin).Assembly));
        services.AddSingleton<IAdminMenuContributor, EcommerceAdminMenu>();
    }
}

internal sealed class EcommerceAdminMenu : IAdminMenuContributor
{
    public IEnumerable<AdminMenuItem> GetMenuItems() =>
    [
        new("catalog", "Catalog", Order: 10),
        new("catalog.products", "Products", "/admin/catalog/products", Order: 11, ParentKey: "catalog"),
    ];
}
