using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using PiranhaCommerce.Admin.Services;
using PiranhaCommerce.Core.Admin;
using PiranhaCommerce.Core.Identity;
using PiranhaCommerce.Core.Plugins;

namespace PiranhaCommerce.Admin;

public static class PiranhaAdminExtensions
{
    public static PiranhaBuilder AddPiranhaAdmin(this PiranhaBuilder builder)
    {
        var services = builder.Services;
        services.AddRazorComponents().AddInteractiveServerComponents();
        services.AddCascadingAuthenticationState();

        // Cookie auth for the admin UI (the API uses JWT, see PiranhaCommerce.Api).
        services.AddAuthentication(o =>
            {
                o.DefaultScheme = IdentityConstants.ApplicationScheme;
                o.DefaultSignInScheme = IdentityConstants.ExternalScheme;
            })
            .AddIdentityCookies();
        services.ConfigureApplicationCookie(o =>
        {
            o.LoginPath = "/admin/login";
            o.AccessDeniedPath = "/admin/login";
            o.Cookie.Name = ".Piranha.Admin";
            o.ExpireTimeSpan = TimeSpan.FromHours(8);
            o.SlidingExpiration = true;
        });
        new IdentityBuilder(typeof(PiranhaUser), typeof(PiranhaRole), services).AddSignInManager();

        services.AddScoped<AdminMenuService>();
        services.AddSingleton<AdminScopeRunner>();
        return builder;
    }

    /// <summary>
    /// Maps the admin UI under <c>/admin</c>, including pages from every registered <see cref="AdminPageAssembly"/>.
    /// The host must call UseAuthentication, UseAuthorization and UseAntiforgery (in that order) first.
    /// </summary>
    public static IEndpointRouteBuilder MapPiranhaAdmin(this IEndpointRouteBuilder app)
    {
        var pluginAssemblies = app.ServiceProvider.GetServices<AdminPageAssembly>().Select(a => a.Assembly).ToArray();
        app.MapRazorComponents<AdminApp>()
            .AddInteractiveServerRenderMode()
            .AddAdditionalAssemblies(pluginAssemblies);

        // Sign-out must be a plain POST (it clears the cookie). Antiforgery is skipped: logout-CSRF is low impact
        // and the token can't be issued from an interactive layout.
        app.MapPost("/admin/logout", async (SignInManager<PiranhaUser> signIn) =>
        {
            await signIn.SignOutAsync();
            return Results.Redirect("/admin/login");
        }).DisableAntiforgery();
        return app;
    }
}
