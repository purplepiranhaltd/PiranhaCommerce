using Microsoft.EntityFrameworkCore;
using PiranhaCommerce.Data;

namespace PiranhaCommerce.Catalog;

internal sealed class CatalogModelContributor : IModelContributor
{
    public const string Schema = "catalog";

    public void OnModelCreating(ModelBuilder b)
    {
        b.Entity<Product>(e =>
        {
            e.ToTable("Product", Schema);
            e.Property(p => p.Name).HasMaxLength(400).IsRequired();
            e.Property(p => p.Sku).HasMaxLength(100);
            e.Property(p => p.Price).HasPrecision(18, 4);
            e.HasIndex(p => p.Sku).IsUnique(); // the guarantee; the service pre-check only gives a friendlier 400
        });

        b.Entity<Category>(e =>
        {
            e.ToTable("Category", Schema);
            e.Property(c => c.Name).HasMaxLength(200).IsRequired();
            e.Property(c => c.Description).HasMaxLength(4000);
            // Optional self-reference: the database keeps NO ACTION (SQL Server forbids cascading self-references);
            // the service refuses to delete a category that still has children.
            e.HasOne<Category>().WithMany().HasForeignKey(c => c.ParentId);
        });

        b.Entity<ProductCategory>(e =>
        {
            e.ToTable("ProductCategory", Schema);
            e.HasKey(l => new { l.ProductId, l.CategoryId });
            e.HasOne<Product>().WithMany().HasForeignKey(l => l.ProductId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne<Category>().WithMany().HasForeignKey(l => l.CategoryId).OnDelete(DeleteBehavior.Cascade);
            e.HasIndex(l => l.CategoryId);
        });

        b.Entity<ProductBarcode>(e =>
        {
            e.ToTable("ProductBarcode", Schema);
            e.Property(x => x.Code).HasMaxLength(BarcodeNormalizer.MaxLength).IsRequired();
            e.Property(x => x.NormalizedCode).HasMaxLength(BarcodeNormalizer.MaxLength).IsRequired();
            e.HasIndex(x => x.NormalizedCode).IsUnique(); // a code identifies one product, on every provider
            e.HasIndex(x => x.ProductId);
            e.HasOne<Product>().WithMany().HasForeignKey(x => x.ProductId).OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<StockLocation>(e =>
        {
            e.ToTable("StockLocation", Schema);
            e.Property(l => l.Code).HasMaxLength(30).IsRequired();
            e.Property(l => l.Name).HasMaxLength(200).IsRequired();
            e.HasIndex(l => l.Code).IsUnique(); // stored upper-case, so this is case-insensitive on every provider
            e.HasData(new StockLocation { Id = StockLocations.DefaultId, Code = StockLocations.DefaultCode, Name = "Main", Active = true });
        });

        b.Entity<StockLevel>(e =>
        {
            e.ToTable("StockLevel", Schema);
            e.HasKey(l => new { l.ProductId, l.LocationId });
            e.Property(l => l.Quantity).HasPrecision(18, 4);
            e.HasOne<Product>().WithMany().HasForeignKey(l => l.ProductId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne<StockLocation>().WithMany().HasForeignKey(l => l.LocationId).OnDelete(DeleteBehavior.Restrict);
            e.HasIndex(l => l.Quantity); // the negative stock report
        });

        b.Entity<StockTransfer>(e =>
        {
            e.ToTable("StockTransfer", Schema);
            e.HasIndex(t => t.Number).IsUnique();
            e.HasIndex(t => new { t.Status, t.Number });
            e.Property(t => t.Note).HasMaxLength(500);
            e.Property(t => t.SentBy).HasMaxLength(256);
            e.Property(t => t.ReceivedBy).HasMaxLength(256);
            e.Property(t => t.CancelledBy).HasMaxLength(256);
            e.HasOne<StockLocation>().WithMany().HasForeignKey(t => t.FromLocationId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<StockLocation>().WithMany().HasForeignKey(t => t.ToLocationId).OnDelete(DeleteBehavior.Restrict);
            e.HasMany(t => t.Lines).WithOne().HasForeignKey(l => l.TransferId).OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<StockTransferLine>(e =>
        {
            e.ToTable("StockTransferLine", Schema);
            e.HasKey(l => new { l.TransferId, l.ProductId });
            e.Property(l => l.ProductName).HasMaxLength(200).IsRequired();
            e.Property(l => l.Sku).HasMaxLength(100);
            e.Property(l => l.QuantitySent).HasPrecision(18, 4);
            e.Property(l => l.QuantityReceived).HasPrecision(18, 4);
            // The transfer is a record of what moved: a product that was transferred cannot be deleted out from under it.
            e.HasOne<Product>().WithMany().HasForeignKey(l => l.ProductId).OnDelete(DeleteBehavior.Restrict);
        });

        b.Entity<StockTransferCounter>(e =>
        {
            e.ToTable("StockTransferCounter", Schema);
            e.HasKey(c => c.Id);
            e.Property(c => c.Id).ValueGeneratedNever();
            e.HasData(new StockTransferCounter { Id = 1, LastNumber = StockTransferNumbers.First - 1 });
        });

        b.Entity<StaffLocation>(e =>
        {
            e.ToTable("StaffLocation", Schema);
            e.HasKey(x => new { x.UserId, x.LocationId });
            e.HasOne<PiranhaCommerce.Core.Identity.PiranhaUser>().WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne<StockLocation>().WithMany().HasForeignKey(x => x.LocationId).OnDelete(DeleteBehavior.Restrict);
        });

        b.Entity<StockMovement>(e =>
        {
            e.ToTable("StockMovement", Schema);
            e.Property(m => m.Delta).HasPrecision(18, 4);
            e.Property(m => m.Balance).HasPrecision(18, 4);
            e.HasOne<StockLocation>().WithMany().HasForeignKey(m => m.LocationId).OnDelete(DeleteBehavior.Restrict);
            e.Property(m => m.Reason).HasMaxLength(200).IsRequired();
            e.Property(m => m.Reference).HasMaxLength(200);
            e.Property(m => m.Actor).HasMaxLength(256);
            e.HasIndex(m => new { m.ProductId, m.CreatedUtc });
            // Audit ledger: a product with history cannot be deleted out from under it.
            e.HasOne<Product>().WithMany().HasForeignKey(m => m.ProductId).OnDelete(DeleteBehavior.Restrict);
        });
    }
}
