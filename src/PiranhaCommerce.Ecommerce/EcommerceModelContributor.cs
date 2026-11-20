using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
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

        b.Entity<Basket>(e =>
        {
            e.ToTable("Basket", Schema);
            e.Property(x => x.Status).HasConversion<string>().HasMaxLength(20);
            // CustomerId is a plain reference (no foreign key): deleting or erasing a customer must not touch baskets or orders.
            e.HasMany(x => x.Lines).WithOne().HasForeignKey(l => l.BasketId).OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<BasketLine>(e =>
        {
            e.ToTable("BasketLine", Schema);
            e.HasIndex(l => new { l.BasketId, l.ProductId }).IsUnique();
            // A deleted product simply disappears from open baskets.
            e.HasOne<Product>().WithMany().HasForeignKey(l => l.ProductId).OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<Order>(e =>
        {
            e.ToTable("Order", Schema);
            e.HasIndex(o => o.Number).IsUnique();
            e.HasIndex(o => new { o.Stage, o.Number });
            e.HasIndex(o => o.Email);
            e.Property(o => o.Stage).HasConversion<string>().HasMaxLength(20);
            e.Property(o => o.Email).HasMaxLength(256);
            e.Property(o => o.Currency).HasMaxLength(3);
            e.Property(o => o.Notes).HasMaxLength(2000);
            e.Property(o => o.Subtotal).HasPrecision(18, 4);
            e.Property(o => o.Total).HasPrecision(18, 4);
            e.OwnsOne(o => o.ShippingAddress, a => ConfigureAddress(a, "Shipping"));
            e.OwnsOne(o => o.BillingAddress, a => ConfigureAddress(a, "Billing"));
            e.HasMany(o => o.Lines).WithOne().HasForeignKey(l => l.OrderId).OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<OrderLine>(e =>
        {
            e.ToTable("OrderLine", Schema);
            e.HasIndex(l => new { l.OrderId, l.Position }).IsUnique();
            e.Property(l => l.Name).HasMaxLength(200);
            e.Property(l => l.Sku).HasMaxLength(100);
            e.Property(l => l.UnitPrice).HasPrecision(18, 4);
            e.Property(l => l.LineTotal).HasPrecision(18, 4);
            // Restrict: a product that has been sold can never be deleted out from under its orders.
            e.HasOne<Product>().WithMany().HasForeignKey(l => l.ProductId).OnDelete(DeleteBehavior.Restrict);
        });

        b.Entity<OrderCounter>(e =>
        {
            e.ToTable("OrderCounter", Schema);
            e.Property(c => c.Id).ValueGeneratedNever();
            e.HasData(new OrderCounter { Id = 1, LastNumber = OrderNumbers.First - 1 });
        });
    }

    private static void ConfigureAddress(OwnedNavigationBuilder<Order, OrderAddress> a, string prefix)
    {
        a.Property(x => x.Recipient).HasColumnName(prefix + "Recipient").HasMaxLength(200);
        a.Property(x => x.Line1).HasColumnName(prefix + "Line1").HasMaxLength(200);
        a.Property(x => x.Line2).HasColumnName(prefix + "Line2").HasMaxLength(200);
        a.Property(x => x.City).HasColumnName(prefix + "City").HasMaxLength(100);
        a.Property(x => x.Region).HasColumnName(prefix + "Region").HasMaxLength(100);
        a.Property(x => x.PostalCode).HasColumnName(prefix + "PostalCode").HasMaxLength(20);
        a.Property(x => x.CountryCode).HasColumnName(prefix + "CountryCode").HasMaxLength(2);
    }
}
