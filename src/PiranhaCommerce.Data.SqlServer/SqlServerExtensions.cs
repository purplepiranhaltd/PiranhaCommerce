using Microsoft.EntityFrameworkCore;
using PiranhaCommerce.Core.Plugins;

namespace PiranhaCommerce.Data.SqlServer;

public static class SqlServerExtensions
{
    /// <summary>SQL Server and Azure SQL (same provider; use an Azure SQL connection string).</summary>
    public static PiranhaBuilder AddPiranhaSqlServer(this PiranhaBuilder builder, string connectionString)
        => builder.AddPiranhaData(o => o.UseSqlServer(connectionString, sql =>
        {
            sql.MigrationsAssembly(typeof(SqlServerExtensions).Assembly.FullName);
            sql.EnableRetryOnFailure(); // transient fault handling for Azure SQL
        }));
}
