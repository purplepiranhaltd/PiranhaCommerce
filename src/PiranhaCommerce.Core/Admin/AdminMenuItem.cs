namespace PiranhaCommerce.Core.Admin;

/// <summary>A navigation entry in the Blazor admin. UI-framework neutral so Core stays reusable (e.g. by EPOS).</summary>
public sealed record AdminMenuItem(string Key, string Title, string? Href = null, string? Icon = null, int Order = 100, string? ParentKey = null, string? RequiredPermission = null);

/// <summary>Plugins implement this to add menu entries to the admin area.</summary>
public interface IAdminMenuContributor
{
    IEnumerable<AdminMenuItem> GetMenuItems();
}
