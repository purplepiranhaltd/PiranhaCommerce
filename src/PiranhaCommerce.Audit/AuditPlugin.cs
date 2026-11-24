using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using PiranhaCommerce.Api;
using PiranhaCommerce.Core.Audit;
using PiranhaCommerce.Core.Plugins;
using PiranhaCommerce.Data;

namespace PiranhaCommerce.Audit;

/// <summary>
/// An audit log of who created, changed or deleted what. Every entity class marked <see cref="AuditedAttribute"/> (products, categories, orders, prices,
/// promotions, tax, shipping and so on) is recorded in the same transaction as the change, with the signed-in user. Install it by adding this plugin.
/// </summary>
public sealed class AuditPlugin : IPiranhaCommercePlugin
{
    public PluginInfo Info { get; } = new("PiranhaCommerce.Audit", "Audit log", "0.1.0", "Who changed what, and when.");

    public void ConfigureServices(IServiceCollection services)
    {
        services.AddSingleton<IModelContributor, AuditModelContributor>();
        services.AddScoped<AuditActor>();
        services.AddScoped<ISaveChangesInterceptor, AuditInterceptor>();
        services.AddScoped<IAuditService, AuditService>();
        services.AddScoped<IAuditTrail, AuditTrail>();
        services.AddSingleton<IApiEndpointContributor, AuditApiEndpoints>();
    }
}

internal sealed class AuditModelContributor : IModelContributor
{
    public void OnModelCreating(ModelBuilder b) => b.Entity<AuditEntry>(e =>
    {
        e.ToTable("AuditEntry", "audit");
        e.Property(a => a.Actor).HasMaxLength(256);
        e.Property(a => a.Action).HasConversion<string>().HasMaxLength(20);
        e.Property(a => a.EntityType).HasMaxLength(100).IsRequired();
        e.Property(a => a.EntityId).HasMaxLength(200).IsRequired();
        e.Property(a => a.Label).HasMaxLength(200);
        e.Property(a => a.Changes).IsRequired();
        e.HasIndex(a => a.WhenUtc);
        e.HasIndex(a => new { a.EntityType, a.EntityId, a.WhenUtc });
        e.HasIndex(a => new { a.Actor, a.WhenUtc });
    });
}

internal sealed class AuditApiEndpoints : IApiEndpointContributor
{
    public void MapEndpoints(IEndpointRouteBuilder api) =>
        // The log names staff and shows what they changed: administrators only.
        api.MapGet("/audit", async (int? page, int? pageSize, string? entityType, string? entityId, string? actor, DateOnly? from, DateOnly? to, IAuditService svc, CancellationToken ct) =>
                Results.Ok(await svc.ListAsync(page, pageSize, entityType, entityId, actor, from, to, ct)))
            .RequireAuthorization(ApiPolicies.Write).WithTags("Audit");
}
