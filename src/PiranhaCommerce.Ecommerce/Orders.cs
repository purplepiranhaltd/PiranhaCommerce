using System.ComponentModel.DataAnnotations;
using PiranhaCommerce.Core.Audit;
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
    /// <summary>The promo code the shopper entered (upper-case), or null. Checked again, and used up, at checkout.</summary>
    public string? PromotionCode { get; set; }
    /// <summary>The gift voucher the shopper will pay with (a reference, not the code: only a hash of the code is ever stored).</summary>
    public Guid? VoucherId { get; set; }
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
[Audited]
public class Order : AuditableEntity
{
    public long Number { get; set; }
    public OrderStage Stage { get; set; }
    public Guid? CustomerId { get; set; }
    [AuditMask] public required string Email { get; set; }
    public required string Currency { get; set; }
    public decimal Subtotal { get; set; }
    /// <summary>Copies of the chosen method's name and the price charged; later edits to the method never change the order.</summary>
    public string? ShippingMethodName { get; set; }
    public decimal ShippingTotal { get; set; }
    /// <summary>The promo code used and what it took off the goods (copies; the code is also kept as an id for giving the use back on cancel).</summary>
    public Guid? PromotionId { get; set; }
    public string? PromotionCode { get; set; }
    public decimal DiscountTotal { get; set; }
    /// <summary>The discount rules applied (copies), besides the promo code.</summary>
    public List<OrderDiscount> Discounts { get; set; } = [];
    /// <summary>The gift voucher paid with, and how much of the total it paid (also recorded as a payment). Given back if the order is cancelled.</summary>
    public Guid? VoucherId { get; set; }
    public decimal VoucherPaid { get; set; }
    /// <summary>Summary of the tax: the rate name when every taxed line shares one, "Tax" when mixed. Each line carries its own.</summary>
    public string? TaxName { get; set; }
    public decimal TaxRate { get; set; }
    /// <summary>All tax: the lines' tax plus <see cref="ShippingTax"/>.</summary>
    public decimal TaxTotal { get; set; }
    public decimal ShippingTax { get; set; }
    public string? ShippingTaxName { get; set; }
    public decimal ShippingTaxRate { get; set; }
    public decimal Total { get; set; }
    [AuditMask] public string? Notes { get; set; }
    /// <summary>Set when the buyer's personal data was erased. The order itself is kept for the shop's accounts; its email is an undeliverable placeholder.</summary>
    public DateTimeOffset? ErasedUtc { get; set; }
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
    /// <summary>This line's share of the order discount. Tax was charged on LineTotal minus this.</summary>
    public decimal DiscountAmount { get; set; }
    /// <summary>Copy of the rate that applied to this line when it was sold (null when untaxed).</summary>
    public string? TaxName { get; set; }
    public decimal TaxRate { get; set; }
    public decimal TaxAmount { get; set; }
    /// <summary>How many have been sent, and how many of those came back. Counters that guard the records (shipments, returns) so that
    /// simultaneous staff cannot send or return more than exists; the shipment and return rows are the history.</summary>
    public int QuantityShipped { get; set; }
    public int QuantityReturned { get; set; }
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

/// <param name="RegularPrice">The product's own price, shown when a customer group gives this shopper a lower <paramref name="UnitPrice"/>; null when they pay the regular price.</param>
public sealed record BasketLineDto(Guid ProductId, string Name, string? Sku, decimal UnitPrice, int Quantity, decimal LineTotal, bool Available, decimal? RegularPrice = null);

public sealed record BasketDto(
    Guid Id, BasketStatus Status, Guid? CustomerId, Guid? OrderId, string Currency, decimal Subtotal, IReadOnlyList<BasketLineDto> Lines,
    string? PromotionCode = null, string? VoucherLast4 = null);

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
    Guid ProductId, string Name, string? Sku, decimal UnitPrice, int Quantity, decimal LineTotal, string? TaxName = null, decimal TaxRate = 0m, decimal TaxAmount = 0m, decimal DiscountAmount = 0m,
    Guid Id = default, int QuantityShipped = 0, int QuantityReturned = 0)
{
    /// <summary>What the customer paid for this line with its tax: the line total less its share of any discount, plus its tax.</summary>
    public decimal LineTotalIncludingTax => LineTotal - DiscountAmount + TaxAmount;
}

public sealed record OrderSummaryDto(
    Guid Id, long Number, string Reference, OrderStage Stage, PaymentStatus PaymentStatus, string Email, string Currency, decimal Total, DateTimeOffset CreatedUtc);

public sealed record OrderDto(
    Guid Id, long Number, string Reference, OrderStage Stage, Guid? CustomerId, string Email, string Currency, decimal Subtotal,
    string? ShippingMethodName, decimal ShippingTotal, string? TaxName, decimal TaxRate, decimal TaxTotal, decimal Total,
    PaymentStatus PaymentStatus, decimal AmountPaid, decimal AmountRefunded,
    string? Notes, OrderAddressDto? ShippingAddress, OrderAddressDto? BillingAddress, IReadOnlyList<OrderLineDto> Lines,
    IReadOnlyList<OrderStage> NextStages, DateTimeOffset CreatedUtc, DateTimeOffset? UpdatedUtc,
    decimal ShippingTax = 0m, IReadOnlyList<TaxLineDto>? Taxes = null, decimal DiscountTotal = 0m, string? PromotionCode = null,
    IReadOnlyList<ShipmentSummaryDto>? Shipments = null, IReadOnlyList<AppliedDiscountDto>? Discounts = null);

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

    /// <summary>Email the buyer when a parcel is sent, the order is cancelled, or a refund is recorded. Same rule: only once an email plugin is registered, and never able to fail the change itself.</summary>
    public bool SendOrderUpdates { get; set; } = true;

    /// <summary>Email the reviewer the first time the shop replies to their review. Only once an email plugin is registered, and never able to fail the reply itself.</summary>
    public bool SendReviewReplies { get; set; } = true;

    /// <summary>
    /// Who is emailed when money a payment gateway confirmed could not be booked and lands on the unreconciled list (one or more addresses,
    /// separated by commas). Empty means nobody is emailed: the list and the log are then the only places it shows. Only does anything once
    /// an email plugin is registered, and can never fail the webhook.
    /// </summary>
    public string? AlertEmail { get; set; }

    /// <summary>
    /// Open baskets nobody has touched for this many days are deleted (checked-out baskets never are). 0 keeps them for ever. Baskets hold no
    /// stock, so this is only housekeeping: a shopper who comes back later simply starts a new basket.
    /// </summary>
    [Range(0, 3650)]
    public int AbandonedBasketDays { get; set; } = 60;

    /// <summary>
    /// Whether a shopper who is not signed in may place an order (<c>POST /storefront/guest/baskets/{id}/checkout</c>). When false they can still
    /// build a basket and sign in or register to check out (the basket is merged or claimed). On by default.
    /// </summary>
    public bool AllowGuestCheckout { get; set; } = true;

    /// <summary>
    /// Whether a new or changed review waits for staff to approve it before shoppers see it. On by default; when off, reviews show at once and staff can still reject or delete them.
    /// </summary>
    public bool ReviewsNeedApproval { get; set; } = true;

    /// <summary>How often, in seconds, to look for sold-out products that can be bought again and email the shoppers waiting for them. 0 switches the check off (a host can then call IStockAlertNotifier itself).</summary>
    [Range(0, 86_400)]
    public int StockAlertPollSeconds { get; set; } = 300;

    /// <summary>
    /// Email the shop's <see cref="AlertEmail"/> address(es) when a product on sale is at or below this much on hand across every location (the same measure as the Low stock page).
    /// Empty switches it off, and so does an empty alert address. Each product is told about once per dip: it is told about again only after it has been restocked above the level.
    /// </summary>
    [Range(0, 1_000_000)]
    public decimal? LowStockAlertLevel { get; set; }

    /// <summary>How often, in seconds, to check for low stock. 0 switches the check off (a host can then call ILowStockAlerter itself).</summary>
    [Range(0, 86_400)]
    public int LowStockAlertPollSeconds { get; set; } = 900;

    /// <summary>The shop front's unsubscribe page for back-in-stock emails, with <c>{token}</c> where the secret goes. When empty the email carries only the token.</summary>
    public string? StockAlertUnsubscribeUrlTemplate { get; set; }

    /// <summary>
    /// The shop's home country (two letters, e.g. GB). Price lookups that are not told a destination show tax-inclusive prices for it. Leave empty to show
    /// no tax-inclusive prices unless the caller names a country.
    /// </summary>
    [RegularExpression("^[A-Za-z]{2}$", ErrorMessage = "Use a two-letter country code such as GB.")]
    public string? DefaultCountryCode { get; set; }

    /// <summary>
    /// Orders still at Placed with no payment at all after this many hours are cancelled, which puts their stock back for other buyers (the buyer is
    /// emailed like any cancel). 0 (the default) never does it: shops that take orders on account or by invoice, or that record payments by hand
    /// later, must leave it off. Paid or part-paid orders and orders already in progress are never touched.
    /// </summary>
    [Range(0, 8760)]
    public int UnpaidOrderHours { get; set; }
}
