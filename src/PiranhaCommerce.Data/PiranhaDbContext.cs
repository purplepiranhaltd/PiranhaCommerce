using Microsoft.EntityFrameworkCore;
using PiranhaCommerce.Core.Domain;

namespace PiranhaCommerce.Data;

public class PiranhaDbContext(DbContextOptions<PiranhaDbContext> options, IEnumerable<IModelContributor> contributors)
    : DbContext(options)
{
    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        foreach (var contributor in contributors)
            contributor.OnModelCreating(modelBuilder);
    }

    public override Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
    {
        var now = DateTimeOffset.UtcNow;
        foreach (var e in ChangeTracker.Entries<AuditableEntity>())
        {
            if (e.State == EntityState.Added) e.Entity.CreatedUtc = now;
            else if (e.State == EntityState.Modified) e.Entity.UpdatedUtc = now;
        }
        return base.SaveChangesAsync(cancellationToken);
    }
}
