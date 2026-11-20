using Microsoft.Extensions.DependencyInjection;

namespace PiranhaCommerce.Admin.Services;

/// <summary>
/// Interactive Server components live as long as the circuit, so they must not hold scoped services
/// (and therefore DbContexts) for that long. Run each operation in its own short-lived DI scope instead.
/// </summary>
public sealed class AdminScopeRunner(IServiceScopeFactory scopes)
{
    public async Task<TResult> RunAsync<TService, TResult>(Func<TService, Task<TResult>> operation) where TService : notnull
    {
        await using var scope = scopes.CreateAsyncScope();
        return await operation(scope.ServiceProvider.GetRequiredService<TService>());
    }
}
