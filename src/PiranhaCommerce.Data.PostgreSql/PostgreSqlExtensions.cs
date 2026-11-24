using System.Reflection;
using Microsoft.EntityFrameworkCore;
using PiranhaCommerce.Core.Plugins;

namespace PiranhaCommerce.Data.PostgreSql;

public static class PostgreSqlExtensions
{
    /// <summary>Migrations live in the host application; pass the host assembly, e.g. <c>typeof(Program).Assembly</c>.</summary>
    public static PiranhaBuilder AddPiranhaCommercePostgreSql(this PiranhaBuilder builder, string connectionString, Assembly migrationsAssembly)
        => builder.AddPiranhaCommerceData(o => o.UseNpgsql(connectionString, npg =>
            npg.MigrationsAssembly(migrationsAssembly.GetName().Name)));
}

