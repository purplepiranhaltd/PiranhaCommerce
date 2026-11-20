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
    }
}
