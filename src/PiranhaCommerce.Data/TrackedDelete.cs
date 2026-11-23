using System.Linq.Expressions;
using Microsoft.EntityFrameworkCore;

namespace PiranhaCommerce.Data;

public static class TrackedDeleteExtensions
{
    /// <summary>
    /// Deletes the matching rows through the change tracker (instead of a set-based DELETE) so that the audit log sees them. Returns how many were deleted;
    /// 0 when they were already gone. A database refusal (the rows are still referenced) is thrown as <see cref="DbUpdateException"/> with the rows left as they were.
    /// </summary>
    public static async Task<int> DeleteTrackedAsync<T>(this DbContext db, Expression<Func<T, bool>> where, CancellationToken ct = default) where T : class
    {
        var rows = await db.Set<T>().Where(where).ToListAsync(ct);
        if (rows.Count == 0) return 0;
        db.RemoveRange(rows);
        try
        {
            await db.SaveChangesAsync(ct);
            return rows.Count;
        }
        catch (DbUpdateConcurrencyException)
        {
            Detach(db, rows);
            return 0; // someone else deleted them first
        }
        catch (DbUpdateException)
        {
            Detach(db, rows);
            throw;
        }
    }

    /// <summary>
    /// Changes the matching rows through the change tracker (instead of a set-based UPDATE) so that the audit log sees what changed. Returns how many matched;
    /// 0 when there were none (or they were deleted meanwhile).
    /// </summary>
    public static async Task<int> UpdateTrackedAsync<T>(this DbContext db, Expression<Func<T, bool>> where, Action<T> change, CancellationToken ct = default) where T : class
    {
        var rows = await db.Set<T>().Where(where).ToListAsync(ct);
        if (rows.Count == 0) return 0;
        foreach (var r in rows) change(r);
        try
        {
            await db.SaveChangesAsync(ct);
            return rows.Count;
        }
        catch (DbUpdateConcurrencyException)
        {
            Detach(db, rows);
            return 0;
        }
    }

    private static void Detach<T>(DbContext db, List<T> rows) where T : class
    {
        foreach (var r in rows) db.Entry(r).State = EntityState.Detached;
        // Audit rows added for the failed save must not be saved by a later one.
        foreach (var e in db.ChangeTracker.Entries().Where(e => e.State == EntityState.Added && e.Metadata.ClrType.Name == "AuditEntry").ToList()) e.State = EntityState.Detached;
    }
}
