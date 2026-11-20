using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using PiranhaCommerce.Api;
using PiranhaCommerce.Data;
using PiranhaCommerce.Ecommerce.Catalog;

namespace PiranhaCommerce.Ecommerce;

internal sealed class EcommerceApiEndpoints : IApiEndpointContributor
{
    public void MapEndpoints(IEndpointRouteBuilder api)
    {
        var products = api.MapGroup("/products").WithTags("Products");
        products.MapGet("/", (PiranhaDbContext db, CancellationToken ct) =>
            db.Set<Product>().AsNoTracking().OrderBy(p => p.Name).ToListAsync(ct));
        products.MapGet("/{id:guid}", async (Guid id, PiranhaDbContext db, CancellationToken ct) =>
            await db.Set<Product>().FindAsync([id], ct) is { } p ? Results.Ok(p) : Results.NotFound());
    }
}
