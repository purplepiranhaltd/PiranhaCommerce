using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using PiranhaCommerce.Core.Admin;

namespace PiranhaCommerce.Admin.Services;

public sealed class AdminMenuService(IEnumerable<IAdminMenuContributor> contributors, IEnumerable<IAdminBadgeProvider> badges, IServiceScopeFactory scopes, ILogger<AdminMenuService> logger)
{
    public IReadOnlyList<AdminMenuItem> GetMenu() =>
        contributors.SelectMany(c => c.GetMenuItems()).OrderBy(i => i.Order).ToList();

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
