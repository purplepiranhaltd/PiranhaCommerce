using Microsoft.EntityFrameworkCore;
using PiranhaCommerce.Core.Plugins;

namespace PiranhaCommerce.Data.PostgreSql;

public static class PostgreSqlExtensions
{
    public static PiranhaBuilder AddPiranhaPostgreSql(this PiranhaBuilder builder, string connectionString)
        => builder.AddPiranhaData(o => o.UseNpgsql(connectionString, npg =>
            npg.MigrationsAssembly(typeof(PostgreSqlExtensions).Assembly.FullName)));
}
