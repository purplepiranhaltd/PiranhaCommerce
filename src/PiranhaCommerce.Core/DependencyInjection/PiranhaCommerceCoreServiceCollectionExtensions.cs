using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using PiranhaCommerce.Core.Email;
using PiranhaCommerce.Core.Files;
using PiranhaCommerce.Core.Identity;
using PiranhaCommerce.Core.Plugins;

namespace PiranhaCommerce.Core.DependencyInjection;

public static class PiranhaCommerceCoreServiceCollectionExtensions
{
    public static PiranhaCommerceBuilder AddPiranhaCommerceCore(this IServiceCollection services)
    {
        var builder = new PiranhaCommerceBuilder(services);
        services.TryAddSingleton(builder);
        services.TryAddSingleton<IPluginCatalog, PluginCatalog>();
        services.AddPiranhaCommerceStaffPermissions();
        services.AddPiranhaCommerceEmailDefaults(); // an email plugin registers its own IEmailSender afterwards and wins
        services.AddPiranhaCommerceFileDefaults(); // a storage plugin registers its own IFileStore afterwards and wins
        return builder;
    }

    /// <summary>Staff permissions: the catalogue of them, who holds what, and the policies that check it. Called by <c>AddPiranhaCommerceCore</c>.</summary>
    public static IServiceCollection AddPiranhaCommerceStaffPermissions(this IServiceCollection services)
    {
        services.AddSingleton<IPermissionContributor, BuiltInPermissions>();
        services.TryAddSingleton<PermissionCatalog>();
        services.TryAddSingleton<StaffPermissionService>();
        services.AddOptions<StaffSecurityOptions>().BindConfiguration(StaffSecurityOptions.SectionName);
        services.TryAddScoped<IStaffAdminService, StaffAdminService>();
        services.TryAddScoped<IStaffSecurityService, StaffSecurityService>();
        services.AddAuthorizationCore();
        services.AddSingleton<IAuthorizationHandler, PermissionHandler>();
        services.Replace(ServiceDescriptor.Singleton<IAuthorizationPolicyProvider, PermissionPolicyProvider>());
        return services;
    }
}
