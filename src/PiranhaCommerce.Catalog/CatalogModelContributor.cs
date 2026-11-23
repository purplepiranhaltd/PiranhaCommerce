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
            e.Property(p => p.Slug).HasMaxLength(200);
            e.Property(p => p.MetaTitle).HasMaxLength(200);
            e.Property(p => p.MetaDescription).HasMaxLength(500);
            e.HasIndex(p => p.Slug).IsUnique();
        });

        b.Entity<Category>(e =>
        {
            e.ToTable("Category", Schema);
            e.Property(c => c.Name).HasMaxLength(200).IsRequired();
            e.Property(c => c.Description).HasMaxLength(4000);
            e.Property(c => c.Slug).HasMaxLength(200);
            e.Property(c => c.MetaTitle).HasMaxLength(200);
            e.Property(c => c.MetaDescription).HasMaxLength(500);
            e.HasIndex(c => c.Slug).IsUnique();
            // Optional self-reference: the database keeps NO ACTION (SQL Server forbids cascading self-references);
            // the service refuses to delete a category that still has children.
            e.HasOne<Category>().WithMany().HasForeignKey(c => c.ParentId);
        });

        b.Entity<Brand>(e =>
        {
            e.ToTable("Brand", Schema);
            e.Property(x => x.Name).HasMaxLength(200).IsRequired();
            e.Property(x => x.NormalizedName).HasMaxLength(200).IsRequired();
            e.HasIndex(x => x.NormalizedName).IsUnique(); // names are unique whatever their letter case
            e.Property(x => x.Description).HasMaxLength(4000);
            e.Property(x => x.Slug).HasMaxLength(200);
            e.Property(x => x.MetaTitle).HasMaxLength(200);
            e.Property(x => x.MetaDescription).HasMaxLength(500);
            e.HasIndex(x => x.Slug).IsUnique();
        });

        // A brand that products carry cannot be deleted: the service says so first, and the database refuses too.
        b.Entity<Product>().HasOne<Brand>().WithMany().HasForeignKey(p => p.BrandId).OnDelete(DeleteBehavior.Restrict);
        b.Entity<Product>().HasIndex(p => p.BrandId);

        b.Entity<SpecAttribute>(e =>
        {
            e.ToTable("SpecAttribute", Schema);
            e.Property(x => x.Name).HasMaxLength(200).IsRequired();
            e.Property(x => x.NormalizedName).HasMaxLength(200).IsRequired();
            e.Property(x => x.Slug).HasMaxLength(200).IsRequired();
            e.HasIndex(x => x.NormalizedName).IsUnique();
            e.HasIndex(x => x.Slug).IsUnique();
        });

        b.Entity<SpecValue>(e =>
        {
            e.ToTable("SpecValue", Schema);
            e.Property(x => x.Value).HasMaxLength(200).IsRequired();
            e.Property(x => x.NormalizedValue).HasMaxLength(200).IsRequired();
            e.Property(x => x.Slug).HasMaxLength(200).IsRequired();
            e.HasIndex(x => new { x.AttributeId, x.NormalizedValue }).IsUnique();
            e.HasIndex(x => new { x.AttributeId, x.Slug }).IsUnique();
            // The service deletes an attribute's values with it, after refusing when products have any.
            e.HasOne<SpecAttribute>().WithMany().HasForeignKey(x => x.AttributeId).OnDelete(DeleteBehavior.Restrict);
        });

        b.Entity<ProductSpecValue>(e =>
        {
            e.ToTable("ProductSpecValue", Schema);
            e.HasKey(l => new { l.ProductId, l.ValueId });
            e.HasOne<Product>().WithMany().HasForeignKey(l => l.ProductId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne<SpecValue>().WithMany().HasForeignKey(l => l.ValueId).OnDelete(DeleteBehavior.Restrict);
            e.HasIndex(l => l.ValueId);
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

        b.Entity<ProductImage>(e =>
        {
            e.ToTable("ProductImage", Schema);
            e.Property(x => x.ContentType).HasMaxLength(100).IsRequired();
            e.Property(x => x.FileName).HasMaxLength(200);
            e.Property(x => x.AltText).HasMaxLength(200);
            e.Property(x => x.Sizes).HasMaxLength(100).IsRequired();
            e.Property(x => x.SizeContentType).HasMaxLength(100);
            e.HasIndex(x => new { x.ProductId, x.Position });
            e.HasOne<Product>().WithMany().HasForeignKey(x => x.ProductId).OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<ProductOption>(e =>
        {
            e.ToTable("ProductOption", Schema);
            e.Property(x => x.Name).HasMaxLength(100).IsRequired();
            e.HasIndex(x => x.ProductId);
            e.HasOne<Product>().WithMany().HasForeignKey(x => x.ProductId).OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<ProductOptionValue>(e =>
        {
            e.ToTable("ProductOptionValue", Schema);
            e.Property(x => x.Value).HasMaxLength(100).IsRequired();
            e.HasIndex(x => x.OptionId);
            e.HasOne<ProductOption>().WithMany().HasForeignKey(x => x.OptionId).OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<ProductVariant>(e =>
        {
            e.ToTable("ProductVariant", Schema);
            e.HasKey(x => x.ProductId);
            e.HasIndex(x => x.ParentProductId);
            e.HasOne<Product>().WithMany().HasForeignKey(x => x.ProductId).OnDelete(DeleteBehavior.Cascade);
            // No cascade from the parent: a parent with variants cannot be deleted (the service says so), and SQL Server forbids a second cascade path.
            e.HasOne<Product>().WithMany().HasForeignKey(x => x.ParentProductId).OnDelete(DeleteBehavior.NoAction);
        });

        b.Entity<ProductVariantValue>(e =>
        {
            e.ToTable("ProductVariantValue", Schema);
            e.HasKey(x => new { x.ProductId, x.OptionId });
            e.HasIndex(x => x.OptionValueId);
            e.HasOne<ProductVariant>().WithMany().HasForeignKey(x => x.ProductId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne<ProductOptionValue>().WithMany().HasForeignKey(x => x.OptionValueId).OnDelete(DeleteBehavior.NoAction);
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
