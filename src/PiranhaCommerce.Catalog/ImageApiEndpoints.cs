using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using PiranhaCommerce.Api;

namespace PiranhaCommerce.Catalog;

internal sealed class ImageApiEndpoints : IApiEndpointContributor
{
    public void MapEndpoints(IEndpointRouteBuilder api)
    {
        var g = api.MapGroup("/products/{productId:guid}/images").WithTags("Product images");

        // A variant with no pictures of its own returns its parent's, marked inherited.
        g.MapGet("/", async (Guid productId, IProductImageService svc, CancellationToken ct) =>
            await svc.ListAsync(productId, ct) is { } r ? Results.Ok(r) : Results.NotFound());

        // multipart/form-data: file (required) and altText. Staff calls authenticate with a bearer token, so there is no cookie for antiforgery to protect.
        g.MapPost("/", async (Guid productId, IFormFile file, [FromForm] string? altText, IProductImageService svc, CancellationToken ct) =>
            {
                await using var stream = file.OpenReadStream();
                return await svc.AddAsync(productId, stream, file.FileName, altText, ct) is { } r
                    ? Results.Created($"/api/v1/products/{productId}/images", r) : Results.NotFound();
            })
            .DisableAntiforgery().RequireAuthorization(ApiPolicies.Write);

        g.MapPut("/order", async (Guid productId, ProductImageOrderInput input, IProductImageService svc, CancellationToken ct) =>
                await svc.ReorderAsync(productId, input.ImageIds, ct) is { } r ? Results.Ok(r) : Results.NotFound())
            .RequireAuthorization(ApiPolicies.Write);

        g.MapPut("/{imageId:guid}", async (Guid productId, Guid imageId, ProductImageInput input, IProductImageService svc, CancellationToken ct) =>
                await svc.UpdateAsync(productId, imageId, input, ct) is { } r ? Results.Ok(r) : Results.NotFound())
            .RequireAuthorization(ApiPolicies.Write);

        g.MapDelete("/{imageId:guid}", async (Guid productId, Guid imageId, IProductImageService svc, CancellationToken ct) =>
                await svc.DeleteAsync(productId, imageId, ct) ? Results.NoContent() : Results.NotFound())
            .RequireAuthorization(ApiPolicies.Write);
    }
}

/// <summary>
/// Pictures for shop fronts and browsers: anonymous and cacheable. An image is found by its own unguessable id, so a picture is as private as its address;
/// it is not hidden when its product is unpublished.
/// </summary>
internal sealed class ImageStorefrontEndpoints : IStorefrontEndpointContributor
{
    public void MapEndpoints(IEndpointRouteBuilder storefront)
    {
        storefront.MapGet("/guest/product-images/{imageId:guid}", async (Guid imageId, int? width, HttpContext http, IProductImageService svc, CancellationToken ct) =>
            {
                var file = await svc.OpenAsync(imageId, width, ct);
                if (file is null) return Results.NotFound();
                // The bytes behind an id and a size never change, so they can be cached hard and revalidated by the tag.
                http.Response.Headers.ETag = file.ETag;
                http.Response.Headers.CacheControl = "public, max-age=86400";
                http.Response.Headers["X-Content-Type-Options"] = "nosniff";
                if (http.Request.Headers.IfNoneMatch.Contains(file.ETag))
                {
                    await file.Content.DisposeAsync();
                    return Results.StatusCode(StatusCodes.Status304NotModified);
                }
                return Results.Stream(file.Content, file.ContentType);
            })
            .AllowAnonymous().WithTags("Storefront images");
    }
}
