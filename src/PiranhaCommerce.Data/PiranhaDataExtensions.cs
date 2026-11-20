using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using PiranhaCommerce.Core.Identity;
using PiranhaCommerce.Core.Plugins;

namespace PiranhaCommerce.Data;

public static class PiranhaDataExtensions
{
    /// <summary>
    /// Registers the shared DbContext and Identity stores. Pass a provider configuration,
    /// e.g. <c>o =&gt; o.UseSqlServer(cs)</c>, or use the provider packages.
    /// </summary>
    public static PiranhaBuilder AddPiranhaData(this PiranhaBuilder builder, Action<DbContextOptionsBuilder> configureProvider)
    {
        builder.Services.AddDbContext<PiranhaDbContext>(configureProvider);
        builder.Services.AddIdentityCore<PiranhaUser>(o =>
            {
                o.Password.RequiredLength = 10;
                o.Lockout.MaxFailedAccessAttempts = 5;
                o.User.RequireUniqueEmail = true;
            })
            .AddRoles<PiranhaRole>()
            .AddEntityFrameworkStores<PiranhaDbContext>();
        return builder;
    }

    /// <summary>Creates the Administrator role and an admin user if missing. Intended for development/first-run only.</summary>
    public static async Task EnsureAdministratorAsync(this IServiceProvider services, string email, string password)
    {
        using var scope = services.CreateScope();
        var roles = scope.ServiceProvider.GetRequiredService<RoleManager<PiranhaRole>>();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<PiranhaUser>>();

        if (!await roles.RoleExistsAsync(PiranhaRoles.Administrator))
            Check(await roles.CreateAsync(new PiranhaRole(PiranhaRoles.Administrator)));

        var user = await users.FindByEmailAsync(email);
        if (user is null)
        {
            user = new PiranhaUser { UserName = email, Email = email, EmailConfirmed = true, DisplayName = "Administrator" };
            Check(await users.CreateAsync(user, password));
        }
        if (!await users.IsInRoleAsync(user, PiranhaRoles.Administrator))
            Check(await users.AddToRoleAsync(user, PiranhaRoles.Administrator));

        static void Check(IdentityResult r)
        {
            if (!r.Succeeded)
                throw new InvalidOperationException("Admin seeding failed: " + string.Join("; ", r.Errors.Select(e => e.Description)));
        }
    }
}
