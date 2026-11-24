using System.ComponentModel.DataAnnotations;
using PiranhaCommerce.Core.Audit;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using PiranhaCommerce.Api;
using PiranhaCommerce.Core.Domain;
using PiranhaCommerce.Core.Paging;
using PiranhaCommerce.Core.Validation;
using PiranhaCommerce.Data;
using PiranhaCommerce.Core.Identity;

namespace PiranhaCommerce.Customers;

/// <summary>
/// A named set of customers (trade, staff, VIP...). Staff put customers in and take them out; other packages use membership to decide who may
/// have something (a promotion limited to a group today). A group carries a percentage off for its members; fixed prices per product live with the product (see Ecommerce group prices).
/// </summary>
[Audited]
public class CustomerGroup : AuditableEntity
{
    public required string Name { get; set; }

    /// <summary>Upper-case form of the name, so the database keeps names unique whatever the letter case.</summary>
    public required string NormalizedName { get; set; }
    public string? Description { get; set; }

    /// <summary>Percentage off every product's regular price for this group's members (0 = none). A fixed price set on a product for the group beats it.</summary>
    public decimal DiscountPercent { get; set; }
}

[Audited]
public class CustomerGroupMember : Entity
{
    public Guid GroupId { get; set; }
    public Guid CustomerId { get; set; }
    public DateTimeOffset AddedUtc { get; set; }
    public string? AddedBy { get; set; }
}

public sealed class CustomerGroupInput
{
    [Required, StringLength(100, MinimumLength = 1)] public string Name { get; set; } = "";
    [StringLength(500)] public string? Description { get; set; }

    /// <summary>Up to 2 decimal places, 0 to 100.</summary>
    [Range(typeof(decimal), "0", "100")] public decimal DiscountPercent { get; set; }
}

public sealed record CustomerGroupDto(Guid Id, string Name, string? Description, int MemberCount, DateTimeOffset CreatedUtc, decimal DiscountPercent = 0m);
public sealed record CustomerGroupRefDto(Guid Id, string Name);
public sealed record CustomerGroupMemberDto(Guid CustomerId, string Email, string? FirstName, string? LastName, DateTimeOffset AddedUtc, string? AddedBy);

public interface ICustomerGroupService
{
    /// <summary>By name, with how many customers each holds.</summary>
    Task<IReadOnlyList<CustomerGroupDto>> ListAsync(CancellationToken ct = default);
    Task<CustomerGroupDto?> GetAsync(Guid id, CancellationToken ct = default);

    /// <summary>Validation error when the name is taken (whatever the letter case).</summary>
    [RequiresPermission(Permissions.CustomersManage)]
    Task<CustomerGroupDto> CreateAsync(CustomerGroupInput input, CancellationToken ct = default);

    /// <summary>Null when not found. Validation error when the new name is another group's.</summary>
    [RequiresPermission(Permissions.CustomersManage)]
    Task<CustomerGroupDto?> UpdateAsync(Guid id, CustomerGroupInput input, CancellationToken ct = default);

    /// <summary>
    /// Deletes the group and its memberships (the customers are untouched). Conflict when something still depends on the group (a promotion
    /// limited to it). False when not found.
    /// </summary>
    [RequiresPermission(Permissions.CustomersManage)]
    Task<bool> DeleteAsync(Guid id, CancellationToken ct = default);

    /// <summary>By email. Null when the group does not exist.</summary>
    Task<PagedResult<CustomerGroupMemberDto>?> ListMembersAsync(Guid groupId, int? page, int? pageSize, CancellationToken ct = default);

    /// <summary>Adds the customer; already a member is fine. Null when the group or customer does not exist.</summary>
    [RequiresPermission(Permissions.CustomersManage)]
    Task<CustomerGroupMemberDto?> AddMemberAsync(Guid groupId, Guid customerId, string? actor = null, CancellationToken ct = default);

    /// <summary>Takes the customer out; not a member is fine. False when the group does not exist.</summary>
    [RequiresPermission(Permissions.CustomersManage)]
    Task<bool> RemoveMemberAsync(Guid groupId, Guid customerId, CancellationToken ct = default);

    /// <summary>The groups a customer belongs to, by name. Null when the customer does not exist.</summary>
    Task<IReadOnlyList<CustomerGroupRefDto>?> GroupsOfAsync(Guid customerId, CancellationToken ct = default);
}

internal sealed class CustomerGroupService(PiranhaCommerceDbContext db) : ICustomerGroupService
{
    private static string Normalize(string name) => name.Trim().ToUpperInvariant();

    private static void CheckPercent(CustomerGroupInput input)
    {
        if (decimal.Round(input.DiscountPercent, 2) != input.DiscountPercent)
            throw new PiranhaCommerceValidationException(nameof(CustomerGroupInput.DiscountPercent), "The percentage can have at most 2 decimal places.");
    }

    public async Task<IReadOnlyList<CustomerGroupDto>> ListAsync(CancellationToken ct = default)
    {
        var groups = await db.Set<CustomerGroup>().AsNoTracking().OrderBy(g => g.NormalizedName).ToListAsync(ct);
        var counts = await db.Set<CustomerGroupMember>().AsNoTracking().GroupBy(m => m.GroupId).Select(g => new { g.Key, Count = g.Count() }).ToDictionaryAsync(x => x.Key, x => x.Count, ct);
        return groups.Select(g => new CustomerGroupDto(g.Id, g.Name, g.Description, counts.GetValueOrDefault(g.Id), g.CreatedUtc, g.DiscountPercent)).ToList();
    }

    public async Task<CustomerGroupDto?> GetAsync(Guid id, CancellationToken ct = default)
    {
        var g = await db.Set<CustomerGroup>().AsNoTracking().FirstOrDefaultAsync(x => x.Id == id, ct);
        if (g is null) return null;
        var count = await db.Set<CustomerGroupMember>().AsNoTracking().CountAsync(m => m.GroupId == id, ct);
        return new CustomerGroupDto(g.Id, g.Name, g.Description, count, g.CreatedUtc, g.DiscountPercent);
    }

    public async Task<CustomerGroupDto> CreateAsync(CustomerGroupInput input, CancellationToken ct = default)
    {
        ModelValidator.ValidateOrThrow(input);
        CheckPercent(input);
        var g = new CustomerGroup { Name = input.Name.Trim(), NormalizedName = Normalize(input.Name), Description = string.IsNullOrWhiteSpace(input.Description) ? null : input.Description.Trim(), DiscountPercent = input.DiscountPercent };
        db.Add(g);
        try { await db.SaveChangesAsync(ct); }
        catch (DbUpdateException)
        {
            db.ChangeTracker.Clear();
            throw new PiranhaCommerceValidationException(nameof(CustomerGroupInput.Name), "There is already a group with that name.");
        }
        return new CustomerGroupDto(g.Id, g.Name, g.Description, 0, g.CreatedUtc, g.DiscountPercent);
    }

    public async Task<CustomerGroupDto?> UpdateAsync(Guid id, CustomerGroupInput input, CancellationToken ct = default)
    {
        ModelValidator.ValidateOrThrow(input);
        CheckPercent(input);
        var g = await db.Set<CustomerGroup>().FirstOrDefaultAsync(x => x.Id == id, ct);
        if (g is null) return null;
        g.DiscountPercent = input.DiscountPercent;
        g.Name = input.Name.Trim(); g.NormalizedName = Normalize(input.Name);
        g.Description = string.IsNullOrWhiteSpace(input.Description) ? null : input.Description.Trim();
        g.UpdatedUtc = DateTimeOffset.UtcNow;
        try { await db.SaveChangesAsync(ct); }
        catch (DbUpdateException)
        {
            db.ChangeTracker.Clear();
            throw new PiranhaCommerceValidationException(nameof(CustomerGroupInput.Name), "There is already a group with that name.");
        }
        return await GetAsync(id, ct);
    }

    public async Task<bool> DeleteAsync(Guid id, CancellationToken ct = default)
    {
        if (!await db.Set<CustomerGroup>().AnyAsync(g => g.Id == id, ct)) return false;
        try { return await db.DeleteTrackedAsync<CustomerGroup>(g => g.Id == id, ct) > 0; } // memberships go with it (cascade)
        catch (Exception ex) when (ex is DbUpdateException or System.Data.Common.DbException) // a set-based delete reports the foreign key as the provider's own exception
        {
            db.ChangeTracker.Clear();
            if (!await db.Set<CustomerGroup>().AnyAsync(g => g.Id == id, ct)) return false; // deleted by someone else meanwhile
            throw new PiranhaCommerceConflictException("Something still uses this group (a promotion limited to it). Remove that first.");
        }
    }

    public async Task<PagedResult<CustomerGroupMemberDto>?> ListMembersAsync(Guid groupId, int? page, int? pageSize, CancellationToken ct = default)
    {
        if (!await db.Set<CustomerGroup>().AnyAsync(g => g.Id == groupId, ct)) return null;
        var (p, size) = Paging.Normalize(page, pageSize);
        var query = from m in db.Set<CustomerGroupMember>().AsNoTracking()
                    join c in db.Set<Customer>().AsNoTracking() on m.CustomerId equals c.Id
                    where m.GroupId == groupId
                    select new { m, c };
        var total = await query.CountAsync(ct);
        var rows = await query.OrderBy(x => x.c.NormalizedEmail).Skip((p - 1) * size).Take(size)
            .Select(x => new CustomerGroupMemberDto(x.c.Id, x.c.Email, x.c.FirstName, x.c.LastName, x.m.AddedUtc, x.m.AddedBy)).ToListAsync(ct);
        return new PagedResult<CustomerGroupMemberDto>(rows, p, size, total);
    }

    public async Task<CustomerGroupMemberDto?> AddMemberAsync(Guid groupId, Guid customerId, string? actor = null, CancellationToken ct = default)
    {
        var customer = await db.Set<Customer>().AsNoTracking().Where(c => c.Id == customerId).Select(c => new { c.Email, c.FirstName, c.LastName }).FirstOrDefaultAsync(ct);
        if (customer is null || !await db.Set<CustomerGroup>().AnyAsync(g => g.Id == groupId, ct)) return null;

        var existing = await db.Set<CustomerGroupMember>().AsNoTracking().FirstOrDefaultAsync(m => m.GroupId == groupId && m.CustomerId == customerId, ct);
        if (existing is null)
        {
            var member = new CustomerGroupMember { GroupId = groupId, CustomerId = customerId, AddedUtc = DateTimeOffset.UtcNow, AddedBy = actor };
            db.Add(member);
            try { await db.SaveChangesAsync(ct); existing = member; }
            catch (DbUpdateException)
            {
                // Added by someone else at the same moment (the pair is unique), or the group or customer was deleted since the check above.
                db.ChangeTracker.Clear();
                existing = await db.Set<CustomerGroupMember>().AsNoTracking().FirstOrDefaultAsync(m => m.GroupId == groupId && m.CustomerId == customerId, ct);
                if (existing is null) return null;
            }
        }
        return new CustomerGroupMemberDto(customerId, customer.Email, customer.FirstName, customer.LastName, existing.AddedUtc, existing.AddedBy);
    }

    public async Task<bool> RemoveMemberAsync(Guid groupId, Guid customerId, CancellationToken ct = default)
    {
        if (!await db.Set<CustomerGroup>().AnyAsync(g => g.Id == groupId, ct)) return false;
        await db.DeleteTrackedAsync<CustomerGroupMember>(m => m.GroupId == groupId && m.CustomerId == customerId, ct);
        return true;
    }

    public async Task<IReadOnlyList<CustomerGroupRefDto>?> GroupsOfAsync(Guid customerId, CancellationToken ct = default)
    {
        if (!await db.Set<Customer>().AnyAsync(c => c.Id == customerId, ct)) return null;
        return await (from m in db.Set<CustomerGroupMember>().AsNoTracking()
                      join g in db.Set<CustomerGroup>().AsNoTracking() on m.GroupId equals g.Id
                      where m.CustomerId == customerId
                      orderby g.NormalizedName
                      select new CustomerGroupRefDto(g.Id, g.Name)).ToListAsync(ct);
    }
}

internal sealed class CustomerGroupEndpoints : IApiEndpointContributor
{
    public void MapEndpoints(IEndpointRouteBuilder api)
    {
        var groups = api.MapGroup("/customer-groups").WithTags("Customer groups").RequirePermission(Permissions.CustomersView);

        groups.MapGet("/", async (ICustomerGroupService svc, CancellationToken ct) => Results.Ok(await svc.ListAsync(ct)));
        groups.MapGet("/{id:guid}", async (Guid id, ICustomerGroupService svc, CancellationToken ct) =>
            await svc.GetAsync(id, ct) is { } g ? Results.Ok(g) : Results.NotFound());
        groups.MapPost("/", async (CustomerGroupInput input, ICustomerGroupService svc, CancellationToken ct) =>
            {
                var created = await svc.CreateAsync(input, ct);
                return Results.Created($"/api/v1/customer-groups/{created.Id}", created);
            }).RequirePermission(Permissions.CustomersManage);
        groups.MapPut("/{id:guid}", async (Guid id, CustomerGroupInput input, ICustomerGroupService svc, CancellationToken ct) =>
            await svc.UpdateAsync(id, input, ct) is { } g ? Results.Ok(g) : Results.NotFound()).RequirePermission(Permissions.CustomersManage);
        groups.MapDelete("/{id:guid}", async (Guid id, ICustomerGroupService svc, CancellationToken ct) =>
            await svc.DeleteAsync(id, ct) ? Results.NoContent() : Results.NotFound()).RequirePermission(Permissions.CustomersManage);

        groups.MapGet("/{id:guid}/members", async (Guid id, int? page, int? pageSize, ICustomerGroupService svc, CancellationToken ct) =>
            await svc.ListMembersAsync(id, page, pageSize, ct) is { } list ? Results.Ok(list) : Results.NotFound());
        groups.MapPut("/{id:guid}/members/{customerId:guid}", async (Guid id, Guid customerId, HttpContext http, ICustomerGroupService svc, CancellationToken ct) =>
            await svc.AddMemberAsync(id, customerId, http.User.FindFirst("email")?.Value, ct) is { } m ? Results.Ok(m) : Results.NotFound()).RequirePermission(Permissions.CustomersManage);
        groups.MapDelete("/{id:guid}/members/{customerId:guid}", async (Guid id, Guid customerId, ICustomerGroupService svc, CancellationToken ct) =>
            await svc.RemoveMemberAsync(id, customerId, ct) ? Results.NoContent() : Results.NotFound()).RequirePermission(Permissions.CustomersManage);

        api.MapGet("/customers/{id:guid}/groups", async (Guid id, ICustomerGroupService svc, CancellationToken ct) =>
            await svc.GroupsOfAsync(id, ct) is { } list ? Results.Ok(list) : Results.NotFound()).WithTags("Customer groups").RequirePermission(Permissions.CustomersView);
    }
}
