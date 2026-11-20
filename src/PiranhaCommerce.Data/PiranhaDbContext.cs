using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using PiranhaCommerce.Core.Domain;
using PiranhaCommerce.Core.Identity;

namespace PiranhaCommerce.Data;

public class PiranhaDbContext(DbContextOptions<PiranhaDbContext> options, IEnumerable<IModelContributor> contributors)
    : IdentityDbContext<PiranhaUser, PiranhaRole, Guid>(options)
{
    public const string CoreSchema = "core";

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder); // Identity first, then packages/plugins.

        modelBuilder.Entity<PiranhaUser>().ToTable("User", CoreSchema);
        modelBuilder.Entity<PiranhaRole>().ToTable("Role", CoreSchema);
        modelBuilder.Entity<IdentityUserRole<Guid>>().ToTable("UserRole", CoreSchema);
        modelBuilder.Entity<IdentityUserClaim<Guid>>().ToTable("UserClaim", CoreSchema);
        modelBuilder.Entity<IdentityUserLogin<Guid>>().ToTable("UserLogin", CoreSchema);
        modelBuilder.Entity<IdentityUserToken<Guid>>().ToTable("UserToken", CoreSchema);
        modelBuilder.Entity<IdentityRoleClaim<Guid>>().ToTable("RoleClaim", CoreSchema);

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
