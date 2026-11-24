using Microsoft.Extensions.DependencyInjection;
using PiranhaCommerce.Core.Admin;
using PiranhaCommerce.Core.Plugins;
using PiranhaCommerce.Core.Identity;

namespace PiranhaCommerce.Webhooks.Admin;

/// <summary>The webhooks page (Settings > Webhooks).</summary>
public sealed class WebhooksAdminPlugin : IPiranhaCommercePlugin
{
    public PluginInfo Info { get; } = new("PiranhaCommerce.Webhooks.Admin", "Webhooks Admin", "0.1.0", "Admin page for webhook endpoints and their deliveries.");

    public IReadOnlyList<IPiranhaCommercePlugin> Requires { get; } = [new WebhookPlugin()];

    public void ConfigureServices(IServiceCollection services)
    {
        services.AddSingleton(new AdminPageAssembly(typeof(WebhooksAdminPlugin).Assembly));
        services.AddSingleton<IAdminMenuContributor, WebhooksAdminMenu>();
    }
}

internal sealed class WebhooksAdminMenu : IAdminMenuContributor
{
    public IEnumerable<AdminMenuItem> GetMenuItems() =>
    [
        new("webhooks", "Webhooks", Icon: "plug", Order: 92),
        new("webhooks.endpoints", "Endpoints and deliveries", "/admin/webhooks", Order: 93, ParentKey: "webhooks", RequiredPermission: Permissions.WebhooksManage),
    ];
}
