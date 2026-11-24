using Microsoft.Extensions.DependencyInjection;
using PiranhaCommerce.Core.Admin;
using PiranhaCommerce.Core.Plugins;
using PiranhaCommerce.Customers.Admin.Pages;

namespace PiranhaCommerce.Customers.Admin;

/// <summary>Admin pages for customers, plus the address book panel on the customer page.</summary>
public sealed class CustomersAdminPlugin : IPiranhaPlugin
{
    public PluginInfo Info { get; } = new("PiranhaCommerce.Customers.Admin", "Customers Admin", "0.1.0", "Admin pages for customers.");

    public IReadOnlyList<IPiranhaPlugin> Requires { get; } = [new CustomersPlugin()];

    public void ConfigureServices(IServiceCollection services)
    {
        services.AddSingleton(new AdminPageAssembly(typeof(CustomersAdminPlugin).Assembly));
        services.AddSingleton<IAdminMenuContributor, CustomersAdminMenu>();
        services.AddSingleton<IAdminSlotContributor, CustomersSlotItems>();
    }
}

internal sealed class CustomersAdminMenu : IAdminMenuContributor
{
    public IEnumerable<AdminMenuItem> GetMenuItems() =>
    [
        new("customers", "Customers", Icon: "users", Order: 20),
        new("customers.list", "All customers", "/admin/customers", Order: 21, ParentKey: "customers"),
        new("customers.groups", "Groups", "/admin/customers/groups", Order: 22, ParentKey: "customers"),
    ];
}

internal sealed class CustomersSlotItems : IAdminSlotContributor
{
    public IEnumerable<AdminSlotItem> GetItems() =>
        [new(AdminSlots.CustomerEditPanels, typeof(CustomerAddressesPanel), Order: 10), new(AdminSlots.CustomerEditPanels, typeof(CustomerGroupsPanel), Order: 20)];
}
