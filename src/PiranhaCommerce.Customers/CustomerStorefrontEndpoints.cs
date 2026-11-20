using System.Security.Claims;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using PiranhaCommerce.Api;
using PiranhaCommerce.Api.Auth;

namespace PiranhaCommerce.Customers;

/// <summary>Who is calling the storefront API. The id comes only from the verified token, never from the request body or URL.</summary>
public static class StorefrontUser
{
    /// <summary>The signed-in customer's id from the token, or null.</summary>
    public static Guid? CustomerId(this ClaimsPrincipal user)
        => Guid.TryParse(user.FindFirstValue("sub"), out var id) ? id : null;
}

/// <summary>
/// Refuses a shopper token whose customer is gone or deactivated. A token stays valid until it expires, so this makes
/// deactivating a customer take effect at once instead of up to an hour later.
/// </summary>
public sealed class ActiveCustomerFilter : IEndpointFilter
{
    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        var http = context.HttpContext;
        if (http.User.CustomerId() is not { } id) return Results.Unauthorized();
        var accounts = (ICustomerAccountService)http.RequestServices.GetService(typeof(ICustomerAccountService))!;
        if (await accounts.GetProfileAsync(id, http.RequestAborted) is null) return Results.Unauthorized();
        return await next(context);
    }
}

internal sealed class CustomerStorefrontEndpoints : IStorefrontEndpointContributor
{
    public void MapEndpoints(IEndpointRouteBuilder storefront)
    {
        // Anonymous on purpose, one by one: everything else in the group needs a shopper token.
        var auth = storefront.MapGroup("/auth").WithTags("Storefront accounts");

        auth.MapPost("/register", async (RegisterInput input, ICustomerAccountService accounts, ICustomerTokenIssuer tokens, CancellationToken ct) =>
            {
                var profile = await accounts.RegisterAsync(input, ct);
                return Results.Created("/api/v1/storefront/me", new RegisteredDto(profile, tokens.Issue(profile.Id, profile.Email)));
            })
            .AllowAnonymous();

        auth.MapPost("/token", async (SignInInput input, ICustomerAccountService accounts, ICustomerTokenIssuer tokens, CancellationToken ct) =>
            {
                var result = await accounts.CheckAsync(input.Email, input.Password, ct);
                // Wrong password, unknown email, no account, inactive and locked out all look the same.
                return result is { Status: SignInStatus.Valid, CustomerId: { } id, Email: { } email }
                    ? Results.Ok(tokens.Issue(id, email))
                    : Results.Unauthorized();
            })
            .AllowAnonymous();

        auth.MapPost("/forgot-password", async (ForgotPasswordInput input, ICustomerAccountService accounts, CancellationToken ct) =>
            {
                await accounts.RequestPasswordResetAsync(input, ct);
                return Results.Accepted(); // always, whether or not that address belongs to a customer
            })
            .AllowAnonymous();

        auth.MapPost("/reset-password", async (ResetPasswordInput input, ICustomerAccountService accounts, CancellationToken ct) =>
                await accounts.ResetPasswordAsync(input, ct)
                    ? Results.NoContent()
                    : Results.ValidationProblem(new Dictionary<string, string[]> { [nameof(ResetPasswordInput.Token)] = ["This link is not valid, or has expired or already been used."] }))
            .AllowAnonymous();

        var me = storefront.MapGroup("/me").WithTags("Storefront account").AddEndpointFilter<ActiveCustomerFilter>();

        me.MapGet("/", async (ClaimsPrincipal user, ICustomerAccountService accounts, CancellationToken ct) =>
            await accounts.GetProfileAsync(user.CustomerId()!.Value, ct) is { } p ? Results.Ok(p) : Results.Unauthorized());

        me.MapPut("/", async (ProfileInput input, ClaimsPrincipal user, ICustomerAccountService accounts, CancellationToken ct) =>
            await accounts.UpdateProfileAsync(user.CustomerId()!.Value, input, ct) is { } p ? Results.Ok(p) : Results.Unauthorized());

        me.MapPost("/password", async (ChangePasswordInput input, ClaimsPrincipal user, ICustomerAccountService accounts, CancellationToken ct) =>
            await accounts.ChangePasswordAsync(user.CustomerId()!.Value, input, ct)
                ? Results.NoContent()
                : Results.ValidationProblem(new Dictionary<string, string[]> { [nameof(ChangePasswordInput.CurrentPassword)] = ["The current password is not correct."] }));

        // The address book is the signed-in customer's own: the customer id is always the token's, so another customer's
        // address id simply does not exist for this caller (404).
        var addresses = me.MapGroup("/addresses").WithTags("Storefront account");

        addresses.MapGet("/", async (ClaimsPrincipal user, ICustomerService svc, CancellationToken ct) =>
            Results.Ok(await svc.ListAddressesAsync(user.CustomerId()!.Value, ct) ?? []));

        addresses.MapPost("/", async (AddressInput input, ClaimsPrincipal user, ICustomerService svc, CancellationToken ct) =>
            await svc.AddAddressAsync(user.CustomerId()!.Value, input, ct) is { } a
                ? Results.Created($"/api/v1/storefront/me/addresses/{a.Id}", a)
                : Results.NotFound());

        addresses.MapPut("/{addressId:guid}", async (Guid addressId, AddressInput input, ClaimsPrincipal user, ICustomerService svc, CancellationToken ct) =>
            await svc.UpdateAddressAsync(user.CustomerId()!.Value, addressId, input, ct) is { } a ? Results.Ok(a) : Results.NotFound());

        addresses.MapDelete("/{addressId:guid}", async (Guid addressId, ClaimsPrincipal user, ICustomerService svc, CancellationToken ct) =>
            await svc.DeleteAddressAsync(user.CustomerId()!.Value, addressId, ct) ? Results.NoContent() : Results.NotFound());
    }
}

public sealed record RegisteredDto(ProfileDto Profile, TokenResponse Token);
