using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using PiranhaCommerce.Core.Identity;
using PiranhaCommerce.Core.Validation;

namespace PiranhaCommerce.Data;

internal sealed class ApiKeyService(
    PiranhaCommerceDbContext db,
    UserManager<PiranhaCommerceUser> users,
    RoleManager<PiranhaCommerceRole> roles,
    StaffPermissionService permissions) : IApiKeyService, IApiKeyAuthenticator
{
    // The last-used time is only written this often, so a busy integration does not write on every call.
    private static readonly TimeSpan UsedGranularity = TimeSpan.FromMinutes(5);

    public async Task<IReadOnlyList<ApiKeyDto>> ListAsync(CancellationToken ct = default)
    {
        var keys = await db.Set<ApiKey>().AsNoTracking().ToListAsync(ct);
        var result = new List<ApiKeyDto>();
        foreach (var k in keys.OrderByDescending(k => k.CreatedUtc)) result.Add(await ToDtoAsync(k));
        return result;
    }

    public async Task<ApiKeyCreated> CreateAsync(ApiKeyInput input, CancellationToken ct = default)
    {
        var name = (input.Name ?? "").Trim();
        if (name.Length == 0) throw new PiranhaCommerceValidationException(nameof(input.Name), "Say what the key is for.");
        if (name.Length > 100) throw new PiranhaCommerceValidationException(nameof(input.Name), "The name can be at most 100 characters.");
        if (input.ExpiresUtc is { } expires && expires <= DateTimeOffset.UtcNow)
            throw new PiranhaCommerceValidationException(nameof(input.ExpiresUtc), "The expiry must be in the future.");
        var chosen = new List<string>();
        foreach (var r in (input.Roles ?? []).Distinct(StringComparer.Ordinal))
            chosen.Add((await roles.FindByNameAsync(r))?.Name ?? throw new PiranhaCommerceValidationException(nameof(input.Roles), $"There is no role called \"{r}\"."));

        var prefix = Convert.ToHexString(RandomNumberGenerator.GetBytes(4)).ToLowerInvariant();
        var secret = $"{ApiKeyFormat.Marker}{prefix}_{Convert.ToHexString(RandomNumberGenerator.GetBytes(24)).ToLowerInvariant()}";

        var id = Guid.NewGuid();
        var email = $"apikey-{id:N}{ApiKeyFormat.ServiceDomain}";
        var user = new PiranhaCommerceUser { UserName = email, Email = email, EmailConfirmed = true, DisplayName = "API key: " + name, LockoutEnabled = false };
        var created = await users.CreateAsync(user); // no password: it cannot sign in, only its key works
        if (!created.Succeeded) throw new PiranhaCommerceConflictException(string.Join(" ", created.Errors.Select(e => e.Description)));
        if (chosen.Count > 0) await users.AddToRolesAsync(user, chosen);

        var key = new ApiKey { Id = id, Name = name, UserId = user.Id, Prefix = prefix, SecretHash = Hash(secret), ExpiresUtc = input.ExpiresUtc };
        db.Add(key);
        await db.SaveChangesAsync(ct);
        permissions.Invalidate();
        return new ApiKeyCreated(await ToDtoAsync(key), secret);
    }

    public async Task<ApiKeyDto?> SetRolesAsync(Guid id, IReadOnlyList<string> roleNames, CancellationToken ct = default)
    {
        var key = await db.Set<ApiKey>().FirstOrDefaultAsync(k => k.Id == id, ct);
        if (key is null || await users.FindByIdAsync(key.UserId.ToString()) is not { } user) return null;
        var chosen = new List<string>();
        foreach (var r in roleNames.Distinct(StringComparer.Ordinal))
            chosen.Add((await roles.FindByNameAsync(r))?.Name ?? throw new PiranhaCommerceValidationException(nameof(roleNames), $"There is no role called \"{r}\"."));
        var current = await users.GetRolesAsync(user);
        if (current.Except(chosen).ToList() is { Count: > 0 } remove) await users.RemoveFromRolesAsync(user, remove);
        if (chosen.Except(current).ToList() is { Count: > 0 } add) await users.AddToRolesAsync(user, add);
        permissions.Invalidate();
        return await ToDtoAsync(key);
    }

    public async Task<bool> RevokeAsync(Guid id, CancellationToken ct = default)
    {
        var key = await db.Set<ApiKey>().FirstOrDefaultAsync(k => k.Id == id, ct);
        if (key is null) return false;
        if (key.RevokedUtc is null)
        {
            key.RevokedUtc = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync(ct);
        }
        // Switching the account off is what stops a call that is already being checked: permissions come from the user.
        if (await users.FindByIdAsync(key.UserId.ToString()) is { Active: true } user)
        {
            user.Active = false;
            await users.UpdateAsync(user);
        }
        permissions.Invalidate();
        return true;
    }

    public async Task<ApiKeyIdentity?> AuthenticateAsync(string presented, CancellationToken ct = default)
    {
        if (!ApiKeyFormat.LooksLikeKey(presented)) return null;
        var parts = presented.Split('_');
        if (parts.Length != 3 || parts[1].Length != 8) return null;

        var candidates = await db.Set<ApiKey>().AsNoTracking().Where(k => k.Prefix == parts[1]).ToListAsync(ct);
        var presentedHash = Encoding.ASCII.GetBytes(Hash(presented));
        var key = candidates.FirstOrDefault(k => CryptographicOperations.FixedTimeEquals(presentedHash, Encoding.ASCII.GetBytes(k.SecretHash)));
        var now = DateTimeOffset.UtcNow;
        if (key is null || key.RevokedUtc is not null || (key.ExpiresUtc is { } e && e <= now)) return null;

        var user = await users.FindByIdAsync(key.UserId.ToString());
        if (user is null || !user.Active) return null;

        if (key.LastUsedUtc is null || now - key.LastUsedUtc > UsedGranularity)
            await db.Set<ApiKey>().Where(k => k.Id == key.Id).ExecuteUpdateAsync(s => s.SetProperty(k => k.LastUsedUtc, now), ct);

        return new ApiKeyIdentity(user.Id, user.Email!, key.Name, (await users.GetRolesAsync(user)).ToList());
    }

    private async Task<ApiKeyDto> ToDtoAsync(ApiKey k)
    {
        var user = await users.FindByIdAsync(k.UserId.ToString());
        var names = user is null ? [] : (await users.GetRolesAsync(user)).OrderBy(r => r, StringComparer.OrdinalIgnoreCase).ToList();
        return new ApiKeyDto(k.Id, k.Name, ApiKeyFormat.Marker + k.Prefix + "_...", names, k.CreatedUtc, k.ExpiresUtc, k.LastUsedUtc, k.RevokedUtc);
    }

    private static string Hash(string secret) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(secret)));
}
