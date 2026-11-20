namespace PiranhaCommerce.Core.Admin;

/// <summary>
/// Names of the slots admin pages expose. A slot is a place in a page where plugins can contribute their own
/// components, so one package can extend another package's page without that page knowing about it.
/// </summary>
public static class AdminSlots
{
    public const string DashboardTop = "dashboard.top";
    public const string DashboardBottom = "dashboard.bottom";

    /// <summary>Panels on the edit page of an existing product. Supplies parameter <c>Guid ProductId</c>.</summary>
    public const string ProductEditPanels = "product.edit.panels";
}

/// <summary>Puts a component (a Razor component type) into a slot. Components receive the slot's parameters.</summary>
public sealed record AdminSlotItem(string Slot, Type ComponentType, int Order = 100);

/// <summary>Plugins implement this to contribute components to admin slots.</summary>
public interface IAdminSlotContributor
{
    IEnumerable<AdminSlotItem> GetItems();
}
