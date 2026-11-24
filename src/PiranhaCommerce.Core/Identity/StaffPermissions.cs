using System.Collections.Concurrent;
using System.Security.Claims;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;

namespace PiranhaCommerce.Core.Identity;

/// <summary>Every permission known to the running application: the built-in ones and those packages contributed.</summary>
public sealed class PermissionCatalog
{
    private readonly Dictionary<string, Permission> _byKey;

    public PermissionCatalog(IEnumerable<IPermissionContributor> contributors)
    {
        _byKey = new(StringComparer.Ordinal);
        foreach (var p in contributors.SelectMany(c => c.GetPermissions()))
            _byKey.TryAdd(p.Key, p);
    }

    public IReadOnlyCollection<Permission> All => _byKey.Values;

    public Permission? Find(string key) => _byKey.GetValueOrDefault(key);

    public bool Exists(string key) => _byKey.ContainsKey(key);

    /// <summary>The permissions grouped under their headings, in the order they were contributed.</summary>
    public IEnumerable<IGrouping<string, Permission>> Groups => _byKey.Values.GroupBy(p => p.Group);

    /// <summary>The given keys plus everything they imply, ignoring keys that are not known.</summary>
    public HashSet<string> Expand(IEnumerable<string> keys)
    {
        var result = new HashSet<string>(StringComparer.Ordinal);
        var pending = new Stack<string>(keys);
        while (pending.Count > 0)
        {
            var key = pending.Pop();
            if (!_byKey.TryGetValue(key, out var p) || !result.Add(key)) continue;
            foreach (var implied in p.Implies ?? []) pending.Push(implied);
        }
        return result;
    }
}

/// <summary>What one signed-in person may do.</summary>
public sealed class StaffAccess
{
    public static readonly StaffAccess None = new(false, new HashSet<string>());

    public StaffAccess(bool isAdministrator, IReadOnlySet<string> permissions)
    {
        IsAdministrator = isAdministrator;
        Permissions = permissions;
    }

    public bool IsAdministrator { get; }

    /// <summary>The permissions held through roles, with implied ones added. Empty for an administrator, who holds all of them.</summary>
    public IReadOnlySet<string> Permissions { get; }

    public bool Has(string key) => key switch
    {
        Identity.Permissions.Administrator => IsAdministrator,
        Identity.Permissions.Access => IsAdministrator || Permissions.Count > 0,
        _ => IsAdministrator || Permissions.Contains(key),
    };
}

/// <summary>
/// Works out what a signed-in person may do, from the database and not from their token: the roles on a token are only as fresh as the token,
/// so a removed permission would otherwise last until it expired. Answers are kept for a few seconds, and dropped at once when roles change
/// in this process; another instance sees a change within <see cref="Lifetime"/>.
/// </summary>
public sealed class StaffPermissionService(IServiceScopeFactory scopes, PermissionCatalog catalog)
{
    /// <summary>The claim type a role's permissions are stored under (the standard Identity role claims table).</summary>
    public const string ClaimType = "permission";

    public static readonly TimeSpan Lifetime = TimeSpan.FromSeconds(15);

    private readonly ConcurrentDictionary<Guid, (DateTimeOffset At, StaffAccess Access)> _cache = new();

    public PermissionCatalog Catalog => catalog;

    /// <summary>Forget what is known, so the next question reads the database. Call after changing roles or who holds them.</summary>
    public void Invalidate() => _cache.Clear();

    public async Task<bool> HasAsync(ClaimsPrincipal user, string key, CancellationToken ct = default) =>
        (await GetAsync(user, ct)).Has(key);

    public async Task<StaffAccess> GetAsync(ClaimsPrincipal principal, CancellationToken ct = default)
    {
        if (principal.Identity?.IsAuthenticated != true || UserId(principal) is not { } id) return StaffAccess.None;
        if (_cache.TryGetValue(id, out var hit) && DateTimeOffset.UtcNow - hit.At < Lifetime) return hit.Access;

        var access = await LoadAsync(id, ct);
        _cache[id] = (DateTimeOffset.UtcNow, access);
        return access;
    }

    public static Guid? UserId(ClaimsPrincipal principal)
    {
        var value = principal.FindFirstValue("sub") ?? principal.FindFirstValue(ClaimTypes.NameIdentifier);
        return Guid.TryParse(value, out var id) ? id : null;
    }

    private async Task<StaffAccess> LoadAsync(Guid id, CancellationToken ct)
    {
        await using var scope = scopes.CreateAsyncScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<PiranhaCommerceUser>>();
        var roles = scope.ServiceProvider.GetRequiredService<RoleManager<PiranhaCommerceRole>>();

        var user = await users.FindByIdAsync(id.ToString());
        if (user is null || !user.Active) return StaffAccess.None;

        var names = await users.GetRolesAsync(user);
        if (names.Contains(PiranhaCommerceRoles.Administrator)) return new StaffAccess(true, new HashSet<string>());

        var keys = new List<string>();
        foreach (var name in names)
        {
            if (await roles.FindByNameAsync(name) is not { } role) continue;
            keys.AddRange((await roles.GetClaimsAsync(role)).Where(c => c.Type == ClaimType).Select(c => c.Value));
        }
        return new StaffAccess(false, catalog.Expand(keys));
    }
}
