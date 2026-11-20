using Microsoft.EntityFrameworkCore;
using PiranhaCommerce.Catalog;
using PiranhaCommerce.Data;

namespace PiranhaCommerce.Ecommerce;

internal sealed class EcommerceModelContributor : IModelContributor
{
    public const string Schema = "ecom";

    public void OnModelCreating(ModelBuilder b)
    {
        b.Entity<ProductListing>(e =>
        {
            e.ToTable("ProductListing", Schema);
            e.HasKey(l => l.ProductId);
            e.HasOne<Product>().WithOne().HasForeignKey<ProductListing>(l => l.ProductId).OnDelete(DeleteBehavior.Cascade);
        });
    }
}
