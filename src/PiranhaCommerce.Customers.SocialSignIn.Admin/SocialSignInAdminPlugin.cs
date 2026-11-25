using Microsoft.Extensions.DependencyInjection;
using PiranhaCommerce.Core.Admin;
using PiranhaCommerce.Core.Plugins;
using PiranhaCommerce.Customers.Admin;
using PiranhaCommerce.Customers.SocialSignIn.Admin.Pages;

namespace PiranhaCommerce.Customers.SocialSignIn.Admin;

/// <summary>Shows a customer's linked social accounts on their page, and lets staff unlink one.</summary>
public sealed class SocialSignInAdminPlugin : IPiranhaCommercePlugin
{
    public PluginInfo Info { get; } = new("PiranhaCommerce.Customers.SocialSignIn.Admin", "Social sign-in Admin", "0.1.0", "Linked social accounts on the customer page.");

    public IReadOnlyList<IPiranhaCommercePlugin> Requires { get; } = [new CustomersAdminPlugin(), new SocialSignInPlugin()];

    public void ConfigureServices(IServiceCollection services)
    {
        services.AddSingleton(new AdminPageAssembly(typeof(SocialSignInAdminPlugin).Assembly));
        services.AddSingleton<IAdminSlotContributor, SocialSlotItems>();
    }
}

internal sealed class SocialSlotItems : IAdminSlotContributor
{
    public IEnumerable<AdminSlotItem> GetItems() => [new(AdminSlots.CustomerEditPanels, typeof(CustomerSocialPanel), Order: 15)];
}
