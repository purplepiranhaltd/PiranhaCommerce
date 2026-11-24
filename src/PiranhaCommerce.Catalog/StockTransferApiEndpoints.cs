using System.Security.Claims;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using PiranhaCommerce.Api;
using PiranhaCommerce.Core.Identity;

namespace PiranhaCommerce.Catalog;

public sealed class StaffLocationsInput
{
    /// <summary>The locations this user is limited to. Empty removes the limit (they can act at every location).</summary>
    public List<Guid> LocationIds { get; set; } = [];
}

/// <summary>Who is calling, from the verified token: the user id and their email.</summary>
public static class StockActors
{
    public static StockActor ForStaff(this ClaimsPrincipal user) =>
        new(Guid.TryParse(user.FindFirstValue("sub") ?? user.FindFirstValue(ClaimTypes.NameIdentifier), out var id) ? id : null,
            user.FindFirstValue("email") ?? user.FindFirstValue(ClaimTypes.Email) ?? user.Identity?.Name);
}

internal sealed class StockTransferApiEndpoints : IApiEndpointContributor
{
    public void MapEndpoints(IEndpointRouteBuilder api)
    {
        var group = api.MapGroup("/stock/transfers").WithTags("Stock transfers").RequirePermission(Permissions.CatalogView);

        group.MapGet("/", async (int? page, int? pageSize, StockTransferStatus? status, Guid? locationId, bool? discrepancies, IStockTransferService svc, CancellationToken ct) =>
            Results.Ok(await svc.ListAsync(page, pageSize, status, locationId, discrepancies, ct)));

        group.MapGet("/{id:guid}", async (Guid id, IStockTransferService svc, CancellationToken ct) =>
            await svc.GetAsync(id, ct) is { } t ? Results.Ok(t) : Results.NotFound());

        // Sending, receiving and cancelling are by a signed-in staff user, who must be allowed at the location involved.
        group.MapPost("/", async (StockTransferInput input, HttpContext http, IStockTransferService svc, CancellationToken ct) =>
            {
                var sent = await svc.SendAsync(input, http.User.ForStaff(), ct);
                return Results.Created($"/api/v1/stock/transfers/{sent.Id}", sent);
            })
            .RequirePermission(Permissions.StockTransfer);

        group.MapPost("/{id:guid}/receive", async (Guid id, StockTransferReceiveInput? input, HttpContext http, IStockTransferService svc, CancellationToken ct) =>
                await svc.ReceiveAsync(id, input ?? new StockTransferReceiveInput(), http.User.ForStaff(), ct) is { } t ? Results.Ok(t) : Results.NotFound())
            .RequirePermission(Permissions.StockTransfer);

        group.MapPost("/{id:guid}/cancel", async (Guid id, HttpContext http, IStockTransferService svc, CancellationToken ct) =>
                await svc.CancelAsync(id, http.User.ForStaff(), ct) is { } t ? Results.Ok(t) : Results.NotFound())
            .RequirePermission(Permissions.StockTransfer);

        // Stock on its way: per product and route.
        api.MapGet("/stock/in-transit", async (IStockTransferService svc, CancellationToken ct) => Results.Ok(await svc.ListInTransitAsync(ct)))
            .WithTags("Stock transfers").RequirePermission(Permissions.CatalogView);

        // Which locations a staff user is limited to. No rows for a user means they can act anywhere.
        var staff = api.MapGroup("/staff").WithTags("Staff locations").RequirePermission(Permissions.StaffManage);
        staff.MapGet("/locations", async (IStaffLocationService svc, CancellationToken ct) => Results.Ok(await svc.ListAsync(ct)));
        staff.MapPut("/{userId:guid}/locations", async (Guid userId, StaffLocationsInput input, IStaffLocationService svc, CancellationToken ct) =>
                await svc.SetAsync(userId, input.LocationIds, ct) is { } u ? Results.Ok(u) : Results.NotFound());
    }
}
