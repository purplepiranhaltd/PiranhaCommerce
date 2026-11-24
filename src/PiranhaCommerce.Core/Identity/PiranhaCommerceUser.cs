using Microsoft.AspNetCore.Identity;

namespace PiranhaCommerce.Core.Identity;

/// <summary>Back-office/API user. Customers and EPOS operators build on this in their own packages.</summary>
public class PiranhaCommerceUser : IdentityUser<Guid>
{
    public string? DisplayName { get; set; }
    public bool Active { get; set; } = true;
    public DateTimeOffset CreatedUtc { get; set; } = DateTimeOffset.UtcNow;
}

public class PiranhaCommerceRole : IdentityRole<Guid>
{
    public PiranhaCommerceRole() { }
    public PiranhaCommerceRole(string name) : base(name) { }
}

/// <summary>Built-in role names.</summary>
public static class PiranhaCommerceRoles
{
    public const string Administrator = "Administrator";
}
