using Microsoft.Extensions.DependencyInjection;
using PiranhaCommerce.Core.Admin;
using PiranhaCommerce.Core.Plugins;

namespace PiranhaCommerce.Audit.Admin;

/// <summary>The audit log page (Settings > Audit log).</summary>
public sealed class AuditAdminPlugin : IPiranhaPlugin
{
    public PluginInfo Info { get; } = new("PiranhaCommerce.Audit.Admin", "Audit log Admin", "0.1.0", "Admin page for the audit log.");

    public IReadOnlyList<IPiranhaPlugin> Requires { get; } = [new AuditPlugin()];

    public void ConfigureServices(IServiceCollection services)
    {
        services.AddSingleton(new AdminPageAssembly(typeof(AuditAdminPlugin).Assembly));
        services.AddSingleton<IAdminMenuContributor, AuditAdminMenu>();
    }
}

internal sealed class AuditAdminMenu : IAdminMenuContributor
{
    public IEnumerable<AdminMenuItem> GetMenuItems() =>
    [
        new("audit", "Audit log", Icon: "shield", Order: 90),
        new("audit.log", "Who changed what", "/admin/audit", Order: 91, ParentKey: "audit"),
    ];
}
