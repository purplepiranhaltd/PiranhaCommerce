using Microsoft.Extensions.DependencyInjection;
using PiranhaCommerce.Core.Admin;
using PiranhaCommerce.Core.Plugins;

namespace PiranhaCommerce.Webhooks.Admin;

/// <summary>The webhooks page (Settings > Webhooks).</summary>
public sealed class WebhooksAdminPlugin : IPiranhaPlugin
{
    public PluginInfo Info { get; } = new("PiranhaCommerce.Webhooks.Admin", "Webhooks Admin", "0.1.0", "Admin page for webhook endpoints and their deliveries.");

    public IReadOnlyList<IPiranhaPlugin> Requires { get; } = [new WebhookPlugin()];

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
        new("webhooks.endpoints", "Endpoints and deliveries", "/admin/webhooks", Order: 93, ParentKey: "webhooks"),
    ];
}
