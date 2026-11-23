using System.Threading.RateLimiting;
using Microsoft.Extensions.Options;

namespace PiranhaCommerce.Web.Shop;

/// <summary>
/// A limit on how often one client may post the shop's forms. It sits in the form routes themselves rather than in the pipeline, so it
/// does not depend on where the host puts its rate limiter middleware. Clients are told apart by address; behind a proxy, configure forwarded headers
/// so that is the visitor's address and not the proxy's.
/// </summary>
public sealed class ShopRateLimiter(IOptions<ShopOptions> options) : IDisposable
{
    private readonly PartitionedRateLimiter<string> _limiter = PartitionedRateLimiter.Create<string, string>(client =>
        RateLimitPartition.GetFixedWindowLimiter(client, _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = Math.Max(1, options.Value.FormsPerMinute), Window = TimeSpan.FromMinutes(1), QueueLimit = 0,
        }));

    // Sign-in, registration and password-reset forms get a much lower limit: they are what a script guessing passwords would hit.
    private readonly PartitionedRateLimiter<string> _accounts = PartitionedRateLimiter.Create<string, string>(client =>
        RateLimitPartition.GetFixedWindowLimiter(client, _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = Math.Max(1, options.Value.AccountFormsPerMinute), Window = TimeSpan.FromMinutes(1), QueueLimit = 0,
        }));

    public bool Allow(HttpContext http, bool account = false)
    {
        using var lease = (account ? _accounts : _limiter).AttemptAcquire(http.Connection.RemoteIpAddress?.ToString() ?? "unknown");
        return lease.IsAcquired;
    }

    public void Dispose() { _limiter.Dispose(); _accounts.Dispose(); }
}
