using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using PiranhaCommerce.Admin.Services;
using PiranhaCommerce.Api;
using PiranhaCommerce.Catalog;
using PiranhaCommerce.Catalog.Admin;
using PiranhaCommerce.Core.Admin;
using PiranhaCommerce.Core.Plugins;
using PiranhaCommerce.Core.Validation;
using PiranhaCommerce.Data;
using PiranhaCommerce.Core.Identity;

namespace PiranhaCommerce.Example.Warranty;

/// <summary>Warranty terms for a product. Lives in its own schema; deleted with the product.</summary>
public class ProductWarranty
{
    public Guid ProductId { get; set; }
    public int Months { get; set; }
    public string? Terms { get; set; }
}

public sealed class WarrantyInput
{
    [Range(0, 600)] public int Months { get; set; }
    [StringLength(2000)] public string? Terms { get; set; }
}

public sealed record WarrantyDto(Guid ProductId, int Months, string? Terms);

public interface IWarrantyService
{
    /// <summary>Null when the product does not exist; zero months when none was set.</summary>
    Task<WarrantyDto?> GetAsync(Guid productId, CancellationToken ct = default);
    Task<WarrantyDto?> SetAsync(Guid productId, WarrantyInput input, CancellationToken ct = default);
}

internal sealed class WarrantyService(PiranhaCommerceDbContext db) : IWarrantyService
{
    public async Task<WarrantyDto?> GetAsync(Guid productId, CancellationToken ct = default)
    {
        if (!await db.Set<Product>().AnyAsync(p => p.Id == productId, ct)) return null;
        var w = await db.Set<ProductWarranty>().AsNoTracking().FirstOrDefaultAsync(x => x.ProductId == productId, ct);
        return new WarrantyDto(productId, w?.Months ?? 0, w?.Terms);
    }

    public async Task<WarrantyDto?> SetAsync(Guid productId, WarrantyInput input, CancellationToken ct = default)
    {
        ModelValidator.ValidateOrThrow(input);
        if (!await db.Set<Product>().AnyAsync(p => p.Id == productId, ct)) return null;
        var w = await db.Set<ProductWarranty>().FirstOrDefaultAsync(x => x.ProductId == productId, ct);
        if (w is null) db.Add(w = new ProductWarranty { ProductId = productId });
        w.Months = input.Months;
        w.Terms = input.Terms;
        await db.SaveChangesAsync(ct);
        return new WarrantyDto(productId, w.Months, w.Terms);
    }
}

internal sealed class WarrantyModel : IModelContributor
{
    public void OnModelCreating(ModelBuilder b) => b.Entity<ProductWarranty>(e =>
    {
        e.ToTable("ProductWarranty", "warranty");
        e.HasKey(x => x.ProductId);
        e.Property(x => x.Terms).HasMaxLength(2000);
        e.HasOne<Product>().WithOne().HasForeignKey<ProductWarranty>(x => x.ProductId).OnDelete(DeleteBehavior.Cascade);
    });
}

internal sealed class WarrantyEndpoints : IApiEndpointContributor
{
    public void MapEndpoints(IEndpointRouteBuilder api)
    {
        var g = api.MapGroup("/products/{productId:guid}/warranty").WithTags("Warranty").RequirePermission(Permissions.CatalogView);
        g.MapGet("/", async (Guid productId, IWarrantyService svc, CancellationToken ct) =>
            await svc.GetAsync(productId, ct) is { } w ? Results.Ok(w) : Results.NotFound());
        g.MapPut("/", async (Guid productId, WarrantyInput input, IWarrantyService svc, CancellationToken ct) =>
                await svc.SetAsync(productId, input, ct) is { } w ? Results.Ok(w) : Results.NotFound())
            .RequirePermission(WarrantyPermissions.Edit);
    }
}

/// <summary>A plugin adds its own permission like this; administrators hold it automatically, and the role editor lists it.</summary>
internal static class WarrantyPermissions
{
    public const string Edit = "warranty.edit";
}

internal sealed class WarrantyPermissionContributor : IPermissionContributor
{
    public IEnumerable<Permission> GetPermissions() =>
        [new(WarrantyPermissions.Edit, "Edit warranty terms", "Catalogue", "Set the warranty offered on a product.", [Permissions.CatalogView])];
}

internal sealed class WarrantySlots : IAdminSlotContributor
{
    public IEnumerable<AdminSlotItem> GetItems() => [new(AdminSlots.ProductEditPanels, typeof(WarrantyPanel), Order: 20)];
}

/// <summary>Adds a warranty to products: data, API and an admin panel, all contributed through the public plugin contracts.</summary>
public sealed class WarrantyPlugin : IPiranhaCommercePlugin
{
    public PluginInfo Info { get; } = new("PiranhaCommerce.Example.Warranty", "Warranty (example)", "0.1.0", "Example plugin: product warranty terms.");

    public IReadOnlyList<IPiranhaCommercePlugin> Requires { get; } = [new CatalogAdminPlugin()];

    public void ConfigureServices(IServiceCollection services)
    {
        services.AddSingleton<IModelContributor, WarrantyModel>();
        services.AddSingleton<IApiEndpointContributor, WarrantyEndpoints>();
        services.AddSingleton<IPermissionContributor, WarrantyPermissionContributor>();
        services.AddSingleton<IAdminSlotContributor, WarrantySlots>();
        services.AddScoped<IWarrantyService, WarrantyService>();
    }
}
