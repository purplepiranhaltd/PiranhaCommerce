namespace PiranhaCommerce.Core.Identity;

public enum CredentialStatus
{
    /// <summary>Email and password are right, the account is active and not locked out.</summary>
    Valid,

    /// <summary>Unknown email, inactive account or wrong password. Deliberately not distinguished.</summary>
    Invalid,

    /// <summary>Too many failed attempts; the account refuses every password until the lockout ends.</summary>
    LockedOut,
}

public sealed record CredentialResult(CredentialStatus Status, PiranhaUser? User)
{
    public bool Succeeded => Status == CredentialStatus.Valid;
}

/// <summary>
/// The one place an admin email and password are checked, for the sign-in page and the API token endpoint alike, so both enforce
/// the same lockout. Failed attempts are counted with atomic database updates: a burst of simultaneous guesses cannot slip past
/// the limit through lost updates, and simultaneous correct sign-ins cannot refuse each other.
/// </summary>
public interface ICredentialChecker
{
    Task<CredentialResult> CheckAsync(string email, string password, CancellationToken ct = default);
}
