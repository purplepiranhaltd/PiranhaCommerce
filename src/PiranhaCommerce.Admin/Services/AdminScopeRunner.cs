using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.Extensions.DependencyInjection;
using PiranhaCommerce.Core.Audit;
using PiranhaCommerce.Core.Identity;

namespace PiranhaCommerce.Admin.Services;

/// <summary>
/// Interactive Server components live as long as the circuit, so they must not hold scoped services
/// (and therefore DbContexts) for that long. Run each operation in its own short-lived DI scope instead.
/// The service handed to the operation is wrapped so that a method marked <see cref="RequiresPermissionAttribute"/> is refused unless the
/// signed-in person holds that permission, whichever page made the call.
/// </summary>
public sealed class AdminScopeRunner(IServiceScopeFactory scopes, AuthenticationStateProvider auth, StaffPermissionService permissions)
{
    public async Task<TResult> RunAsync<TService, TResult>(Func<TService, Task<TResult>> operation) where TService : notnull
    {
        await using var scope = scopes.CreateAsyncScope();
        // Tell the audit log (when installed) who is making the change: always the signed-in user, never something from a form.
        if (scope.ServiceProvider.GetService<AuditActor>() is { } actor) actor.Name = await auth.GetActorAsync();
        var access = await permissions.GetAsync((await auth.GetAuthenticationStateAsync()).User);
        return await operation(PermissionGuard.Wrap(scope.ServiceProvider.GetRequiredService<TService>(), access));
    }

    /// <summary>Whether the signed-in person holds a permission, for hiding what they could not use.</summary>
    public async Task<bool> CanAsync(string permission) =>
        (await permissions.GetAsync((await auth.GetAuthenticationStateAsync()).User)).Has(permission);
}
