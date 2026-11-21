using System.Globalization;
using System.Text;
using PiranhaCommerce.Core.Email;

namespace PiranhaCommerce.Ecommerce;

internal static class OrderEmails
{
    /// <summary>Plain text on purpose: it survives every mail client, and shop wording belongs to a template plugin later.</summary>
    public static EmailMessage Confirmation(OrderDto o)
    {
        var inv = CultureInfo.InvariantCulture;
        var text = new StringBuilder();
        text.AppendLine($"Thank you for your order {o.Reference}.").AppendLine();
        foreach (var l in o.Lines)
            text.AppendLine($"{l.Quantity} x {l.Name} ({l.Sku})  {l.LineTotal.ToString("N2", inv)} {o.Currency}");
        text.AppendLine();
        text.AppendLine($"Subtotal: {o.Subtotal.ToString("N2", inv)} {o.Currency}");
        if (o.DiscountTotal > 0) text.AppendLine($"Discount{(string.IsNullOrEmpty(o.PromotionCode) ? "" : $" ({o.PromotionCode})")}: -{o.DiscountTotal.ToString("N2", inv)} {o.Currency}");
        if (o.ShippingTotal > 0 || !string.IsNullOrEmpty(o.ShippingMethodName))
            text.AppendLine($"Shipping{(string.IsNullOrEmpty(o.ShippingMethodName) ? "" : $" ({o.ShippingMethodName})")}: {o.ShippingTotal.ToString("N2", inv)} {o.Currency}");
        if (o.Taxes is { Count: > 0 })
        {
            foreach (var t in o.Taxes.Where(t => t.Amount > 0))
                text.AppendLine($"{t.Name} ({t.Rate.ToString("0.####", inv)}%): {t.Amount.ToString("N2", inv)} {o.Currency}");
        }
        else if (o.TaxTotal > 0) text.AppendLine($"Tax: {o.TaxTotal.ToString("N2", inv)} {o.Currency}");
        text.AppendLine($"Total: {o.Total.ToString("N2", inv)} {o.Currency}");
        if (o.AmountPaid > 0)
        {
            text.AppendLine($"Paid: {o.AmountPaid.ToString("N2", inv)} {o.Currency}");
            text.AppendLine($"Still to pay: {Math.Max(0m, o.Total - o.AmountPaid + o.AmountRefunded).ToString("N2", inv)} {o.Currency}");
        }
        text.AppendLine();
        if (o.ShippingAddress is { } a)
        {
            text.AppendLine("Delivering to:");
            foreach (var line in new[] { a.Recipient, a.Line1, a.Line2, a.City, a.Region, a.PostalCode, a.CountryCode })
                if (!string.IsNullOrWhiteSpace(line)) text.AppendLine(line);
        }
        string Money(decimal v) => v.ToString("N2", inv);
        var variables = new Dictionary<string, object?>
        {
            ["reference"] = o.Reference, ["email"] = o.Email, ["currency"] = o.Currency,
            ["subtotal"] = Money(o.Subtotal), ["discount"] = o.DiscountTotal > 0 ? Money(o.DiscountTotal) : null, ["promotionCode"] = o.PromotionCode,
            ["shipping"] = Money(o.ShippingTotal), ["shippingMethod"] = o.ShippingMethodName, ["tax"] = Money(o.TaxTotal), ["total"] = Money(o.Total),
            ["paid"] = o.AmountPaid > 0 ? Money(o.AmountPaid) : null, ["stillToPay"] = Money(Math.Max(0m, o.Total - o.AmountPaid + o.AmountRefunded)),
            ["lines"] = o.Lines.Select(l => (object)new Dictionary<string, object?>
                { ["name"] = l.Name, ["sku"] = l.Sku, ["quantity"] = l.Quantity, ["total"] = Money(l.LineTotal) }).ToList(),
            ["taxes"] = (o.Taxes ?? []).Where(t => t.Amount > 0).Select(t => (object)new Dictionary<string, object?>
                { ["name"] = t.Name, ["rate"] = t.Rate.ToString("0.####", inv), ["amount"] = Money(t.Amount) }).ToList(),
        };
        return new EmailMessage(o.Email, $"Your order {o.Reference}", text.ToString(), TemplateKey: EmailTemplates.OrderConfirmation, Variables: variables);
    }
}
