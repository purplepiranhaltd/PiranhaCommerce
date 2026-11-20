using PiranhaCommerce.Core.Admin;

namespace PiranhaCommerce.Admin.Services;

public sealed class AdminMenuService(IEnumerable<IAdminMenuContributor> contributors)
{
    public IReadOnlyList<AdminMenuItem> GetMenu() =>
        contributors.SelectMany(c => c.GetMenuItems()).OrderBy(i => i.Order).ToList();
}
