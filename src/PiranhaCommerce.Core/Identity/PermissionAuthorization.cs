using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.Options;

namespace PiranhaCommerce.Core.Identity;

/// <summary>Names of the authorization policies that stand for a permission.</summary>
public static class PermissionPolicies
{
    public const string Prefix = "permission:";

    /// <summary>The policy name for a permission key, for <c>[Authorize(Policy = ...)]</c> and <c>AuthorizeView Policy="..."</c>.</summary>
    public static string For(string permissionKey) => Prefix + permissionKey;

    // The built-in ones as constants, because an attribute needs one: [Authorize(Policy = PermissionPolicies.OrdersView)].
    public const string CatalogView = Prefix + Permissions.CatalogView;
    public const string CatalogEdit = Prefix + Permissions.CatalogEdit;
    public const string PricesManage = Prefix + Permissions.PricesManage;
    public const string StockAdjust = Prefix + Permissions.StockAdjust;
    public const string StockTransfer = Prefix + Permissions.StockTransfer;
    public const string OrdersView = Prefix + Permissions.OrdersView;
    public const string OrdersCreate = Prefix + Permissions.OrdersCreate;
    public const string OrdersFulfil = Prefix + Permissions.OrdersFulfil;
    public const string OrdersRefund = Prefix + Permissions.OrdersRefund;
    public const string VouchersManage = Prefix + Permissions.VouchersManage;
    public const string CustomersView = Prefix + Permissions.CustomersView;
    public const string CustomersManage = Prefix + Permissions.CustomersManage;
    public const string ReviewsModerate = Prefix + Permissions.ReviewsModerate;
    public const string SettingsManage = Prefix + Permissions.SettingsManage;
    public const string ReportsView = Prefix + Permissions.ReportsView;
    public const string AuditView = Prefix + Permissions.AuditView;
    public const string WebhooksManage = Prefix + Permissions.WebhooksManage;
    public const string StaffManage = Prefix + Permissions.StaffManage;
    public const string Access = Prefix + Permissions.Access;
    public const string Administrator = Prefix + Permissions.Administrator;
    public const string SignedIn = Prefix + Permissions.SignedIn;
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
