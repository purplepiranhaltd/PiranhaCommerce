using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.Options;

namespace PiranhaCommerce.Core.Identity;

/// <summary>Names of the authorization policies that stand for a permission.</summary>
public static class PermissionPolicies
{
    public const string Prefix = "permission:";

    /// <summary>The policy name for a permission key, for <c>[Authorize(Policy = ...)]</c> and <c>AuthorizeView Policy="..."</c>.</summary>
    public static string For(string permissionKey) => Prefix + permissionKey;
}

public sealed class PermissionRequirement(string key) : IAuthorizationRequirement
{
    public string Key { get; } = key;
}

internal sealed class PermissionHandler(StaffPermissionService permissions) : AuthorizationHandler<PermissionRequirement>
{
    protected override async Task HandleRequirementAsync(AuthorizationHandlerContext context, PermissionRequirement requirement)
    {
        if (await permissions.HasAsync(context.User, requirement.Key)) context.Succeed(requirement);
    }
}

/// <summary>Builds a policy for any name that starts with <c>permission:</c>, so a package's permissions need no registration of their own.</summary>
internal sealed class PermissionPolicyProvider(IOptions<AuthorizationOptions> options) : DefaultAuthorizationPolicyProvider(options)
{
    public override Task<AuthorizationPolicy?> GetPolicyAsync(string policyName)
    {
        if (!policyName.StartsWith(PermissionPolicies.Prefix, StringComparison.Ordinal)) return base.GetPolicyAsync(policyName);
        var policy = new AuthorizationPolicyBuilder()
            .RequireAuthenticatedUser()
            .AddRequirements(new PermissionRequirement(policyName[PermissionPolicies.Prefix.Length..]))
            .Build();
        return Task.FromResult<AuthorizationPolicy?>(policy);
    }
}
