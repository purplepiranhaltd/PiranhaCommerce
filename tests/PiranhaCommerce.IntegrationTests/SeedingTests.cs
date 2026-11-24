using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;
using PiranhaCommerce.Core.Identity;
using PiranhaCommerce.Data;

namespace PiranhaCommerce.IntegrationTests;

public class SeedingTests(PiranhaCommerceFactory factory) : IClassFixture<PiranhaCommerceFactory>
{
    [Fact]
    public async Task Concurrent_admin_seeding_is_safe_and_idempotent()
    {
        var email = $"seed-{Guid.NewGuid():N}@piranhacommerce.local";

        // Several instances starting at once against the same database must all succeed (no duplicate role/user errors).
        await Task.WhenAll(Enumerable.Range(0, 6).Select(_ => factory.Services.EnsureAdministratorAsync(email, "Seed-Passw0rd!")));

        using var scope = factory.Services.CreateScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<PiranhaCommerceUser>>();
        var roles = scope.ServiceProvider.GetRequiredService<RoleManager<PiranhaCommerceRole>>();
        var user = await users.FindByEmailAsync(email);
        Assert.NotNull(user);
        Assert.True(await users.IsInRoleAsync(user, PiranhaCommerceRoles.Administrator));
        Assert.Equal(1, roles.Roles.Count(r => r.Name == PiranhaCommerceRoles.Administrator));
    }
}
