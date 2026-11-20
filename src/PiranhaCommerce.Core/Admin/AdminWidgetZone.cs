namespace PiranhaCommerce.Core.Admin;

/// <summary>Named extension points in admin pages (the Blazor equivalent of nopCommerce widget zones).</summary>
public static class AdminWidgetZones
{
    public const string DashboardTop = "admin.dashboard.top";
    public const string DashboardBottom = "admin.dashboard.bottom";
    public const string ProductEditTabs = "admin.product.edit.tabs";
}

/// <summary>Registers a component type (must derive from ComponentBase) into a zone. Resolved by the Admin package.</summary>
public sealed record AdminWidget(string Zone, Type ComponentType, int Order = 100);

public interface IAdminWidgetContributor
{
    IEnumerable<AdminWidget> GetWidgets();
}
