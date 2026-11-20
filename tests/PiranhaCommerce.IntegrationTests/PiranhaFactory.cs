using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using PiranhaCommerce.Api.Auth;
using PiranhaCommerce.Core.Identity;
using PiranhaCommerce.Data;

namespace PiranhaCommerce.IntegrationTests;

/// <summary>
/// Boots the sample host against a throwaway LocalDB database (unique per fixture). Development startup
/// applies migrations and seeds the admin user, so this also proves the migrations apply from scratch.
/// Requires SQL Server LocalDB.
/// </summary>
public sealed class PiranhaFactory : WebApplicationFactory<Program>, IAsyncLifetime
{
    public const string AdminEmail = "admin@piranha.local";
    public const string AdminPassword = "Dev-Passw0rd!";

    private readonly string _connectionString =
        $"Server=(localdb)\\MSSQLLocalDB;Database=PiranhaTests_{Guid.NewGuid():N};Trusted_Connection=True;TrustServerCertificate=True";

    // Per-factory setting (not an environment variable: fixtures run in parallel and would overwrite each other).
    protected override void ConfigureWebHost(IWebHostBuilder builder) =>
        builder.UseSetting("ConnectionStrings:Piranha", _connectionString);

    public Task InitializeAsync() => Task.CompletedTask;

    async Task IAsyncLifetime.DisposeAsync()
    {
        try
        {
            using var scope = Services.CreateScope();
            await scope.ServiceProvider.GetRequiredService<PiranhaDbContext>().Database.EnsureDeletedAsync();
        }
        finally
        {
            SqlConnection.ClearAllPools();
            await base.DisposeAsync();
        }
    }

    public async Task<HttpClient> CreateAuthenticatedApiClientAsync(string email = AdminEmail, string password = AdminPassword)
    {
        var client = CreateClient();
        var response = await client.PostAsJsonAsync("/api/v1/auth/token", new TokenRequest(email, password));
        response.EnsureSuccessStatusCode();
        var token = (await response.Content.ReadFromJsonAsync<TokenResponse>())!;
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token.AccessToken);
        return client;
    }

    /// <summary>Creates a user with no roles (can authenticate, but is not an administrator).</summary>
    public async Task<(string Email, string Password)> CreateReadOnlyUserAsync()
    {
        using var scope = Services.CreateScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<PiranhaUser>>();
        var email = $"viewer-{Guid.NewGuid():N}@piranha.local";
        const string password = "Viewer-Passw0rd!";
        var result = await users.CreateAsync(new PiranhaUser { UserName = email, Email = email, EmailConfirmed = true }, password);
        Assert.True(result.Succeeded, string.Join(";", result.Errors.Select(e => e.Description)));
        return (email, password);
    }
}
