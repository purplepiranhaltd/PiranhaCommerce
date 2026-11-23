using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.Extensions.DependencyInjection;
using PiranhaCommerce.Core.Audit;

namespace PiranhaCommerce.Admin.Services;

/// <summary>
/// Interactive Server components live as long as the circuit, so they must not hold scoped services
/// (and therefore DbContexts) for that long. Run each operation in its own short-lived DI scope instead.
/// </summary>
public sealed class AdminScopeRunner(IServiceScopeFactory scopes, AuthenticationStateProvider auth)
{
    public async Task<TResult> RunAsync<TService, TResult>(Func<TService, Task<TResult>> operation) where TService : notnull
    {
        await using var scope = scopes.CreateAsyncScope();
        // Tell the audit log (when installed) who is making the change: always the signed-in user, never something from a form.
        if (scope.ServiceProvider.GetService<AuditActor>() is { } actor) actor.Name = await auth.GetActorAsync();
        return await operation(scope.ServiceProvider.GetRequiredService<TService>());
    }
}
