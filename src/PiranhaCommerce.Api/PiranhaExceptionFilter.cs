using Microsoft.AspNetCore.Http;
using PiranhaCommerce.Core.Validation;

namespace PiranhaCommerce.Api;

/// <summary>Maps the framework's domain exceptions to HTTP problem responses so endpoints can simply throw.</summary>
internal sealed class PiranhaExceptionFilter : IEndpointFilter
{
    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        try
        {
            return await next(context);
        }
        catch (PiranhaValidationException ex)
        {
            return Results.ValidationProblem(ex.Errors.ToDictionary(e => e.Key, e => e.Value));
        }
        catch (PiranhaForbiddenException ex)
        {
            return Results.Problem(title: "Forbidden", detail: ex.Message, statusCode: StatusCodes.Status403Forbidden);
        }
        catch (PiranhaConflictException ex)
        {
            return Results.Problem(title: "Conflict", detail: ex.Message, statusCode: StatusCodes.Status409Conflict);
        }
    }
}
