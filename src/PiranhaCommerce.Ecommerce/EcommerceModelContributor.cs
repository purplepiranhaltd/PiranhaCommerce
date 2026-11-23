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
        b.Entity<ProductPriceBreak>(e =>
        {
            e.ToTable("ProductPriceBreak", Schema);
            e.Property(x => x.Price).HasPrecision(18, 4);
            e.Property(x => x.Percent).HasPrecision(5, 2);
            e.HasIndex(x => new { x.ProductId, x.GroupId, x.MinQuantity }).IsUnique();
            e.HasOne<PiranhaCommerce.Catalog.Product>().WithMany().HasForeignKey(x => x.ProductId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne<PiranhaCommerce.Customers.CustomerGroup>().WithMany().HasForeignKey(x => x.GroupId).OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<GroupPrice>(e =>
        {
            e.ToTable("GroupPrice", Schema);
            e.HasKey(p => new { p.GroupId, p.ProductId });
            e.Property(p => p.Price).HasPrecision(18, 4);
            e.HasOne<PiranhaCommerce.Customers.CustomerGroup>().WithMany().HasForeignKey(p => p.GroupId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne<Product>().WithMany().HasForeignKey(p => p.ProductId).OnDelete(DeleteBehavior.Cascade);
            e.HasIndex(p => p.ProductId);
        });

        b.Entity<Sale>(e =>
        {
            e.ToTable("Sale", Schema);
            e.Property(x => x.Name).HasMaxLength(200);
            e.Property(x => x.Percent).HasPrecision(5, 2);
            e.HasIndex(x => new { x.Enabled, x.StartsUtc, x.EndsUtc });
        });
        b.Entity<SaleProduct>(e =>
        {
            e.ToTable("SaleProduct", Schema);
            e.HasKey(x => new { x.SaleId, x.ProductId });
            e.Property(x => x.Price).HasPrecision(18, 4);
            e.HasOne<Sale>().WithMany().HasForeignKey(x => x.SaleId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne<Product>().WithMany().HasForeignKey(x => x.ProductId).OnDelete(DeleteBehavior.Cascade);
            e.HasIndex(x => x.ProductId);
        });
        b.Entity<SaleCategory>(e =>
        {
            e.ToTable("SaleCategory", Schema);
            e.HasKey(x => new { x.SaleId, x.CategoryId });
            e.HasOne<Sale>().WithMany().HasForeignKey(x => x.SaleId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne<Category>().WithMany().HasForeignKey(x => x.CategoryId).OnDelete(DeleteBehavior.Cascade);
            e.HasIndex(x => x.CategoryId);
        });

        b.Entity<GroupCategoryDiscount>(e =>
        {
            e.ToTable("GroupCategoryDiscount", Schema);
            e.HasKey(d => new { d.GroupId, d.CategoryId });
            e.Property(d => d.Percent).HasPrecision(5, 2);
            e.HasOne<PiranhaCommerce.Customers.CustomerGroup>().WithMany().HasForeignKey(d => d.GroupId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne<Category>().WithMany().HasForeignKey(d => d.CategoryId).OnDelete(DeleteBehavior.Cascade);
            e.HasIndex(d => d.CategoryId);
        });

        b.Entity<ProductListing>(e =>
        {
            e.ToTable("ProductListing", Schema);
            e.HasKey(l => l.ProductId);
            e.HasOne<Product>().WithOne().HasForeignKey<ProductListing>(l => l.ProductId).OnDelete(DeleteBehavior.Cascade);
            // Restrict: a class that products still use cannot be deleted (the service says so first).
            e.HasOne<TaxClass>().WithMany().HasForeignKey(l => l.TaxClassId).OnDelete(DeleteBehavior.Restrict);
        });

        b.Entity<Basket>(e =>
        {
            e.ToTable("Basket", Schema);
            e.Property(x => x.Status).HasConversion<string>().HasMaxLength(20);
            e.Property(x => x.PromotionCode).HasMaxLength(40);
            // VoucherId is a plain reference too: checkout re-checks the voucher, and vouchers are never deleted.
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
            e.Property(o => o.ShippingMethodName).HasMaxLength(100);
            e.Property(o => o.ShippingTotal).HasPrecision(18, 4);
            e.Property(o => o.PromotionCode).HasMaxLength(40);
            e.Property(o => o.DiscountTotal).HasPrecision(18, 4);
            e.Property(o => o.VoucherPaid).HasPrecision(18, 4);
            e.Property(o => o.TaxName).HasMaxLength(50);
            e.Property(o => o.TaxRate).HasPrecision(9, 4);
            e.Property(o => o.TaxTotal).HasPrecision(18, 4);
            e.Property(o => o.ShippingTax).HasPrecision(18, 4);
            e.Property(o => o.ShippingTaxName).HasMaxLength(50);
            e.Property(o => o.ShippingTaxRate).HasPrecision(9, 4);
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
            e.Property(l => l.DiscountAmount).HasPrecision(18, 4);
            e.Property(l => l.TaxName).HasMaxLength(50);
            e.Property(l => l.TaxRate).HasPrecision(9, 4);
            e.Property(l => l.TaxAmount).HasPrecision(18, 4);
            // Restrict: a product that has been sold can never be deleted out from under its orders.
            e.HasOne<Product>().WithMany().HasForeignKey(l => l.ProductId).OnDelete(DeleteBehavior.Restrict);
        });

        b.Entity<Shipment>(e =>
        {
            e.ToTable("Shipment", Schema);
            e.HasIndex(s => new { s.OrderId, s.ShippedUtc });
            e.Property(s => s.Carrier).HasMaxLength(100);
            e.Property(s => s.TrackingNumber).HasMaxLength(100);
            e.Property(s => s.TrackingUrl).HasMaxLength(500);
            e.Property(s => s.Actor).HasMaxLength(256);
            // Restrict: shipping records are history and never go with anything else.
            e.HasOne<Order>().WithMany().HasForeignKey(s => s.OrderId).OnDelete(DeleteBehavior.Restrict);
            e.HasMany(s => s.Lines).WithOne().HasForeignKey(l => l.ShipmentId).OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<ShipmentLine>(e =>
        {
            e.ToTable("ShipmentLine", Schema);
            e.HasOne<OrderLine>().WithMany().HasForeignKey(l => l.OrderLineId).OnDelete(DeleteBehavior.Restrict);
            e.HasIndex(l => l.OrderLineId);
        });

        b.Entity<OrderReturn>(e =>
        {
            e.ToTable("OrderReturn", Schema);
            e.HasIndex(r => new { r.OrderId, r.CreatedUtc });
            e.Property(r => r.Reason).HasMaxLength(200);
            e.Property(r => r.Note).HasMaxLength(1000);
            e.Property(r => r.Actor).HasMaxLength(256);
            e.Property(r => r.SuggestedRefund).HasPrecision(18, 4);
            e.HasOne<Order>().WithMany().HasForeignKey(r => r.OrderId).OnDelete(DeleteBehavior.Restrict);
            e.HasMany(r => r.Lines).WithOne().HasForeignKey(l => l.OrderReturnId).OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<OrderReturnLine>(e =>
        {
            e.ToTable("OrderReturnLine", Schema);
            e.HasOne<OrderLine>().WithMany().HasForeignKey(l => l.OrderLineId).OnDelete(DeleteBehavior.Restrict);
            e.HasIndex(l => l.OrderLineId);
        });

        b.Entity<ReturnRequest>(e =>
        {
            e.ToTable("ReturnRequest", Schema);
            e.HasIndex(r => new { r.OrderId, r.Status });
            e.HasIndex(r => new { r.Status, r.RequestedUtc });
            e.Property(r => r.Status).HasConversion<string>().HasMaxLength(20);
            e.Property(r => r.Reason).HasMaxLength(200);
            e.Property(r => r.Note).HasMaxLength(1000);
            e.Property(r => r.ResponseNote).HasMaxLength(1000);
            e.Property(r => r.RespondedBy).HasMaxLength(256);
            e.HasOne<Order>().WithMany().HasForeignKey(r => r.OrderId).OnDelete(DeleteBehavior.Restrict);
            e.HasMany(r => r.Lines).WithOne().HasForeignKey(l => l.ReturnRequestId).OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<ReturnRequestLine>(e =>
        {
            e.ToTable("ReturnRequestLine", Schema);
            e.HasOne<OrderLine>().WithMany().HasForeignKey(l => l.OrderLineId).OnDelete(DeleteBehavior.Restrict);
            e.HasIndex(l => l.OrderLineId);
        });

        b.Entity<Payment>(e =>
        {
            e.ToTable("Payment", Schema);
            e.HasIndex(p => new { p.OrderId, p.CreatedUtc });
            e.HasIndex(p => new { p.Provider, p.Reference }).IsUnique();
            e.Property(p => p.Kind).HasConversion<string>().HasMaxLength(10);
            e.Property(p => p.Provider).HasMaxLength(50);
            e.Property(p => p.Amount).HasPrecision(18, 4);
            e.Property(p => p.Currency).HasMaxLength(3);
            e.Property(p => p.Reference).HasMaxLength(200);
            e.Property(p => p.Note).HasMaxLength(500);
            e.Property(p => p.Actor).HasMaxLength(256);
            // Restrict: money records are never removed with anything else.
            e.HasOne<Order>().WithMany().HasForeignKey(p => p.OrderId).OnDelete(DeleteBehavior.Restrict);
        });

        b.Entity<ShippingMethod>(e =>
        {
            e.ToTable("ShippingMethod", Schema);
            e.Property(m => m.Name).HasMaxLength(100);
            e.Property(m => m.Price).HasPrecision(18, 4);
            e.Property(m => m.FreeOver).HasPrecision(18, 4);
        });

        b.Entity<ShippingRate>(e =>
        {
            e.ToTable("ShippingRate", Schema);
            e.Property(r => r.CountryCode).HasMaxLength(2).IsRequired();
            e.Property(r => r.Price).HasPrecision(18, 4);
            e.HasIndex(r => new { r.ShippingMethodId, r.CountryCode });
            e.HasOne<ShippingMethod>().WithMany().HasForeignKey(r => r.ShippingMethodId).OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<Promotion>(e =>
        {
            e.ToTable("Promotion", Schema);
            e.Property(p => p.Code).HasMaxLength(40);
            e.HasIndex(p => p.Code).IsUnique();
            e.Property(p => p.Name).HasMaxLength(100);
            e.Property(p => p.Kind).HasConversion<string>().HasMaxLength(10);
            e.Property(p => p.Value).HasPrecision(18, 4);
            e.Property(p => p.MinSubtotal).HasPrecision(18, 4);
        });

        b.Entity<UnreconciledPayment>(e =>
        {
            e.ToTable("UnreconciledPayment", Schema);
            e.Property(u => u.Provider).HasMaxLength(50);
            e.Property(u => u.Kind).HasConversion<string>().HasMaxLength(20);
            e.Property(u => u.Amount).HasPrecision(18, 4);
            e.Property(u => u.Currency).HasMaxLength(3);
            e.Property(u => u.Reference).HasMaxLength(200);
            e.Property(u => u.Reason).HasMaxLength(500);
            e.Property(u => u.ResolvedBy).HasMaxLength(256);
            e.Property(u => u.Resolution).HasMaxLength(500);
            e.HasIndex(u => new { u.Provider, u.Reference }).IsUnique();
            e.HasIndex(u => new { u.ResolvedUtc, u.CreatedUtc });
        });

        b.Entity<Promotion>().HasOne<PiranhaCommerce.Customers.CustomerGroup>().WithMany().HasForeignKey(p => p.CustomerGroupId).OnDelete(DeleteBehavior.Restrict);

        b.Entity<DisputeEvidenceFile>(e =>
        {
            e.ToTable("DisputeEvidenceFile", Schema);
            e.Property(f => f.Kind).HasConversion<string>().HasMaxLength(30);
            e.Property(f => f.FileName).HasMaxLength(200).IsRequired();
            e.Property(f => f.ContentType).HasMaxLength(100).IsRequired();
            e.Property(f => f.Actor).HasMaxLength(256);
            e.HasIndex(f => new { f.DisputeId, f.Kind }).IsUnique(); // one document per kind
            e.HasOne<PaymentDispute>().WithMany().HasForeignKey(f => f.DisputeId).OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<PaymentDispute>(e =>
        {
            e.ToTable("PaymentDispute", Schema);
            e.Property(d => d.Provider).HasMaxLength(50);
            e.Property(d => d.Reference).HasMaxLength(200);
            e.Property(d => d.Amount).HasPrecision(18, 4);
            e.Property(d => d.Currency).HasMaxLength(3);
            e.Property(d => d.Status).HasConversion<string>().HasMaxLength(10);
            e.Property(d => d.Reason).HasMaxLength(200);
            e.Property(d => d.Fee).HasPrecision(18, 4);
            e.HasIndex(d => new { d.Provider, d.Reference }).IsUnique();
            e.HasIndex(d => new { d.OrderId, d.OpenedUtc });
            e.HasIndex(d => new { d.Status, d.OpenedUtc });
            e.HasOne<Order>().WithMany().HasForeignKey(d => d.OrderId).OnDelete(DeleteBehavior.Restrict);
        });

        b.Entity<GiftVoucher>(e =>
        {
            e.ToTable("GiftVoucher", Schema);
            e.Property(v => v.CodeHash).HasMaxLength(64);
            e.HasIndex(v => v.CodeHash).IsUnique();
            e.Property(v => v.CodeLast4).HasMaxLength(4);
            e.Property(v => v.InitialValue).HasPrecision(18, 4);
            e.Property(v => v.Balance).HasPrecision(18, 4);
            e.Property(v => v.Currency).HasMaxLength(3);
            e.Property(v => v.Note).HasMaxLength(200);
        });

        b.Entity<VoucherEntry>(e =>
        {
            e.ToTable("VoucherEntry", Schema);
            e.Property(x => x.Amount).HasPrecision(18, 4);
            e.Property(x => x.Kind).HasMaxLength(20);
            e.Property(x => x.Reference).HasMaxLength(200);
            e.Property(x => x.Actor).HasMaxLength(256);
            e.HasIndex(x => new { x.VoucherId, x.CreatedUtc });
            // Restrict: a voucher with history is never deleted (there is no delete; switch it off instead).
            e.HasOne<GiftVoucher>().WithMany().HasForeignKey(x => x.VoucherId).OnDelete(DeleteBehavior.Restrict);
        });

        b.Entity<TaxClass>(e =>
        {
            e.ToTable("TaxClass", Schema);
            e.Property(c => c.Name).HasMaxLength(50);
            e.HasIndex(c => c.Name).IsUnique();
            e.HasData(new TaxClass
            {
                Id = TaxClasses.StandardId, Name = "Standard", IsDefault = true,
                CreatedUtc = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero),
            });
        });

        b.Entity<TaxRule>(e =>
        {
            e.ToTable("TaxRule", Schema);
            e.HasIndex(r => new { r.TaxClassId, r.CountryCode }).IsUnique(); // one rule per class per country
            e.HasOne<TaxClass>().WithMany().HasForeignKey(r => r.TaxClassId).OnDelete(DeleteBehavior.Restrict);
            e.Property(r => r.CountryCode).HasMaxLength(2);
            e.Property(r => r.Name).HasMaxLength(50);
            e.Property(r => r.Rate).HasPrecision(9, 4);
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
