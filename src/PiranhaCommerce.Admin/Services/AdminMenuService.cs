using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using PiranhaCommerce.Core.Admin;
using PiranhaCommerce.Core.Identity;

namespace PiranhaCommerce.Admin.Services;

public sealed class AdminMenuService(IEnumerable<IAdminMenuContributor> contributors, IEnumerable<IAdminBadgeProvider> badges, IServiceScopeFactory scopes, ILogger<AdminMenuService> logger)
{
    /// <summary>
    /// The section reports are gathered under. It belongs to the admin itself, not to one package, so a package adds a report by giving its entry
    /// <c>ParentKey: "reports"</c>; the section only shows when something is in it.
    /// </summary>
    public const string ReportsKey = "reports";

    public IReadOnlyList<AdminMenuItem> GetMenu()
    {
        var items = contributors.SelectMany(c => c.GetMenuItems()).ToList();
        if (!items.Any(i => i.Key == ReportsKey)) items.Add(new AdminMenuItem(ReportsKey, "Reports", Icon: "chart", Order: 30));
        return items.OrderBy(i => i.Order).ToList();
    }

    /// <summary>
    /// The menu for one person: entries they hold the permission for, and a section only when something in it is left. An entry that names no
    /// permission is for administrators only, the same default as a page.
    /// </summary>
    public IReadOnlyList<AdminMenuItem> GetMenuFor(StaffAccess access)
    {
        var all = GetMenu();
        var visible = all.Where(i => i.Href is not null && access.Has(i.RequiredPermission ?? Permissions.Administrator)).ToList();
        var parents = visible.Where(i => i.ParentKey is not null).Select(i => i.ParentKey!).ToHashSet();
        // A section shows when one of its entries does; a top-level link with no section follows its own permission.
        var sections = all.Where(i => i.Href is null && parents.Contains(i.Key));
        return visible.Concat(sections).OrderBy(i => i.Order).ToList();
    }

    /// <summary>What is waiting, by badge key. Only keys with something waiting are present. A failing provider is logged and left out: a menu must never break a page.</summary>
    public async Task<IReadOnlyDictionary<string, int>> GetBadgesAsync(CancellationToken ct = default)
    {
        var result = new Dictionary<string, int>();
        var providers = badges.ToList();
        if (providers.Count == 0) return result;
        await using var scope = scopes.CreateAsyncScope();
        foreach (var provider in providers)
        {
            try
            {
                var count = await provider.CountAsync(scope.ServiceProvider, ct);
                if (count > 0) result[provider.Key] = count;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex) { logger.LogWarning(ex, "The admin menu badge {Key} could not be counted.", provider.Key); }
        }
        return result;
    }
}
