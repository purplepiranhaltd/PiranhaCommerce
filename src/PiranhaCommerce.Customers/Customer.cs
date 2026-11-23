using PiranhaCommerce.Core.Domain;
using PiranhaCommerce.Core.Audit;

namespace PiranhaCommerce.Customers;

/// <summary>
/// A person or business that buys, shared by every sales channel. Not an admin user: a customer may later be linked to a
/// storefront login, but holds no permissions of its own.
/// </summary>
[Audited]
public class Customer : AuditableEntity
{
    [AuditMask] public required string Email { get; set; }

    /// <summary>Canonical form (trimmed, upper-case) used for uniqueness and lookup, so the database guarantees one customer per email.</summary>
    [AuditMask] public required string NormalizedEmail { get; set; }

    [AuditMask] public string? FirstName { get; set; }
    [AuditMask] public string? LastName { get; set; }
    [AuditMask] public string? Phone { get; set; }

    /// <summary>Staff-only notes. Never shown to the customer.</summary>
    [AuditMask] public string? Notes { get; set; }

    public bool Active { get; set; } = true;
}

/// <summary>A postal address in a customer's address book. Orders copy the address they use, so editing this never changes history.</summary>
[Audited]
public class CustomerAddress : Entity
{
    public Guid CustomerId { get; set; }
    [AuditMask] public string? Label { get; set; }
    [AuditMask] public string? Recipient { get; set; }
    [AuditMask] public required string Line1 { get; set; }
    [AuditMask] public string? Line2 { get; set; }
    [AuditMask] public required string City { get; set; }
    [AuditMask] public string? Region { get; set; }
    [AuditMask] public string? PostalCode { get; set; }

    /// <summary>ISO 3166-1 alpha-2, upper-case (for example <c>GB</c>).</summary>
    [AuditMask] public required string CountryCode { get; set; }
}
