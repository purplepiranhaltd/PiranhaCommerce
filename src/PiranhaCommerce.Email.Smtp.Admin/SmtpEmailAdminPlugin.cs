using Microsoft.Extensions.DependencyInjection;
using PiranhaCommerce.Core.Admin;
using PiranhaCommerce.Core.Plugins;
using PiranhaCommerce.Core.Identity;

namespace PiranhaCommerce.Email.Smtp.Admin;

/// <summary>Admin section for editing the wording of the emails the SMTP sender sends. Not needed (and not offered) with a provider that keeps its templates itself.</summary>
public sealed class SmtpEmailAdminPlugin : IPiranhaCommercePlugin
{
    public PluginInfo Info { get; } = new("PiranhaCommerce.Email.Smtp.Admin", "SMTP email Admin", "0.1.0", "Edit the wording of platform emails.");

    public IReadOnlyList<IPiranhaCommercePlugin> Requires { get; } = [new SmtpEmailPlugin()];

    public void ConfigureServices(IServiceCollection services)
    {
        services.AddSingleton(new AdminPageAssembly(typeof(SmtpEmailAdminPlugin).Assembly));
        services.AddSingleton<IAdminMenuContributor, SmtpEmailAdminMenu>();
    }
}

internal sealed class SmtpEmailAdminMenu : IAdminMenuContributor
{
    public IEnumerable<AdminMenuItem> GetMenuItems() =>
    [
        new("email", "Email", Icon: "mail", Order: 40),
        new("email.templates", "Email templates", "/admin/email/templates", Order: 41, ParentKey: "email", RequiredPermission: Permissions.Access),
    ];
}
