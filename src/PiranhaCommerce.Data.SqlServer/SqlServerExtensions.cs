using System.Reflection;
using Microsoft.EntityFrameworkCore;
using PiranhaCommerce.Core.Plugins;

namespace PiranhaCommerce.Data.SqlServer;

public static class SqlServerExtensions
{
    /// <summary>
    /// SQL Server and Azure SQL (same provider; use an Azure SQL connection string).
    /// Migrations live in the host application (it knows which plugins are installed), so
    /// pass the host assembly, e.g. <c>typeof(Program).Assembly</c>.
    /// </summary>
    public static PiranhaBuilder AddPiranhaCommerceSqlServer(this PiranhaBuilder builder, string connectionString, Assembly migrationsAssembly)
        => builder.AddPiranhaCommerceData(o => o.UseSqlServer(connectionString, sql =>
        {
            sql.MigrationsAssembly(migrationsAssembly.GetName().Name);
            sql.EnableRetryOnFailure(); // transient fault handling for Azure SQL
        }));
}

