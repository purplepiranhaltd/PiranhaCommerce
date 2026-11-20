using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using PiranhaCommerce.Core.Plugins;

namespace PiranhaCommerce.Api;

public static class PiranhaApiExtensions
{
    public static PiranhaBuilder AddPiranhaApi(this PiranhaBuilder builder)
    {
        builder.Services.AddOpenApi();
        return builder;
    }

    public static IEndpointRouteBuilder MapPiranhaApi(this IEndpointRouteBuilder app, string prefix = "/api/v1")
    {
        var api = app.MapGroup(prefix);
        foreach (var c in app.ServiceProvider.GetServices<IApiEndpointContributor>())
            c.MapEndpoints(api);
        app.MapOpenApi();
        return app;
    }
}
