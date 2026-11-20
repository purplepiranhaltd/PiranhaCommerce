using System.ComponentModel.DataAnnotations;
using PiranhaCommerce.Core.Domain;
using PiranhaCommerce.Customers;

namespace PiranhaCommerce.Ecommerce;

public enum BasketStatus { Open = 0, CheckedOut = 1 }

/// <summary>
/// Items a shopper intends to buy. Holds no prices: they are read from the catalogue every time, and copied onto the order at checkout.
/// The id is an unguessable GUID, which is the only thing a storefront needs to keep.
/// </summary>
public class Basket : AuditableEntity
{
    public BasketStatus Status { get; set; }
    public Guid? CustomerId { get; set; }
    public Guid? OrderId { get; set; }
    public List<BasketLine> Lines { get; set; } = [];
}

public class BasketLine : Entity
{
    public Guid BasketId { get; set; }
    public Guid ProductId { get; set; }
    public int Quantity { get; set; }
}

/// <summary>One lifecycle, one field. Payment, shipment and refund will be their own records that move the stage.</summary>
public enum OrderStage { Placed = 0, Processing = 1, Completed = 2, Cancelled = 3 }

public static class OrderStages
{
    /// <summary>The only moves allowed. Completed and Cancelled are final.</summary>
    public static IReadOnlyList<OrderStage> Next(OrderStage from) => from switch
    {
        OrderStage.Placed => [OrderStage.Processing, OrderStage.Cancelled],
        OrderStage.Processing => [OrderStage.Completed, OrderStage.Cancelled],
        _ => [],
    };
}

/// <summary>
/// A purchase. Everything on it is a copy taken at checkout (names, prices, addresses, email), so later edits to products or
/// customers - or erasing a customer - never change what was sold. <see cref="CustomerId"/> is a plain reference, not a foreign key.
/// </summary>
public class Order : AuditableEntity
{
    public long Number { get; set; }
    public OrderStage Stage { get; set; }
    public Guid? CustomerId { get; set; }
    public required string Email { get; set; }
    public required string Currency { get; set; }
    public decimal Subtotal { get; set; }
    /// <summary>Copies of the chosen method's name and the price charged; later edits to the method never change the order.</summary>
    public string? ShippingMethodName { get; set; }
    public decimal ShippingTotal { get; set; }
    /// <summary>Summary of the tax: the rate name when every taxed line shares one, "Tax" when mixed. Each line carries its own.</summary>
    public string? TaxName { get; set; }
    public decimal TaxRate { get; set; }
    /// <summary>All tax: the lines' tax plus <see cref="ShippingTax"/>.</summary>
    public decimal TaxTotal { get; set; }
    public decimal ShippingTax { get; set; }
    public string? ShippingTaxName { get; set; }
    public decimal ShippingTaxRate { get; set; }
    public decimal Total { get; set; }
    public string? Notes { get; set; }
    public OrderAddress? ShippingAddress { get; set; }
    public OrderAddress? BillingAddress { get; set; }
    public List<OrderLine> Lines { get; set; } = [];
}

/// <summary>Owned by the order (columns on the order row). A copy: it is never linked to the customer's address book.</summary>
public class OrderAddress
{
    public string? Recipient { get; set; }
    public required string Line1 { get; set; }
    public string? Line2 { get; set; }
    public required string City { get; set; }
    public string? Region { get; set; }
    public string? PostalCode { get; set; }
    public required string CountryCode { get; set; }
}

public class OrderLine : Entity
{
    public Guid OrderId { get; set; }
    public int Position { get; set; }
    /// <summary>The product at the time of sale. Deliberately restricted: a product that has been sold cannot be deleted.</summary>
    public Guid ProductId { get; set; }
    public required string Name { get; set; }
    public string? Sku { get; set; }
    public decimal UnitPrice { get; set; }
    public int Quantity { get; set; }
    public decimal LineTotal { get; set; }
    /// <summary>Copy of the rate that applied to this line when it was sold (null when untaxed).</summary>
    public string? TaxName { get; set; }
    public decimal TaxRate { get; set; }
    public decimal TaxAmount { get; set; }
}

/// <summary>Single-row counter behind order numbers; incremented inside the checkout transaction, whose row lock serialises checkouts.</summary>
public class OrderCounter
{
    public int Id { get; set; } = 1;
    public long LastNumber { get; set; }
}

public static class OrderNumbers
{
    public const long First = 1001;
    public static string Format(long number) => $"ORD-{number}";
}

// ---- Inputs and DTOs -------------------------------------------------------------------------------------------------------

public sealed class BasketCreateInput
{
    public Guid? CustomerId { get; set; }
}

public sealed class BasketLineInput
{
    /// <summary>0 removes the line. At most <see cref="Max"/>.</summary>
    [Range(0, Max)]
    public int Quantity { get; set; }

    public const int Max = 10_000;
}

public sealed record BasketLineDto(Guid ProductId, string Name, string? Sku, decimal UnitPrice, int Quantity, decimal LineTotal, bool Available);

public sealed record BasketDto(
    Guid Id, BasketStatus Status, Guid? CustomerId, Guid? OrderId, string Currency, decimal Subtotal, IReadOnlyList<BasketLineDto> Lines);

public sealed class CheckoutInput
{
    [Required, EmailAddress, StringLength(256)]
    public string Email { get; set; } = "";

    /// <summary>Optional link to a customer record. Must exist when given.</summary>
    public Guid? CustomerId { get; set; }

    [Required]
    public AddressInput? ShippingAddress { get; set; }

    /// <summary>Required whenever the store has any active shipping method.</summary>
    public Guid? ShippingMethodId { get; set; }

    /// <summary>Defaults to the shipping address.</summary>
    public AddressInput? BillingAddress { get; set; }

    [StringLength(2000)]
    public string? Notes { get; set; }
}

public sealed class OrderStageInput
{
    public OrderStage Stage { get; set; }
}

public sealed record OrderAddressDto(string? Recipient, string Line1, string? Line2, string City, string? Region, string? PostalCode, string CountryCode);

public sealed record OrderLineDto(
    Guid ProductId, string Name, string? Sku, decimal UnitPrice, int Quantity, decimal LineTotal, string? TaxName = null, decimal TaxRate = 0m, decimal TaxAmount = 0m);

public sealed record OrderSummaryDto(
    Guid Id, long Number, string Reference, OrderStage Stage, PaymentStatus PaymentStatus, string Email, string Currency, decimal Total, DateTimeOffset CreatedUtc);

public sealed record OrderDto(
    Guid Id, long Number, string Reference, OrderStage Stage, Guid? CustomerId, string Email, string Currency, decimal Subtotal,
    string? ShippingMethodName, decimal ShippingTotal, string? TaxName, decimal TaxRate, decimal TaxTotal, decimal Total,
    PaymentStatus PaymentStatus, decimal AmountPaid, decimal AmountRefunded,
    string? Notes, OrderAddressDto? ShippingAddress, OrderAddressDto? BillingAddress, IReadOnlyList<OrderLineDto> Lines,
    IReadOnlyList<OrderStage> NextStages, DateTimeOffset CreatedUtc, DateTimeOffset? UpdatedUtc,
    decimal ShippingTax = 0m, IReadOnlyList<TaxLineDto>? Taxes = null);

public sealed class EcommerceOptions
{
    public const string SectionName = "Piranha:Ecommerce";

    /// <summary>ISO 4217 code written on every order. One currency per store for now.</summary>
    [Required, RegularExpression("^[A-Za-z]{3}$")]
    public string Currency { get; set; } = "GBP";

    /// <summary>Where online orders take stock from (and cancellations return it to). The default location when empty.</summary>
    public Guid? StockLocationId { get; set; }

    /// <summary>When true an order can only move to Processing once it is fully paid. Off by default (invoice and pay-on-collection shops).</summary>
    public bool RequirePaymentBeforeProcessing { get; set; }

    /// <summary>Email the buyer a confirmation when an order is placed. Only does anything once an email plugin is registered.</summary>
    public bool SendOrderConfirmations { get; set; } = true;
}
