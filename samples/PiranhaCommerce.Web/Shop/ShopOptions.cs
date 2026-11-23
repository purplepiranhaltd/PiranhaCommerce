namespace PiranhaCommerce.Web.Shop;

/// <summary>Settings under "Piranha:Shop" for the sample shop front.</summary>
public sealed class ShopOptions
{
    public const string SectionName = "Piranha:Shop";

    /// <summary>The shop's name, shown in the header and page titles.</summary>
    public string Name { get; set; } = "Petalwick";

    /// <summary>The country prices are shown for (with tax). Real shops would take this from the shopper's delivery address.</summary>
    public string CountryCode { get; set; } = "GB";
}
