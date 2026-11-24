using PiranhaCommerce.Core.Audit;

namespace PiranhaCommerce.Core.Identity;

/// <summary>
/// A key for another system to call the API. It is not a person: it is a service account with its own roles, so it can do exactly what those roles
/// allow and nothing else, and it shows in the audit log under its own name. Only a hash of the secret is stored.
/// </summary>
[Audited]
public class ApiKey
{
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>What the key is for, e.g. "Warehouse system".</summary>
    public string Name { get; set; } = "";

    /// <summary>The service-account user the key acts as. Its roles decide what the key may do.</summary>
    public Guid UserId { get; set; }

    /// <summary>The first part of the key, kept in the clear to find it and to let a person recognise it.</summary>
    public string Prefix { get; set; } = "";

    [AuditMask]
    public string SecretHash { get; set; } = "";

    public DateTimeOffset CreatedUtc { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? ExpiresUtc { get; set; }
    public DateTimeOffset? LastUsedUtc { get; set; }
    public DateTimeOffset? RevokedUtc { get; set; }
}

public sealed record ApiKeyDto(Guid Id, string Name, string Prefix, IReadOnlyList<string> Roles, DateTimeOffset CreatedUtc, DateTimeOffset? ExpiresUtc,
    DateTimeOffset? LastUsedUtc, DateTimeOffset? RevokedUtc)
{
    public bool IsActive(DateTimeOffset now) => RevokedUtc is null && (ExpiresUtc is null || ExpiresUtc > now);
}

public sealed record ApiKeyInput(string Name, IReadOnlyList<string>? Roles, DateTimeOffset? ExpiresUtc = null);

/// <param name="Secret">The whole key. Shown once, when it is made; it cannot be read again.</param>
public sealed record ApiKeyCreated(ApiKeyDto Key, string Secret);

/// <summary>Who an accepted key acts as.</summary>
public sealed record ApiKeyIdentity(Guid UserId, string Email, string Name, IReadOnlyList<string> Roles);

/// <summary>Managing API keys, from the admin.</summary>
public interface IApiKeyService
{
    Task<IReadOnlyList<ApiKeyDto>> ListAsync(CancellationToken ct = default);

    [RequiresPermission(Permissions.StaffManage)]
    Task<ApiKeyCreated> CreateAsync(ApiKeyInput input, CancellationToken ct = default);

    /// <summary>Changes which roles the key acts with. Null when there is no such key.</summary>
    [RequiresPermission(Permissions.StaffManage)]
    Task<ApiKeyDto?> SetRolesAsync(Guid id, IReadOnlyList<string> roles, CancellationToken ct = default);

    /// <summary>Stops the key working at once. It cannot be switched back on: make a new one. False when there is no such key.</summary>
    [RequiresPermission(Permissions.StaffManage)]
    Task<bool> RevokeAsync(Guid id, CancellationToken ct = default);
}

/// <summary>Checks a key presented to the API. Used by the API's sign-in; not for pages.</summary>
public interface IApiKeyAuthenticator
{
    /// <summary>Null for anything that is not a live key.</summary>
    Task<ApiKeyIdentity?> AuthenticateAsync(string presented, CancellationToken ct = default);
}

public static class ApiKeyFormat
{
    /// <summary>Every key starts with this, so it is recognised among tokens and picked up by secret scanners.</summary>
    public const string Marker = "pck_";

    /// <summary>The address a service account is given; staff lists leave these out.</summary>
    public const string ServiceDomain = "@apikeys.invalid";

    public static bool LooksLikeKey(string? value) => value is not null && value.StartsWith(Marker, StringComparison.Ordinal);
}
