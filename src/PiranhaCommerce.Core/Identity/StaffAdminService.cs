using System.Security.Claims;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;
using PiranhaCommerce.Core.Audit;
using PiranhaCommerce.Core.Validation;

namespace PiranhaCommerce.Core.Identity;

/// <param name="Name">The role's name; also its identity.</param>
/// <param name="IsAdministrator">The built-in role that holds every permission. It cannot be edited or deleted.</param>
/// <param name="Permissions">Permission keys held directly. Empty for Administrator, which holds them all.</param>
/// <param name="Members">How many staff are in the role.</param>
public sealed record StaffRoleDto(string Name, bool IsAdministrator, IReadOnlyList<string> Permissions, int Members);

public sealed record StaffRoleInput(string Name, IReadOnlyList<string>? Permissions);

public sealed record StaffUserDto(Guid Id, string Email, string? DisplayName, bool Active, bool IsAdministrator, IReadOnlyList<string> Roles, bool TwoFactorEnabled = false);

public sealed record StaffUserInput(string Email, string? DisplayName, string Password, IReadOnlyList<string>? Roles);

/// <summary>Staff, their roles and what each role may do. The admin and the API both use it, so both keep the same rules.</summary>
public interface IStaffAdminService
{
    Task<IReadOnlyList<StaffRoleDto>> ListRolesAsync(CancellationToken ct = default);

    Task<StaffRoleDto?> GetRoleAsync(string name, CancellationToken ct = default);

    /// <summary>Creates the role, or when <paramref name="existingName"/> is given, changes that role (and may rename it).</summary>
    [RequiresPermission(Permissions.StaffManage)]
    Task<StaffRoleDto> SaveRoleAsync(StaffRoleInput input, string? existingName = null, CancellationToken ct = default);

    /// <summary>False when there is no such role. Refused while anyone is in it, and always for Administrator.</summary>
    [RequiresPermission(Permissions.StaffManage)]
    Task<bool> DeleteRoleAsync(string name, CancellationToken ct = default);

    Task<IReadOnlyList<StaffUserDto>> ListStaffAsync(CancellationToken ct = default);

    [RequiresPermission(Permissions.StaffManage)]
    Task<StaffUserDto> CreateStaffAsync(StaffUserInput input, CancellationToken ct = default);

    /// <summary>Replaces the person's roles. Null when there is no such person.</summary>
    [RequiresPermission(Permissions.StaffManage)]
    Task<StaffUserDto?> SetRolesAsync(Guid userId, IReadOnlyList<string> roles, CancellationToken ct = default);

    /// <summary>
    /// Turns off someone's two-factor sign-in, for a lost or replaced phone. They sign in with their password and can set it up again. An administrator
    /// cannot read or set anyone's codes. Null when there is no such person.
    /// </summary>
    [RequiresPermission(Permissions.StaffManage)]
    Task<StaffUserDto?> ResetTwoFactorAsync(Guid userId, CancellationToken ct = default);

    /// <summary>Switches a person on or off. Someone who is off cannot sign in and holds no permission at once. Null when there is no such person.</summary>
    [RequiresPermission(Permissions.StaffManage)]
    Task<StaffUserDto?> SetActiveAsync(Guid userId, bool active, CancellationToken ct = default);
}

internal sealed class StaffAdminService(
    UserManager<PiranhaCommerceUser> users,
    RoleManager<PiranhaCommerceRole> roles,
    PermissionCatalog catalog,
    StaffPermissionService permissions,
    IServiceProvider services) : IStaffAdminService
{
    private const string Administrator = PiranhaCommerceRoles.Administrator;

    private IAuditTrail? Trail => services.GetService<IAuditTrail>();

    public async Task<IReadOnlyList<StaffRoleDto>> ListRolesAsync(CancellationToken ct = default)
    {
        var result = new List<StaffRoleDto>();
        foreach (var role in roles.Roles.OrderBy(r => r.Name).ToList()) result.Add(await ToDtoAsync(role));
        // Administrator first, then by name (sorted here: databases differ on how text sorts).
        return result.OrderByDescending(r => r.IsAdministrator).ThenBy(r => r.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    public async Task<StaffRoleDto?> GetRoleAsync(string name, CancellationToken ct = default) =>
        await roles.FindByNameAsync(name) is { } role ? await ToDtoAsync(role) : null;

    public async Task<StaffRoleDto> SaveRoleAsync(StaffRoleInput input, string? existingName = null, CancellationToken ct = default)
    {
        var name = (input.Name ?? "").Trim();
        if (name.Length == 0) throw new PiranhaCommerceValidationException(nameof(input.Name), "A role needs a name.");
        if (name.Length > 60) throw new PiranhaCommerceValidationException(nameof(input.Name), "A role's name can be at most 60 characters.");
        if (string.Equals(name, Administrator, StringComparison.OrdinalIgnoreCase) && !string.Equals(existingName, Administrator, StringComparison.Ordinal))
            throw new PiranhaCommerceValidationException(nameof(input.Name), "Administrator is the built-in role that can do everything. Choose another name.");

        var keys = (input.Permissions ?? []).Distinct(StringComparer.Ordinal).ToList();
        var unknown = keys.Where(k => !catalog.Exists(k)).ToList();
        if (unknown.Count > 0) throw new PiranhaCommerceValidationException(nameof(input.Permissions), "Unknown permission: " + string.Join(", ", unknown) + ".");

        PiranhaCommerceRole role;
        List<string> before;
        if (existingName is null)
        {
            if (await roles.FindByNameAsync(name) is not null)
                throw new PiranhaCommerceValidationException(nameof(input.Name), $"There is already a role called \"{name}\".");
            role = new PiranhaCommerceRole(name);
            Check(await roles.CreateAsync(role));
            before = [];
        }
        else
        {
            role = await roles.FindByNameAsync(existingName) ?? throw new PiranhaCommerceConflictException("That role no longer exists.");
            if (existingName == Administrator) throw new PiranhaCommerceValidationException(nameof(input.Name), "Administrator holds every permission and cannot be edited.");
            if (!string.Equals(existingName, name, StringComparison.Ordinal))
            {
                if (await roles.FindByNameAsync(name) is { } other && other.Id != role.Id)
                    throw new PiranhaCommerceValidationException(nameof(input.Name), $"There is already a role called \"{name}\".");
                Check(await roles.SetRoleNameAsync(role, name));
                Check(await roles.UpdateAsync(role));
            }
            before = (await roles.GetClaimsAsync(role)).Where(c => c.Type == StaffPermissionService.ClaimType).Select(c => c.Value).ToList();
        }

        // Replace the set: remove what is no longer ticked, add what is new.
        foreach (var gone in before.Except(keys))
            Check(await roles.RemoveClaimAsync(role, new Claim(StaffPermissionService.ClaimType, gone)));
        foreach (var added in keys.Except(before))
            Check(await roles.AddClaimAsync(role, new Claim(StaffPermissionService.ClaimType, added)));

        permissions.Invalidate();
        await RecordAsync("StaffRole", name, existingName is null ? "created" : "changed",
            [new AuditChange("Permissions", Join(before), Join(keys)), .. existingName is not null && existingName != name ? [new AuditChange("Name", existingName, name)] : Array.Empty<AuditChange>()], ct);
        return await ToDtoAsync(role);
    }

    public async Task<bool> DeleteRoleAsync(string name, CancellationToken ct = default)
    {
        if (name == Administrator) throw new PiranhaCommerceConflictException("Administrator is built in and cannot be deleted.");
        if (await roles.FindByNameAsync(name) is not { } role) return false;
        var members = await users.GetUsersInRoleAsync(name);
        if (members.Count > 0)
            throw new PiranhaCommerceConflictException($"{members.Count} {(members.Count == 1 ? "person is" : "people are")} in \"{name}\". Move them to another role first.");
        var held = (await roles.GetClaimsAsync(role)).Where(c => c.Type == StaffPermissionService.ClaimType).Select(c => c.Value).ToList();
        Check(await roles.DeleteAsync(role));
        permissions.Invalidate();
        await RecordAsync("StaffRole", name, "deleted", [new AuditChange("Permissions", Join(held), null)], ct);
        return true;
    }

    public async Task<IReadOnlyList<StaffUserDto>> ListStaffAsync(CancellationToken ct = default)
    {
        var result = new List<StaffUserDto>();
        foreach (var user in users.Users.ToList().Where(u => !IsServiceAccount(u))) result.Add(await ToDtoAsync(user)); // API keys have their own page
        return result.OrderBy(u => u.Email, StringComparer.OrdinalIgnoreCase).ToList();
    }

    public async Task<StaffUserDto> CreateStaffAsync(StaffUserInput input, CancellationToken ct = default)
    {
        var email = (input.Email ?? "").Trim();
        if (email.Length == 0 || !email.Contains('@')) throw new PiranhaCommerceValidationException(nameof(input.Email), "Enter an email address.");
        if (await users.FindByEmailAsync(email) is not null) throw new PiranhaCommerceValidationException(nameof(input.Email), "There is already someone with that email address.");
        var chosen = await ValidRolesAsync(input.Roles ?? [], nameof(input.Roles));

        var user = new PiranhaCommerceUser { UserName = email, Email = email, EmailConfirmed = true, DisplayName = string.IsNullOrWhiteSpace(input.DisplayName) ? null : input.DisplayName.Trim() };
        var created = await users.CreateAsync(user, input.Password ?? "");
        if (!created.Succeeded)
            throw new PiranhaCommerceValidationException(nameof(input.Password), string.Join(" ", created.Errors.Select(e => e.Description)));
        if (chosen.Count > 0) Check(await users.AddToRolesAsync(user, chosen));

        permissions.Invalidate();
        await RecordAsync("StaffUser", email, "created", [new AuditChange("Roles", null, Join(chosen))], ct);
        return await ToDtoAsync(user);
    }

    public async Task<StaffUserDto?> SetRolesAsync(Guid userId, IReadOnlyList<string> roleNames, CancellationToken ct = default)
    {
        if (await users.FindByIdAsync(userId.ToString()) is not { } user) return null;
        var chosen = await ValidRolesAsync(roleNames, nameof(roleNames));
        var current = (await users.GetRolesAsync(user)).ToList();

        if (current.Contains(Administrator) && !chosen.Contains(Administrator) && user.Active)
            await EnsureAnotherAdministratorAsync(user);

        var remove = current.Except(chosen).ToList();
        var add = chosen.Except(current).ToList();
        if (remove.Count > 0) Check(await users.RemoveFromRolesAsync(user, remove));
        if (add.Count > 0) Check(await users.AddToRolesAsync(user, add));

        permissions.Invalidate();
        if (remove.Count + add.Count > 0) await RecordAsync("StaffUser", user.Email ?? userId.ToString(), "changed", [new AuditChange("Roles", Join(current), Join(chosen))], ct);
        return await ToDtoAsync(user);
    }

    public async Task<StaffUserDto?> SetActiveAsync(Guid userId, bool active, CancellationToken ct = default)
    {
        if (await users.FindByIdAsync(userId.ToString()) is not { } user) return null;
        if (user.Active == active) return await ToDtoAsync(user);
        if (!active && await users.IsInRoleAsync(user, Administrator)) await EnsureAnotherAdministratorAsync(user);

        user.Active = active;
        Check(await users.UpdateAsync(user));
        permissions.Invalidate();
        await RecordAsync("StaffUser", user.Email ?? userId.ToString(), "changed", [new AuditChange("Active", (!active).ToString(), active.ToString())], ct);
        return await ToDtoAsync(user);
    }

    public async Task<StaffUserDto?> ResetTwoFactorAsync(Guid userId, CancellationToken ct = default)
    {
        if (await users.FindByIdAsync(userId.ToString()) is not { } user) return null;
        if (!user.TwoFactorEnabled) return await ToDtoAsync(user);
        Check(await users.SetTwoFactorEnabledAsync(user, false));
        await users.ResetAuthenticatorKeyAsync(user);
        await users.RemoveAuthenticationTokenAsync(user, "[AspNetUserStore]", "RecoveryCodes");
        await RecordAsync("StaffUser", user.Email ?? userId.ToString(), "two-factor reset", [new AuditChange("TwoFactor", "On", "Off")], ct);
        return await ToDtoAsync(user);
    }

    /// <summary>Someone must always be able to run the shop: refuse a change that would leave no active administrator.</summary>
    private async Task EnsureAnotherAdministratorAsync(PiranhaCommerceUser leaving)
    {
        var others = (await users.GetUsersInRoleAsync(Administrator)).Where(u => u.Active && u.Id != leaving.Id && !IsServiceAccount(u)).ToList(); // a key is not a person who can sign in
        if (others.Count == 0) throw new PiranhaCommerceConflictException("There must always be at least one active administrator. Make someone else an administrator first.");
    }

    private static bool IsServiceAccount(PiranhaCommerceUser u) => u.Email?.EndsWith(ApiKeyFormat.ServiceDomain, StringComparison.OrdinalIgnoreCase) == true;

    private async Task<List<string>> ValidRolesAsync(IReadOnlyList<string> names, string field)
    {
        var chosen = new List<string>();
        foreach (var name in names.Distinct(StringComparer.Ordinal))
        {
            if (await roles.FindByNameAsync(name) is not { } role) throw new PiranhaCommerceValidationException(field, $"There is no role called \"{name}\".");
            chosen.Add(role.Name!);
        }
        return chosen;
    }

    private async Task<StaffRoleDto> ToDtoAsync(PiranhaCommerceRole role)
    {
        var name = role.Name!;
        var keys = (await roles.GetClaimsAsync(role)).Where(c => c.Type == StaffPermissionService.ClaimType).Select(c => c.Value).OrderBy(k => k, StringComparer.Ordinal).ToList();
        var members = (await users.GetUsersInRoleAsync(name)).Count;
        return new StaffRoleDto(name, name == Administrator, name == Administrator ? [] : keys, members);
    }

    private async Task<StaffUserDto> ToDtoAsync(PiranhaCommerceUser user)
    {
        var names = (await users.GetRolesAsync(user)).OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList();
        return new StaffUserDto(user.Id, user.Email ?? "", user.DisplayName, user.Active, names.Contains(Administrator), names, user.TwoFactorEnabled);
    }

    private async Task RecordAsync(string entityType, string id, string what, IReadOnlyList<AuditChange> changes, CancellationToken ct)
    {
        if (Trail is { } trail) await trail.RecordUpdateAsync(entityType, id, $"{entityType} {id} {what}", changes, ct);
    }

    private static string Join(IEnumerable<string> keys) => string.Join(", ", keys.OrderBy(k => k, StringComparer.Ordinal));

    private static void Check(IdentityResult result)
    {
        if (!result.Succeeded) throw new PiranhaCommerceConflictException(string.Join(" ", result.Errors.Select(e => e.Description)));
    }
}
