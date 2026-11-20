using System.Net;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace PiranhaCommerce.Api.RateLimiting;

/// <summary>
/// Limits password guessing per client address. Account lockout caps guesses at one account; this caps how fast
/// one client can try, including one guess each at many accounts. Bound to <c>Piranha:RateLimit</c>.
/// </summary>
public sealed class SignInRateLimitOptions
{
    public const string SectionName = "Piranha:RateLimit";

    /// <summary>Set false to turn the limit off (for example behind a gateway that already limits).</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Sign-in attempts one client address may make in each window.</summary>
    public int SignInAttempts { get; set; } = 10;

    /// <summary>Window length in seconds.</summary>
    public int WindowSeconds { get; set; } = 60;
}

internal static class SignInRateLimiting
{
    /// <summary>The sign-in POSTs: the API token endpoint (under any prefix) and the admin login form.</summary>
    internal static bool IsSignIn(HttpContext http) =>
        HttpMethods.IsPost(http.Request.Method)
        && (http.Request.Path.Value is { } p)
        && (p.EndsWith("/auth/token", StringComparison.OrdinalIgnoreCase)
            || p.EndsWith("/auth/register", StringComparison.OrdinalIgnoreCase)
            || p.EndsWith("/auth/forgot-password", StringComparison.OrdinalIgnoreCase)
            || p.EndsWith("/auth/reset-password", StringComparison.OrdinalIgnoreCase)
            || p.EndsWith("/me/password", StringComparison.OrdinalIgnoreCase)
            || p.Equals("/admin/login", StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// One bucket per client. IPv6 clients are grouped by their /64, because one machine typically owns a whole /64
    /// and could otherwise try a fresh address for every guess.
    /// </summary>
    internal static string ClientKey(HttpContext http)
    {
        var ip = http.Connection.RemoteIpAddress;
        if (ip is null) return "unknown";
        if (ip.IsIPv4MappedToIPv6) ip = ip.MapToIPv4();
        if (ip.AddressFamily != System.Net.Sockets.AddressFamily.InterNetworkV6) return ip.ToString();
        var bytes = ip.GetAddressBytes();
        Array.Clear(bytes, 8, 8);
        return new IPAddress(bytes).ToString() + "/64";
    }

    internal static IServiceCollection AddSignInRateLimiting(this IServiceCollection services)
    {
        services.AddOptions<SignInRateLimitOptions>().BindConfiguration(SignInRateLimitOptions.SectionName);

        services.AddRateLimiter(_ => { });
        services.AddOptions<Microsoft.AspNetCore.RateLimiting.RateLimiterOptions>().Configure<IOptions<SignInRateLimitOptions>>((o, settings) =>
        {
            var s = settings.Value;
            o.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            o.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(http =>
            {
                if (!s.Enabled || !IsSignIn(http)) return RateLimitPartition.GetNoLimiter("none");
                return RateLimitPartition.GetSlidingWindowLimiter(ClientKey(http), _ => new SlidingWindowRateLimiterOptions
                {
                    PermitLimit = Math.Max(1, s.SignInAttempts),
                    Window = TimeSpan.FromSeconds(Math.Max(1, s.WindowSeconds)),
                    SegmentsPerWindow = 6,
                    QueueLimit = 0,
                });
            });
            o.OnRejected = (context, _) =>
            {
                context.HttpContext.Response.Headers.RetryAfter = Math.Max(1, s.WindowSeconds).ToString();
                return ValueTask.CompletedTask;
            };
        });

        // The host does not have to remember to call UseRateLimiter: it is added ahead of the rest of the pipeline.
        services.AddTransient<IStartupFilter, SignInRateLimitStartupFilter>();
        return services;
    }

    private sealed class SignInRateLimitStartupFilter : IStartupFilter
    {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
        {
            app.UseRateLimiter();
            next(app);
        };
    }
}
