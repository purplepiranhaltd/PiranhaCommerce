using Microsoft.AspNetCore.Components.Authorization;

namespace PiranhaCommerce.Admin.Services;

public static class AdminActor
{
    /// <summary>
    /// Who is making a change in the admin, as the services record it ("edited by", "resolved by", the payment ledger's actor): the
    /// signed-in user's email, otherwise their name, otherwise null. Always read it from the signed-in user, never from the form.
    /// </summary>
    public static async Task<string?> GetActorAsync(this AuthenticationStateProvider auth)
    {
        var user = (await auth.GetAuthenticationStateAsync()).User;
        if (user.Identity?.IsAuthenticated != true) return null;
        return user.FindFirst("email")?.Value ?? user.FindFirst(System.Security.Claims.ClaimTypes.Email)?.Value ?? user.Identity.Name;
    }
}
