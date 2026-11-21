using System.Globalization;
using PiranhaCommerce.Core.Email;

namespace PiranhaCommerce.Ecommerce;

/// <summary>The emails the online store sends. The wording is the email sender's: this only names the email and says what data it has.</summary>
public static class EcommerceEmailTemplates
{
    public const string OrderConfirmation = "order.confirmation";
}

internal sealed class EcommerceEmailContributor : IEmailTemplateContributor
{
    public IEnumerable<EmailTemplateDescriptor> GetTemplates() =>
    [
        new(EcommerceEmailTemplates.OrderConfirmation, "Order confirmation", "Sent to the customer when an order is placed.",
        [
            new("reference", "The order reference, e.g. ORD-1001.", "ORD-1001"),
            new("email", "The customer's email address.", "ada@example.com"),
            new("currency", "The currency code, e.g. GBP.", "GBP"),
            new("subtotal", "Goods total before any discount, formatted.", "30.00"),
            new("discount", "What a promo code took off, formatted. Empty when there was none.", "3.00"),
            new("promotionCode", "The promo code used. Empty when there was none.", "SPRING"),
            new("shipping", "The postage charge, formatted.", "5.00"),
            new("shippingMethod", "The name of the postage method. May be empty.", "Royal Mail Tracked"),
            new("tax", "Total tax, formatted.", "3.70"),
            new("total", "The order total, formatted.", "35.70"),
            new("paid", "How much has been paid, formatted. Empty when nothing has.", "35.70"),
            new("stillToPay", "How much is still owed, formatted.", "0.00"),
            new("lines", "The items ordered: a list where each has name, sku, quantity and total.", new List<object?>
            {
                new Dictionary<string, object?> { ["name"] = "Blue mug", ["sku"] = "MUG-BLU", ["quantity"] = 1, ["total"] = "10.00" },
                new Dictionary<string, object?> { ["name"] = "Teapot", ["sku"] = "POT-1", ["quantity"] = 2, ["total"] = "20.00" },
            }),
            new("taxes", "Tax by rate: a list where each has name, rate and amount. Empty when no tax was charged.", new List<object?>
            {
                new Dictionary<string, object?> { ["name"] = "VAT", ["rate"] = "20", ["amount"] = "3.70" },
            }),
            new("shippingAddress", "The delivery address as a list of lines. Empty when there is none.", new List<object?> { "Ada Lovelace", "1 High St", "Leeds", "LS1 1AA", "GB" }),
        ]),
    ];
}

internal static class OrderEmails
{
    public static EmailMessage Confirmation(OrderDto o)
    {
        var inv = CultureInfo.InvariantCulture;
        string Money(decimal v) => v.ToString("N2", inv);
        var variables = new Dictionary<string, object?>
        {
            ["reference"] = o.Reference, ["email"] = o.Email, ["currency"] = o.Currency,
            ["subtotal"] = Money(o.Subtotal), ["discount"] = o.DiscountTotal > 0 ? Money(o.DiscountTotal) : null, ["promotionCode"] = o.PromotionCode,
            ["shipping"] = Money(o.ShippingTotal), ["shippingMethod"] = o.ShippingMethodName, ["tax"] = Money(o.TaxTotal), ["total"] = Money(o.Total),
            ["paid"] = o.AmountPaid > 0 ? Money(o.AmountPaid) : null, ["stillToPay"] = Money(Math.Max(0m, o.Total - o.AmountPaid + o.AmountRefunded)),
            ["lines"] = o.Lines.Select(l => (object?)new Dictionary<string, object?>
                { ["name"] = l.Name, ["sku"] = l.Sku, ["quantity"] = l.Quantity, ["total"] = Money(l.LineTotal) }).ToList(),
            ["taxes"] = (o.Taxes ?? []).Where(t => t.Amount > 0).Select(t => (object?)new Dictionary<string, object?>
                { ["name"] = t.Name, ["rate"] = t.Rate.ToString("0.####", inv), ["amount"] = Money(t.Amount) }).ToList(),
            ["shippingAddress"] = o.ShippingAddress is { } a
                ? new[] { a.Recipient, a.Line1, a.Line2, a.City, a.Region, a.PostalCode, a.CountryCode }.Where(l => !string.IsNullOrWhiteSpace(l)).Cast<object?>().ToList()
                : new List<object?>(),
        };
        return new EmailMessage(o.Email, EcommerceEmailTemplates.OrderConfirmation, variables);
    }
}
