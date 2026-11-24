using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using PiranhaCommerce.Api;
using PiranhaCommerce.Core.Identity;

namespace PiranhaCommerce.Catalog;

internal sealed class ImportExportEndpoints : IApiEndpointContributor
{
    public void MapEndpoints(IEndpointRouteBuilder api)
    {
        var g = api.MapGroup("/products").WithTags("Catalogue import and export").RequirePermission(Permissions.CatalogView);

        // The whole catalogue as a CSV file (or the products matching search). Variants follow their parent.
        g.MapGet("/export", (string? search, ICatalogTransferService svc) =>
            Results.Stream(async stream => await svc.ExportProductsAsync(stream, search), "text/csv; charset=utf-8", $"products-{DateTime.UtcNow:yyyyMMdd-HHmm}.csv"));

        // multipart/form-data with a file. dryRun=true only checks. skipInvalid=true saves the good rows even if others are bad.
        g.MapPost("/import", async (IFormFile file, bool? dryRun, bool? skipInvalid, HttpContext http, ICatalogTransferService svc, IStaffLocationService staff, CancellationToken ct) =>
            {
                var actor = http.User.ForStaff();
                var canAdjust = await staff.CanActAtAsync(actor.UserId, StockLocations.DefaultId, ct);
                await using var stream = file.OpenReadStream();
                return Results.Ok(await svc.ImportProductsAsync(stream, new ProductImportRequest(dryRun ?? false, skipInvalid ?? false, actor.Name, canAdjust), ct));
            })
            .DisableAntiforgery().RequirePermission(Permissions.CatalogEdit);
    }
}
