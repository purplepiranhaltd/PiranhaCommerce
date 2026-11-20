namespace PiranhaCommerce.Core.Domain;

/// <summary>Base type for all persisted entities.</summary>
public abstract class Entity
{
    public Guid Id { get; set; } = Guid.NewGuid();
}

/// <summary>Entity that tracks creation and modification time (UTC).</summary>
public abstract class AuditableEntity : Entity
{
    public DateTimeOffset CreatedUtc { get; set; }
    public DateTimeOffset? UpdatedUtc { get; set; }
}
