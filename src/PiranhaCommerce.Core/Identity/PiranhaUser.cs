using Microsoft.AspNetCore.Identity;

namespace PiranhaCommerce.Core.Identity;

/// <summary>Back-office/API user. Customers and EPOS operators build on this in their own packages.</summary>
public class PiranhaUser : IdentityUser<Guid>
{
    public string? DisplayName { get; set; }
    public bool Active { get; set; } = true;
    public DateTimeOffset CreatedUtc { get; set; } = DateTimeOffset.UtcNow;
}

public class PiranhaRole : IdentityRole<Guid>
{
    public PiranhaRole() { }
    public PiranhaRole(string name) : base(name) { }
}

/// <summary>Built-in role names.</summary>
public static class PiranhaRoles
{
    public const string Administrator = "Administrator";
}
