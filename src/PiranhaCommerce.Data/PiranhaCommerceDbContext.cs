using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using PiranhaCommerce.Core.Domain;
using PiranhaCommerce.Core.Identity;

namespace PiranhaCommerce.Data;

public class PiranhaCommerceDbContext(DbContextOptions<PiranhaCommerceDbContext> options, IEnumerable<IModelContributor> contributors)
    : IdentityDbContext<PiranhaCommerceUser, PiranhaCommerceRole, Guid>(options)
{
    public const string CoreSchema = "core";

    /// <summary>EF Core keeps the list of applied migrations here (core.Migrations), not in the default dbo.__EFMigrationsHistory, so the schema holds only our own tables.</summary>
    public const string MigrationsHistoryTable = "Migrations";

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder); // Identity first, then packages/plugins.

        modelBuilder.Entity<PiranhaCommerceUser>().ToTable("User", CoreSchema);
        modelBuilder.Entity<PiranhaCommerceRole>().ToTable("Role", CoreSchema);
        modelBuilder.Entity<IdentityUserRole<Guid>>().ToTable("UserRole", CoreSchema);
        modelBuilder.Entity<IdentityUserClaim<Guid>>().ToTable("UserClaim", CoreSchema);
        modelBuilder.Entity<IdentityUserLogin<Guid>>().ToTable("UserLogin", CoreSchema);
        modelBuilder.Entity<IdentityUserToken<Guid>>().ToTable("UserToken", CoreSchema);
        modelBuilder.Entity<IdentityRoleClaim<Guid>>().ToTable("RoleClaim", CoreSchema);

        modelBuilder.Entity<ApiKey>(b =>
        {
            b.ToTable("ApiKey", CoreSchema);
            b.HasKey(k => k.Id);
            b.Property(k => k.Name).HasMaxLength(100).IsRequired();
            b.Property(k => k.Prefix).HasMaxLength(16).IsRequired();
            b.Property(k => k.SecretHash).HasMaxLength(64).IsRequired();
            b.HasIndex(k => k.Prefix);
            b.HasIndex(k => k.UserId);
        });

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
