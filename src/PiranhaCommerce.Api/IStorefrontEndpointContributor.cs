using Microsoft.AspNetCore.Routing;

namespace PiranhaCommerce.Api;

/// <summary>
/// Contributes endpoints for shoppers (under <c>/api/v1/storefront</c>). Every endpoint in the group requires a shopper token
/// unless it opts out with <c>AllowAnonymous()</c>, so a forgotten attribute leaves an endpoint closed, never open. Shopper tokens
/// are not accepted by the staff API, and staff tokens are not accepted here.
/// </summary>
public interface IStorefrontEndpointContributor
{
    void MapEndpoints(IEndpointRouteBuilder storefront);
}
