namespace PiranhaCommerce.Core.Audit;

/// <summary>Put on an entity class to have every create, change and delete of it recorded in the audit log (when the Audit package is installed).</summary>
[AttributeUsage(AttributeTargets.Class)]
public sealed class AuditedAttribute : Attribute;

/// <summary>Put on a property that holds personal or secret data: the audit log records that it changed, never its value.</summary>
[AttributeUsage(AttributeTargets.Property)]
public sealed class AuditMaskAttribute : Attribute;

/// <summary>
/// Who is making the changes in the current request or admin operation. The API and the admin fill it in from the signed-in user;
/// the audit log reads it when saving. Left empty (background work, a guest at the checkout) the entry shows no person.
/// </summary>
public sealed class AuditActor
{
    public string? Name { get; set; }
}

/// <param name="From">Null when created, or when the property is private.</param>
/// <param name="To">Null when deleted or cleared, or when the property is private.</param>
/// <param name="Hidden">True when the value is private: only the fact that it changed is recorded.</param>
public sealed record AuditChange(string Property, string? From, string? To, bool Hidden = false);

/// <summary>
/// For the few changes made with set-based SQL (which the audit log cannot see on its own), a service says what it changed. Optional: when the Audit
/// package is not installed nothing is registered and the service skips it. Call it inside the same transaction as the change.
/// </summary>
public interface IAuditTrail
{
    Task RecordUpdateAsync(string entityType, string entityId, string? label, IReadOnlyList<AuditChange> changes, CancellationToken ct = default);
}
