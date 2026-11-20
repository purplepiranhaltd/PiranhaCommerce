using Microsoft.AspNetCore.Routing;

namespace PiranhaCommerce.Api;

/// <summary>Packages/plugins implement this to expose endpoints under the shared /api prefix.</summary>
public interface IApiEndpointContributor
{
    void MapEndpoints(IEndpointRouteBuilder api);
}
