namespace PiranhaCommerce.Core.Identity;

/// <summary>Settings under <c>PiranhaCommerce:Staff</c>.</summary>
public sealed class StaffSecurityOptions
{
    public const string SectionName = "PiranhaCommerce:Staff";

    /// <summary>
    /// Every member of staff must use two-factor sign-in. On by default. Someone who has not set it up can sign in to the admin but only to set it up,
    /// and the API refuses them a token. API keys are not people and are not affected. Turn it off only for local development.
    /// </summary>
    public bool RequireTwoFactor { get; set; } = true;
}
