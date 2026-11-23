using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using PiranhaCommerce.Core.Audit;

namespace PiranhaCommerce.Api;

/// <summary>Tells the audit log (when installed) who is calling, from the verified token: a staff user's email, or "shopper:{id}" for a shopper.</summary>
internal sealed class AuditActorFilter : IEndpointFilter
{
    public ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        var http = context.HttpContext;
        if (http.RequestServices.GetService(typeof(AuditActor)) is AuditActor actor)
        {
            var user = http.User;
            if (user.Identity?.IsAuthenticated == true)
            {
                var email = user.FindFirstValue("email") ?? user.FindFirstValue(ClaimTypes.Email);
                var customer = user.FindFirstValue("sub");
                actor.Name = http.Request.Path.Value?.Contains("/storefront/", StringComparison.OrdinalIgnoreCase) == true
                    ? (customer is null ? "shopper" : "shopper:" + customer)
                    : email ?? user.Identity.Name;
            }
        }
        return next(context);
    }
}
