namespace PiranhaCommerce.Web.Shop;

/// <summary>Settings under "Piranha:Shop" for the sample shop front.</summary>
public sealed class ShopOptions
{
    public const string SectionName = "Piranha:Shop";

    /// <summary>The shop's name, shown in the header and page titles.</summary>
    public string Name { get; set; } = "Petalwick";

    /// <summary>The country prices are shown for (with tax). Real shops would take this from the shopper's delivery address.</summary>
    public string CountryCode { get; set; } = "GB";

    /// <summary>How many form posts (add to basket, checkout and so on) one client may make a minute. Starting a basket costs a database row, so this stops a script filling the table.</summary>
    public int FormsPerMinute { get; set; } = 120;

    /// <summary>How many sign-in, registration and password-reset posts one client may make a minute. Lower than the general limit, because these are what password guessing uses. (Wrong passwords also lock the account, see <c>Piranha:Customers</c>.)</summary>
    public int AccountFormsPerMinute { get; set; } = 10;
}
