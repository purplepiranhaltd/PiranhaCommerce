using PiranhaCommerce.Core.Admin;
using PiranhaCommerce.Core.Identity;

namespace PiranhaCommerce.Admin;

/// <summary>The admin's own menu entries: the pages that belong to the admin itself, not to a package.</summary>
internal sealed class AdminCoreMenu : IAdminMenuContributor
{
    public IEnumerable<AdminMenuItem> GetMenuItems() =>
    [
        new("staff", "Staff", Icon: "users", Order: 85),
        new("staff.roles", "Staff and roles", "/admin/staff", Order: 86, ParentKey: "staff", RequiredPermission: Permissions.StaffManage),
    ];
}
