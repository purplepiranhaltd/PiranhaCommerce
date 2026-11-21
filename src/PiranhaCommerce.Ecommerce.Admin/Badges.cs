using Microsoft.Extensions.DependencyInjection;
using PiranhaCommerce.Core.Admin;

namespace PiranhaCommerce.Ecommerce.Admin;

internal sealed class UnreconciledBadge : IAdminBadgeProvider
{
    public string Key => "unreconciled";
    public Task<int> CountAsync(IServiceProvider services, CancellationToken ct = default) => services.GetRequiredService<IUnreconciledPaymentService>().OpenCountAsync(ct);
}

internal sealed class DisputesBadge : IAdminBadgeProvider
{
    public string Key => "disputes";
    public Task<int> CountAsync(IServiceProvider services, CancellationToken ct = default) => services.GetRequiredService<IDisputeService>().OpenCountAsync(ct);
}

internal sealed class ReturnRequestsBadge : IAdminBadgeProvider
{
    public string Key => "return-requests";
    public Task<int> CountAsync(IServiceProvider services, CancellationToken ct = default) => services.GetRequiredService<IReturnRequestService>().WaitingCountAsync(ct);
}
