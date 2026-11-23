using System.Globalization;
using PiranhaCommerce.Ecommerce;

namespace PiranhaCommerce.Web.Shop;

internal static class Money
{
    public static string Format(decimal amount, string currency) =>
        (currency.ToUpperInvariant() switch { "GBP" => "£", "EUR" => "€", "USD" => "$", var c => c + " " }) + amount.ToString("N2", CultureInfo.GetCultureInfo("en-GB"));

    /// <summary>What a shopper is shown for one: the tax-inclusive figure when a destination was priced, otherwise the plain price.</summary>
    public static decimal Now(ShopperPriceDto p) => p.PriceIncludingTax ?? p.Price;

    public static decimal Was(ShopperPriceDto p) => p.RegularPriceIncludingTax ?? p.RegularPrice;

    public static string Stars(decimal average)
    {
        var full = (int)Math.Round(average, MidpointRounding.AwayFromZero);
        return new string('★', full) + new string('☆', 5 - full);
    }

    public static string Image(string url, int width) => url + "?width=" + width;
}
