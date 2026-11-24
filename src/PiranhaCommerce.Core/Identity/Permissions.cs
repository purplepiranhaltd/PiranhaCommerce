namespace PiranhaCommerce.Core.Identity;

/// <summary>
/// One thing a member of staff can be allowed to do. A role is a set of these; <c>Administrator</c> always holds every one, including
/// permissions a package adds later.
/// </summary>
/// <param name="Key">Stable key, such as <c>orders.refund</c>. Stored on roles, so never change one after release.</param>
/// <param name="Title">What the role editor shows.</param>
/// <param name="Group">The heading the role editor lists it under.</param>
/// <param name="Description">One sentence saying what it allows.</param>
/// <param name="Implies">Permissions that come with this one, so a role that can edit can also view.</param>
public sealed record Permission(string Key, string Title, string Group, string? Description = null, string[]? Implies = null);

/// <summary>
/// A package implements this to add its own permissions. Register with <c>services.AddSingleton&lt;IPermissionContributor, T&gt;()</c>;
/// then guard its API endpoints with <c>RequirePermission(key)</c> and its admin pages with <c>[Authorize(Policy = PermissionPolicies.For(key))]</c>.
/// </summary>
public interface IPermissionContributor
{
    IEnumerable<Permission> GetPermissions();
}

/// <summary>The permissions the built-in packages define.</summary>
public static class Permissions
{
    public const string CatalogView = "catalog.view";
    public const string CatalogEdit = "catalog.edit";
    public const string PricesManage = "prices.manage";
    public const string StockAdjust = "stock.adjust";
    public const string StockTransfer = "stock.transfer";
    public const string OrdersView = "orders.view";
    public const string OrdersCreate = "orders.create";
    public const string OrdersFulfil = "orders.fulfil";
    public const string OrdersRefund = "orders.refund";
    public const string VouchersManage = "vouchers.manage";
    public const string CustomersView = "customers.view";
    public const string CustomersManage = "customers.manage";
    public const string ReviewsModerate = "reviews.moderate";
    public const string SettingsManage = "settings.manage";
    public const string ReportsView = "reports.view";
    public const string AuditView = "audit.view";
    public const string WebhooksManage = "webhooks.manage";
    public const string StaffManage = "staff.manage";

    /// <summary>Not assignable: held by anyone who has at least one permission, and by administrators. Lets a person into the admin.</summary>
    public const string Access = "access";

    /// <summary>Not assignable: held by administrators only. The default for an endpoint or page that does not name a permission.</summary>
    public const string Administrator = "administrator";

    /// <summary>Not assignable: held by any active person who is signed in, even one with no role or who has yet to set up two-factor sign-in.</summary>
    public const string SignedIn = "signedin";
}

/// <summary>The built-in permissions, so every package can name them without each defining its own.</summary>
internal sealed class BuiltInPermissions : IPermissionContributor
{
    public IEnumerable<Permission> GetPermissions() =>
    [
        new(Permissions.CatalogView, "View the catalogue", "Catalogue", "See products, categories, brands, specifications, tags, stock and the catalogue export."),
        new(Permissions.CatalogEdit, "Edit the catalogue", "Catalogue", "Create, change and delete products, categories, brands, pictures and variants, and import products.", [Permissions.CatalogView]),
        new(Permissions.PricesManage, "Manage prices and promotions", "Catalogue", "Change prices, quantity breaks, group prices, sales, promotions and discounts.", [Permissions.CatalogView]),
        new(Permissions.StockAdjust, "Adjust stock", "Stock", "Add or remove stock, record counts, and manage stock locations.", [Permissions.CatalogView]),
        new(Permissions.StockTransfer, "Move stock between locations", "Stock", "Raise, send and receive stock transfers.", [Permissions.CatalogView]),
        new(Permissions.OrdersView, "View orders", "Orders", "See orders, baskets, disputes, return requests and unreconciled payments."),
        new(Permissions.OrdersCreate, "Create orders", "Orders", "Start baskets and place orders for customers, such as phone orders and tills.", [Permissions.OrdersView]),
        new(Permissions.OrdersFulfil, "Fulfil orders", "Orders", "Move orders on, cancel them, ship parcels and handle returns.", [Permissions.OrdersView]),
        new(Permissions.OrdersRefund, "Refund money", "Orders", "Refund payments, resolve disputes and book unreconciled payments.", [Permissions.OrdersView]),
        new(Permissions.VouchersManage, "Manage gift vouchers", "Orders", "Issue, adjust and cancel gift vouchers.", [Permissions.OrdersView]),
        new(Permissions.CustomersView, "View customers", "Customers", "See customers, their addresses and groups. Includes personal details."),
        new(Permissions.CustomersManage, "Manage customers", "Customers", "Change customers and groups, end sessions, and export or erase a customer's data.", [Permissions.CustomersView]),
        new(Permissions.ReviewsModerate, "Moderate reviews", "Customers", "Approve, reject and reply to reviews and their photos.", [Permissions.CustomersView]),
        new(Permissions.SettingsManage, "Change shop settings", "Settings", "Shipping zones, carriers and methods, tax rules and classes, and email wording."),
        new(Permissions.ReportsView, "View reports", "Reporting", "Sales, product, customer and tax reports."),
        new(Permissions.AuditView, "View the audit log", "Reporting", "See who changed what, and when."),
        new(Permissions.WebhooksManage, "Manage webhooks", "Settings", "Create and change webhook endpoints and see their deliveries."),
        new(Permissions.StaffManage, "Manage staff and roles", "Staff", "Create staff, assign roles, edit what each role can do, and limit staff to locations."),
    ];
}
