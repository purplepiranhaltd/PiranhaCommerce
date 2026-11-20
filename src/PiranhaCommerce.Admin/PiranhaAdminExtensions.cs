using Microsoft.Extensions.DependencyInjection;
using PiranhaCommerce.Admin.Services;
using PiranhaCommerce.Core.Plugins;

namespace PiranhaCommerce.Admin;

public static class PiranhaAdminExtensions
{
    public static PiranhaBuilder AddPiranhaAdmin(this PiranhaBuilder builder)
    {
        builder.Services.AddScoped<AdminMenuService>();
        return builder;
    }
}
