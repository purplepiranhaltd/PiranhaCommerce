using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using PiranhaCommerce.Core.Identity;

namespace PiranhaCommerce.Api;

public static class PermissionEndpointExtensions
{
    /// <summary>
    /// Requires a signed-in member of staff (a staff token) who holds the permission. Put it on a route group to guard reads, and on a write
    /// endpoint inside it to ask for more: both must pass. A permission that <em>implies</em> another (editing implies viewing) satisfies it.
    /// Administrators hold every permission.
    /// </summary>
    public static TBuilder RequirePermission<TBuilder>(this TBuilder builder, string permissionKey) where TBuilder : IEndpointConventionBuilder =>
        builder.RequireAuthorization(new AuthorizeAttribute
        {
            Policy = PermissionPolicies.For(permissionKey),
            AuthenticationSchemes = PiranhaCommerceApiExtensions.JwtScheme,
        });

    /// <summary>
    /// Secure by default: a staff endpoint that names no permission (a plugin written before permissions, or one that forgot) is open to
    /// administrators only, never to every signed-in member of staff.
    /// </summary>
    internal static void RequireAdministratorUnlessPermissionNamed(EndpointBuilder endpoint)
    {
        var data = endpoint.Metadata.OfType<IAuthorizeData>().ToList();
        if (endpoint.Metadata.OfType<IAllowAnonymous>().Any()) return;
        if (data.Any(d => d.Policy is { } p && p.StartsWith(PermissionPolicies.Prefix, StringComparison.Ordinal))) return;
        endpoint.Metadata.Add(new AuthorizeAttribute
        {
            Policy = PermissionPolicies.For(Permissions.Administrator),
            AuthenticationSchemes = PiranhaCommerceApiExtensions.JwtScheme,
        });
    }
}
