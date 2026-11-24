using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using PiranhaCommerce.Api;
using PiranhaCommerce.Core.Identity;

namespace PiranhaCommerce.Catalog;

internal sealed class VariantApiEndpoints : IApiEndpointContributor
{
    public void MapEndpoints(IEndpointRouteBuilder api)
    {
        var g = api.MapGroup("/products/{productId:guid}").WithTags("Product variants").RequirePermission(Permissions.CatalogView);

        // For a parent: its options and variants. For a variant: the parent id and which values it is.
        g.MapGet("/variants", async (Guid productId, IProductVariantService svc, CancellationToken ct) =>
            await svc.GetAsync(productId, ct) is { } r ? Results.Ok(r) : Results.NotFound());

        g.MapPost("/options", async (Guid productId, ProductOptionInput input, IProductVariantService svc, CancellationToken ct) =>
                await svc.AddOptionAsync(productId, input, ct) is { } r ? Results.Ok(r) : Results.NotFound())
            .RequirePermission(Permissions.CatalogEdit);
        g.MapPut("/options/{optionId:guid}", async (Guid productId, Guid optionId, ProductOptionRenameInput input, IProductVariantService svc, CancellationToken ct) =>
                await svc.RenameOptionAsync(productId, optionId, input, ct) is { } r ? Results.Ok(r) : Results.NotFound())
            .RequirePermission(Permissions.CatalogEdit);
        g.MapDelete("/options/{optionId:guid}", async (Guid productId, Guid optionId, IProductVariantService svc, CancellationToken ct) =>
                await svc.DeleteOptionAsync(productId, optionId, ct) is { } r ? Results.Ok(r) : Results.NotFound())
            .RequirePermission(Permissions.CatalogEdit);
        g.MapPost("/options/{optionId:guid}/values", async (Guid productId, Guid optionId, ProductOptionValueInput input, IProductVariantService svc, CancellationToken ct) =>
                await svc.AddValueAsync(productId, optionId, input, ct) is { } r ? Results.Ok(r) : Results.NotFound())
            .RequirePermission(Permissions.CatalogEdit);
        g.MapDelete("/options/{optionId:guid}/values/{valueId:guid}", async (Guid productId, Guid optionId, Guid valueId, IProductVariantService svc, CancellationToken ct) =>
                await svc.DeleteValueAsync(productId, optionId, valueId, ct) is { } r ? Results.Ok(r) : Results.NotFound())
            .RequirePermission(Permissions.CatalogEdit);

        g.MapPost("/variants", async (Guid productId, ProductVariantInput input, IProductVariantService svc, CancellationToken ct) =>
                await svc.AddVariantAsync(productId, input, ct) is { } r ? Results.Ok(r) : Results.NotFound())
            .RequirePermission(Permissions.CatalogEdit);
        g.MapPost("/variants/generate", async (Guid productId, GenerateVariantsInput input, IProductVariantService svc, CancellationToken ct) =>
                await svc.GenerateAsync(productId, input, ct) is { } r ? Results.Ok(r) : Results.NotFound())
            .RequirePermission(Permissions.CatalogEdit);
    }
}
