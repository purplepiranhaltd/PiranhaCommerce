using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using PiranhaCommerce.Core.Plugins;

namespace PiranhaCommerce.Data;

public static class PiranhaDataExtensions
{
    /// <summary>Registers the shared DbContext. Pass a provider configuration, e.g. <c>o =&gt; o.UseSqlServer(cs)</c>, or use the provider packages.</summary>
    public static PiranhaBuilder AddPiranhaData(this PiranhaBuilder builder, Action<DbContextOptionsBuilder> configureProvider)
    {
        builder.Services.AddDbContext<PiranhaDbContext>(configureProvider);
        return builder;
    }
}
