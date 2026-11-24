using PiranhaCommerce.Core.Admin;
using PiranhaCommerce.Core.Identity;

namespace PiranhaCommerce.Admin;

/// <summary>The admin's own menu entries: the pages that belong to the admin itself, not to a package.</summary>
internal sealed class AdminCoreMenu : IAdminMenuContributor
{
    public IEnumerable<AdminMenuItem> GetMenuItems() =>
    [
        new("account", "My account", Icon: "shield", Order: 84),
        new("account.security", "Sign-in security", "/admin/account/security", Order: 84, ParentKey: "account", RequiredPermission: Permissions.Access),
        new("staff", "Staff", Icon: "users", Order: 85),
        new("staff.roles", "Staff and roles", "/admin/staff", Order: 86, ParentKey: "staff", RequiredPermission: Permissions.StaffManage),
        new("staff.apikeys", "API keys", "/admin/staff/api-keys", Order: 87, ParentKey: "staff", RequiredPermission: Permissions.StaffManage),
    ];
}
