using System.ComponentModel.DataAnnotations;

namespace PiranhaCommerce.Customers;

public sealed class CustomerInput
{
    [Required, EmailAddress, StringLength(256)]
    public string Email { get; set; } = "";

    [StringLength(100)] public string? FirstName { get; set; }
    [StringLength(100)] public string? LastName { get; set; }
    [StringLength(40)] public string? Phone { get; set; }
    [StringLength(2000)] public string? Notes { get; set; }
    public bool Active { get; set; } = true;
}

public sealed record CustomerDto(
    Guid Id, string Email, string? FirstName, string? LastName, string? Phone, string? Notes, bool Active,
    DateTimeOffset CreatedUtc, DateTimeOffset? UpdatedUtc)
{
    /// <summary>First and last name when present, otherwise the email.</summary>
    public string DisplayName => string.Join(' ', new[] { FirstName, LastName }.Where(n => !string.IsNullOrWhiteSpace(n))) is { Length: > 0 } n ? n : Email;
}

public sealed class AddressInput
{
    [StringLength(100)] public string? Label { get; set; }
    [StringLength(200)] public string? Recipient { get; set; }
    [Required, StringLength(200)] public string Line1 { get; set; } = "";
    [StringLength(200)] public string? Line2 { get; set; }
    [Required, StringLength(100)] public string City { get; set; } = "";
    [StringLength(100)] public string? Region { get; set; }
    [StringLength(20)] public string? PostalCode { get; set; }

    [Required, RegularExpression("^[A-Za-z]{2}$", ErrorMessage = "Use a two-letter country code such as GB.")]
    public string CountryCode { get; set; } = "";
}

public sealed record AddressDto(
    Guid Id, Guid CustomerId, string? Label, string? Recipient, string Line1, string? Line2, string City,
    string? Region, string? PostalCode, string CountryCode);
