using PiranhaCommerce.Core.Domain;

namespace PiranhaCommerce.Customers;

/// <summary>
/// A person or business that buys, shared by every sales channel. Not an admin user: a customer may later be linked to a
/// storefront login, but holds no permissions of its own.
/// </summary>
public class Customer : AuditableEntity
{
    public required string Email { get; set; }

    /// <summary>Canonical form (trimmed, upper-case) used for uniqueness and lookup, so the database guarantees one customer per email.</summary>
    public required string NormalizedEmail { get; set; }

    public string? FirstName { get; set; }
    public string? LastName { get; set; }
    public string? Phone { get; set; }

    /// <summary>Staff-only notes. Never shown to the customer.</summary>
    public string? Notes { get; set; }

    public bool Active { get; set; } = true;
}

/// <summary>A postal address in a customer's address book. Orders copy the address they use, so editing this never changes history.</summary>
public class CustomerAddress : Entity
{
    public Guid CustomerId { get; set; }
    public string? Label { get; set; }
    public string? Recipient { get; set; }
    public required string Line1 { get; set; }
    public string? Line2 { get; set; }
    public required string City { get; set; }
    public string? Region { get; set; }
    public string? PostalCode { get; set; }

    /// <summary>ISO 3166-1 alpha-2, upper-case (for example <c>GB</c>).</summary>
    public required string CountryCode { get; set; }
}
