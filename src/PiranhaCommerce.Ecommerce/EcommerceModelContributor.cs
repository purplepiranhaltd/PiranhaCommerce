using Microsoft.EntityFrameworkCore;
using PiranhaCommerce.Data;
using PiranhaCommerce.Ecommerce.Catalog;

namespace PiranhaCommerce.Ecommerce;

internal sealed class EcommerceModelContributor : IModelContributor
{
    public void OnModelCreating(ModelBuilder b)
    {
        b.Entity<Product>(e =>
        {
            e.ToTable("Product", "ecom");
            e.Property(p => p.Name).HasMaxLength(400).IsRequired();
            e.Property(p => p.Sku).HasMaxLength(100);
            e.Property(p => p.Price).HasPrecision(18, 4);
            e.HasIndex(p => p.Sku);
        });
    }
}
