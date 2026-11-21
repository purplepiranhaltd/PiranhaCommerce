using System.Security.Claims;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Options;
using PiranhaCommerce.Api;
using PiranhaCommerce.Api.Auth;
using PiranhaCommerce.Core.Validation;

namespace PiranhaCommerce.Customers;

/// <summary>Who is calling the storefront API. The id comes only from the verified token, never from the request body or URL.</summary>
public static class StorefrontUser
{
    /// <summary>The signed-in customer's id from the token, or null.</summary>
    public static Guid? CustomerId(this ClaimsPrincipal user)
        => Guid.TryParse(user.FindFirstValue("sub"), out var id) ? id : null;

    /// <summary>The session version the token was issued with. A token from before versions existed has none, which counts as 0.</summary>
    public static int SessionVersion(this ClaimsPrincipal user)
        => int.TryParse(user.FindFirstValue("sv"), out var v) ? v : 0;
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
        if (!await accounts.IsSessionCurrentAsync(id, http.User.SessionVersion(), http.RequestAborted)) return Results.Unauthorized();
        return await next(context);
    }
}

internal sealed class CustomerStorefrontEndpoints : IStorefrontEndpointContributor
{
    public void MapEndpoints(IEndpointRouteBuilder storefront)
    {
        // Anonymous on purpose, one by one: everything else in the group needs a shopper token.
        var auth = storefront.MapGroup("/auth").WithTags("Storefront accounts");

        auth.MapPost("/register", async (RegisterInput input, ICustomerAccountService accounts, ICustomerTokenIssuer tokens, IOptions<CustomerAccountOptions> accountOptions, CancellationToken ct) =>
            {
                var profile = await accounts.RegisterAsync(input, ct);
                // When the shop requires a verified email there is no token yet: they follow the emailed link first.
                if (accountOptions.Value.RequireVerifiedEmail) return Results.Accepted("/api/v1/storefront/auth/verify-email", new VerificationRequiredDto(profile, true));
                return Results.Created("/api/v1/storefront/me", new RegisteredDto(profile, tokens.Issue(profile.Id, profile.Email, 0)));
            })
            .AllowAnonymous();

        auth.MapPost("/token", async (SignInInput input, ICustomerAccountService accounts, ICustomerTokenIssuer tokens, CancellationToken ct) =>
            {
                var result = await accounts.CheckAsync(input.Email, input.Password, ct);
                // Wrong password, unknown email, no account, inactive and locked out all look the same.
                if (result.Status == SignInStatus.Unverified) return Results.Json(new { error = "email_not_verified" }, statusCode: StatusCodes.Status403Forbidden);
                return result is { Status: SignInStatus.Valid, CustomerId: { } id, Email: { } email }
                    ? Results.Ok(tokens.Issue(id, email, result.SessionVersion))
                    : Results.Unauthorized();
            })
            .AllowAnonymous();

        auth.MapPost("/verify-email", async (VerifyEmailInput input, ICustomerAccountService accounts, CancellationToken ct) =>
                await accounts.VerifyEmailAsync(input, ct)
                    ? Results.NoContent()
                    : Results.ValidationProblem(new Dictionary<string, string[]> { [nameof(VerifyEmailInput.Token)] = ["This link is not valid, or has expired or already been used."] }))
            .AllowAnonymous();

        auth.MapPost("/resend-verification", async (ForgotPasswordInput input, ICustomerAccountService accounts, CancellationToken ct) =>
            {
                await accounts.RequestVerificationAsync(input, ct);
                return Results.Accepted(); // always, whether or not that address needs one
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

        // Changing the password ends every session, so the response carries a new token for the device that changed it.
        me.MapPost("/password", async (ChangePasswordInput input, ClaimsPrincipal user, ICustomerAccountService accounts, ICustomerTokenIssuer tokens, CancellationToken ct) =>
            await accounts.ChangePasswordAsync(user.CustomerId()!.Value, input, ct) is { } changed
                ? Results.Ok(tokens.Issue(user.CustomerId()!.Value, changed.Email, changed.SessionVersion))
                : Results.ValidationProblem(new Dictionary<string, string[]> { [nameof(ChangePasswordInput.CurrentPassword)] = ["The current password is not correct."] }));

        // Their own data: everything held about them, and erasing their account. The id is always the token's. Erasing needs the password again
        // (checked and counted like a sign-in), so a stolen token alone cannot wipe an account. Orders are anonymised, never deleted.
        me.MapGet("/export", async (ClaimsPrincipal user, ICustomerErasureService svc, CancellationToken ct) =>
            await svc.ExportAsync(user.CustomerId()!.Value, ct) is { } export ? Results.Ok(export) : Results.Unauthorized());

        me.MapPost("/erase", async (EraseOwnAccountInput input, ClaimsPrincipal user, ICustomerAccountService accounts, ICustomerErasureService erasure, CancellationToken ct) =>
        {
            ModelValidator.ValidateOrThrow(input);
            var id = user.CustomerId()!.Value;
            if (await accounts.GetProfileAsync(id, ct) is not { } profile) return Results.Unauthorized();
            var check = await accounts.CheckAsync(profile.Email, input.Password, ct);
            if (check.Status != SignInStatus.Valid || check.CustomerId != id)
                return Results.ValidationProblem(new Dictionary<string, string[]> { [nameof(EraseOwnAccountInput.Password)] = ["The password is not correct."] });
            return await erasure.EraseAsync(id, new EraseCustomerInput { ConfirmEmail = profile.Email }, "customer (self-service)", ct) is not null
                ? Results.NoContent() : Results.Unauthorized();
        });

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

public sealed class EraseOwnAccountInput
{
    [System.ComponentModel.DataAnnotations.Required, System.ComponentModel.DataAnnotations.StringLength(128)]
    public string Password { get; set; } = "";
}

public sealed record RegisteredDto(ProfileDto Profile, TokenResponse Token);
