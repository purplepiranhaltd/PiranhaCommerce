using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using PiranhaCommerce.Data;

namespace PiranhaCommerce.IntegrationTests;

public class MigrationsHistoryTests(PiranhaCommerceFactory factory) : IClassFixture<PiranhaCommerceFactory>
{
    private async Task<int> CountAsync(string where)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PiranhaCommerceDbContext>();
        // information_schema exists on SQL Server and PostgreSQL alike. The conditions are fixed text written in this file, never user input.
#pragma warning disable EF1002
        return await db.Database.SqlQueryRaw<int>($"SELECT COUNT(*) AS \"Value\" FROM information_schema.tables WHERE {where}").SingleAsync();
#pragma warning restore EF1002
    }

    [Fact]
    public async Task The_list_of_applied_migrations_is_kept_in_core_Migrations_and_not_in_the_default_table()
    {
        Assert.Equal(1, await CountAsync("table_schema = 'core' AND table_name = 'Migrations'"));
        Assert.Equal(0, await CountAsync("table_name = '__EFMigrationsHistory'"));

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PiranhaCommerceDbContext>();
        Assert.NotEmpty(await db.Database.GetAppliedMigrationsAsync()); // EF reads it from the new place
        Assert.Empty(await db.Database.GetPendingMigrationsAsync());
    }
}
