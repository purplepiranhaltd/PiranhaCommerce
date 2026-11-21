namespace PiranhaCommerce.Core.Admin;

/// <summary>A navigation entry in the Blazor admin. UI-framework neutral so Core stays reusable (e.g. by EPOS).</summary>
public sealed record AdminMenuItem(string Key, string Title, string? Href = null, string? Icon = null, int Order = 100, string? ParentKey = null, string? RequiredPermission = null, string? BadgeKey = null);

/// <summary>
/// A number shown beside a menu entry that names it in <see cref="AdminMenuItem.BadgeKey"/> (things waiting for a person). Register with
/// <c>services.AddSingleton&lt;IAdminBadgeProvider, T&gt;()</c>. A count of zero shows nothing, and a provider that throws shows nothing.
/// </summary>
public interface IAdminBadgeProvider
{
    string Key { get; }

    /// <param name="services">A short-lived scope, so scoped services (the database) can be used.</param>
    Task<int> CountAsync(IServiceProvider services, CancellationToken ct = default);
}

/// <summary>Plugins implement this to add menu entries to the admin area.</summary>
public interface IAdminMenuContributor
{
    IEnumerable<AdminMenuItem> GetMenuItems();
}
