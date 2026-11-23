using System.Globalization;
using System.Reflection;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Diagnostics;
using PiranhaCommerce.Core.Audit;
using PiranhaCommerce.Core.Paging;
using PiranhaCommerce.Core.Validation;
using PiranhaCommerce.Data;

namespace PiranhaCommerce.Audit;

public enum AuditAction { Created = 0, Updated = 1, Deleted = 2 }

/// <summary>One recorded change. Written in the same transaction as the change itself, so it exists exactly when the change does.</summary>
public class AuditEntry
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public DateTimeOffset WhenUtc { get; set; }
    /// <summary>The signed-in user's email (staff), "shopper" or "shopper:{id}" (storefront), or null when nobody was signed in (background work).</summary>
    public string? Actor { get; set; }
    public AuditAction Action { get; set; }
    /// <summary>The entity's class name, for example "Product".</summary>
    public required string EntityType { get; set; }
    /// <summary>The primary key, several values joined with "/".</summary>
    public required string EntityId { get; set; }
    /// <summary>A name to recognise it by (name, SKU, code or number), when it has one.</summary>
    public string? Label { get; set; }
    /// <summary>JSON list of <see cref="AuditChange"/>. Values of properties marked as private are never kept.</summary>
    public required string Changes { get; set; }
}

public sealed record AuditEntryDto(Guid Id, DateTimeOffset WhenUtc, string? Actor, AuditAction Action, string EntityType, string EntityId, string? Label, IReadOnlyList<AuditChange> Changes);

public interface IAuditService
{
    /// <summary>Newest first. Every filter is optional: the entity type (class name) and id, the actor (exact), and an inclusive range of dates (UTC).</summary>
    Task<PagedResult<AuditEntryDto>> ListAsync(int? page, int? pageSize, string? entityType, string? entityId, string? actor, DateOnly? from, DateOnly? to, CancellationToken ct = default);
}

internal sealed class AuditService(PiranhaDbContext db) : IAuditService
{
    public async Task<PagedResult<AuditEntryDto>> ListAsync(int? page, int? pageSize, string? entityType, string? entityId, string? actor, DateOnly? from, DateOnly? to, CancellationToken ct = default)
    {
        var (p, size) = Paging.Normalize(page, pageSize);
        if (from is { } f && to is { } t && f > t) throw new PiranhaValidationException("from", "The start date is after the end date.");
        var q = db.Set<AuditEntry>().AsNoTracking().AsQueryable();
        if (!string.IsNullOrWhiteSpace(entityType)) { var type = entityType.Trim(); q = q.Where(e => e.EntityType == type); }
        if (!string.IsNullOrWhiteSpace(entityId)) { var id = entityId.Trim(); q = q.Where(e => e.EntityId == id); }
        if (!string.IsNullOrWhiteSpace(actor)) { var a = actor.Trim(); q = q.Where(e => e.Actor == a); }
        if (from is { } start) { var s = new DateTimeOffset(start.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero); q = q.Where(e => e.WhenUtc >= s); }
        if (to is { } end) { var after = new DateTimeOffset(end.AddDays(1).ToDateTime(TimeOnly.MinValue), TimeSpan.Zero); q = q.Where(e => e.WhenUtc < after); }

        var total = await q.CountAsync(ct);
        var rows = await q.OrderByDescending(e => e.WhenUtc).ThenByDescending(e => e.Id).Skip((p - 1) * size).Take(size).ToListAsync(ct);
        var items = rows.Select(e => new AuditEntryDto(e.Id, e.WhenUtc, e.Actor, e.Action, e.EntityType, e.EntityId, e.Label,
            JsonSerializer.Deserialize<List<AuditChange>>(e.Changes) ?? [])).ToList();
        return new PagedResult<AuditEntryDto>(items, p, size, total);
    }
}

/// <summary>Records a change entry for every create, change or delete of an entity marked [Audited], in the same save.</summary>
internal sealed class AuditInterceptor(AuditActor actor) : SaveChangesInterceptor
{
    private const int MaxValue = 300;
    private static readonly string[] LabelProperties = ["Name", "Sku", "Code", "Number", "Reference"];

    public override InterceptionResult<int> SavingChanges(DbContextEventData eventData, InterceptionResult<int> result)
    {
        Record(eventData.Context);
        return result;
    }

    public override ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData, InterceptionResult<int> result, CancellationToken ct = default)
    {
        Record(eventData.Context);
        return ValueTask.FromResult(result);
    }

    private void Record(DbContext? db)
    {
        if (db is null) return;
        var now = DateTimeOffset.UtcNow;
        var found = new List<AuditEntry>();
        foreach (var e in db.ChangeTracker.Entries().Where(e => e.State is EntityState.Added or EntityState.Modified or EntityState.Deleted).ToList())
        {
            var clr = e.Metadata.ClrType;
            if (clr.GetCustomAttribute<AuditedAttribute>() is null) continue;

            var action = e.State switch { EntityState.Added => AuditAction.Created, EntityState.Deleted => AuditAction.Deleted, _ => AuditAction.Updated };
            var changes = new List<AuditChange>();
            foreach (var p in e.Properties)
            {
                var name = p.Metadata.Name;
                if (name is "CreatedUtc" or "UpdatedUtc" || p.Metadata.IsShadowProperty()) continue;
                var hidden = p.Metadata.PropertyInfo?.GetCustomAttribute<AuditMaskAttribute>() is not null;
                if (action == AuditAction.Updated)
                {
                    if (!p.IsModified || Equals(p.OriginalValue, p.CurrentValue)) continue;
                    changes.Add(hidden ? new(name, null, null, true) : new(name, Format(p.OriginalValue), Format(p.CurrentValue)));
                }
                else
                {
                    var value = action == AuditAction.Created ? p.CurrentValue : p.OriginalValue;
                    if (value is null) continue;
                    changes.Add(hidden ? new(name, null, null, true) : action == AuditAction.Created ? new(name, null, Format(value)) : new(name, Format(value), null));
                }
            }
            if (action == AuditAction.Updated && changes.Count == 0) continue;

            var key = e.Metadata.FindPrimaryKey()!.Properties.Select(k => Format(e.Property(k.Name).CurrentValue) ?? "").ToArray();
            found.Add(new AuditEntry
            {
                WhenUtc = now, Actor = actor.Name, Action = action, EntityType = clr.Name, EntityId = string.Join('/', key), Label = Label(e),
                Changes = JsonSerializer.Serialize(changes),
            });
        }
        if (found.Count > 0) db.Set<AuditEntry>().AddRange(found);
    }

    private static string? Label(EntityEntry e)
    {
        foreach (var name in LabelProperties)
            if (e.Metadata.FindProperty(name) is { } prop && prop.PropertyInfo?.GetCustomAttribute<AuditMaskAttribute>() is null)
                if (Format(e.Property(name).CurrentValue) is { Length: > 0 } text) return text.Length > 200 ? text[..200] : text;
        return null;
    }

    private static string? Format(object? value)
    {
        var text = value switch
        {
            null => null,
            DateTimeOffset d => d.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture),
            decimal m => m.ToString("0.############################", CultureInfo.InvariantCulture), // 10.0000 and 10 are the same price
            IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
            _ => value.ToString(),
        };
        return text is { Length: > MaxValue } ? text[..MaxValue] + "…" : text;
    }
}

internal sealed class AuditTrail(PiranhaDbContext db, AuditActor actor) : IAuditTrail
{
    public async Task RecordUpdateAsync(string entityType, string entityId, string? label, IReadOnlyList<AuditChange> changes, CancellationToken ct = default)
    {
        if (changes.Count == 0) return;
        db.Set<AuditEntry>().Add(new AuditEntry
        {
            WhenUtc = DateTimeOffset.UtcNow, Actor = actor.Name, Action = AuditAction.Updated, EntityType = entityType, EntityId = entityId, Label = label,
            Changes = JsonSerializer.Serialize(changes),
        });
        await db.SaveChangesAsync(ct);
    }
}
