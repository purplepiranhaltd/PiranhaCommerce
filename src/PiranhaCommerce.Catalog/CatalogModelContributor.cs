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

        b.Entity<StockLevel>(e =>
        {
            e.ToTable("StockLevel", Schema);
            e.HasKey(l => l.ProductId);
            e.Property(l => l.Quantity).HasPrecision(18, 4);
            e.HasOne<Product>().WithMany().HasForeignKey(l => l.ProductId).OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<StockMovement>(e =>
        {
            e.ToTable("StockMovement", Schema);
            e.Property(m => m.Delta).HasPrecision(18, 4);
            e.Property(m => m.Reason).HasMaxLength(200).IsRequired();
            e.Property(m => m.Reference).HasMaxLength(200);
            e.Property(m => m.Actor).HasMaxLength(256);
            e.HasIndex(m => new { m.ProductId, m.CreatedUtc });
            // Audit ledger: a product with history cannot be deleted out from under it.
            e.HasOne<Product>().WithMany().HasForeignKey(m => m.ProductId).OnDelete(DeleteBehavior.Restrict);
        });
    }
}
