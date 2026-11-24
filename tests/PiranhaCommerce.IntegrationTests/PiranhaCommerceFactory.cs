using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using PiranhaCommerce.Api.Auth;
using PiranhaCommerce.Core.Email;
using PiranhaCommerce.Core.Identity;
using PiranhaCommerce.Data;
using PiranhaCommerce.Ecommerce;

namespace PiranhaCommerce.IntegrationTests;

/// <summary>
/// Boots the sample host against a throwaway LocalDB database (unique per fixture). Development startup
/// applies migrations and seeds the admin user, so this also proves the migrations apply from scratch.
/// Requires SQL Server LocalDB.
/// </summary>
public class PiranhaCommerceFactory: WebApplicationFactory<Program>, IAsyncLifetime
{
    public const string AdminEmail = "admin@piranhacommerce.local";
    public const string AdminPassword = "Dev-Passw0rd!";

    // PIRANHACOMMERCE_TEST_PROVIDER=PostgreSql + PIRANHACOMMERCE_TEST_PG_SERVER="Host=...;Port=...;Username=...;Password=..." runs the whole suite
    // against PostgreSQL (a uniquely named database is created per fixture). Default: SQL Server LocalDB, or PIRANHACOMMERCE_TEST_SQLSERVER.
    private static readonly string Provider = Environment.GetEnvironmentVariable("PIRANHACOMMERCE_TEST_PROVIDER") ?? "SqlServer";
    // PIRANHACOMMERCE_TEST_SQLSERVER_DATABASE=<name> runs against that EXISTING database and never drops it (for servers where the test
    // login cannot create databases, e.g. Azure SQL). The tests then leave their rows behind: use a disposable dev database.
    private static readonly string? ExistingDatabase = Environment.GetEnvironmentVariable("PIRANHACOMMERCE_TEST_SQLSERVER_DATABASE");

    /// <summary>True when every test class shares one existing database (so a test must not change store-wide settings such as tax rules).</summary>
    public static bool UsesSharedDatabase => ExistingDatabase is not null;
    private readonly string _database = ExistingDatabase is { Length: > 0 } ? ExistingDatabase : $"PiranhaCommerceTests_{Guid.NewGuid():N}";

    private string ConnectionString
    {
        get
        {
            if (Provider == "PostgreSql")
            {
                var server = Environment.GetEnvironmentVariable("PIRANHACOMMERCE_TEST_PG_SERVER")
                    ?? throw new InvalidOperationException("Set PIRANHACOMMERCE_TEST_PG_SERVER for PostgreSQL runs.");
                // Generous open/command timeouts: ~10 fixtures create databases and run migrations in parallel on one container.
                return $"{server};Database={_database.ToLowerInvariant()};Timeout=60;Command Timeout=60";
            }
            // PIRANHACOMMERCE_TEST_SQLSERVER="Server=host,port;User Id=sa;Password=...;TrustServerCertificate=True" targets a real SQL Server
            // (CI on Linux); otherwise LocalDB.
            if (Environment.GetEnvironmentVariable("PIRANHACOMMERCE_TEST_SQLSERVER") is { Length: > 0 } sql)
                return $"{sql};Database={_database}";
            return $@"Server=(localdb)\MSSQLLocalDB;Database={_database};Trusted_Connection=True;TrustServerCertificate=True";
        }
    }

    // Per-factory setting (not an environment variable: fixtures run in parallel and would overwrite each other).
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseSetting("PiranhaCommerce:Provider", Provider);
        builder.UseSetting("ConnectionStrings:PiranhaCommerce", ConnectionString);
        builder.UseSetting("PiranhaCommerce:RateLimit:SignInAttempts", SignInAttempts.ToString());
        builder.UseSetting("PiranhaCommerce:Storage:LocalDisk:Path", FilesFolder);
        builder.UseSetting("PiranhaCommerce:Ecommerce:StockAlertPollSeconds", "0"); // tests run the notifier themselves
        builder.UseSetting("PiranhaCommerce:Shop:AccountFormsPerMinute", "1000"); // every test shares one client address; tests of the limit lower it
        builder.UseSetting("PiranhaCommerce:SampleData:Enabled", "false"); // the tests share one database and count what is in it
        builder.UseSetting("PiranhaCommerce:Webhooks:PollSeconds", "0"); // tests drive the sender themselves; the background one must not post anywhere
        if (AlertEmail is { } alert) builder.UseSetting("PiranhaCommerce:Ecommerce:AlertEmail", alert);
        foreach (var (key, value) in ExtraSettings) builder.UseSetting(key, value);
        // Mail goes to memory so tests can read the links and codes it would have carried.
        builder.ConfigureTestServices(s => { s.AddSingleton<IEmailSender>(Email); s.AddPaymentGateway<TestGateway>(); });
    }

    public CapturingEmailSender Email { get; } = new();

    /// <summary>Sign-in attempts allowed per minute. High by default so the many sign-ins in the suite never trip it.</summary>
    protected virtual int SignInAttempts => 100_000;
    protected virtual string? AlertEmail => null;
    /// <summary>Where this factory's uploaded files go (its own folder, so tests never share files).</summary>
    internal string FilesFolder { get; } = Path.Combine(Path.GetTempPath(), "piranhacommerce-test-files-" + Guid.NewGuid().ToString("N"));

    protected virtual IReadOnlyDictionary<string, string?> ExtraSettings => new Dictionary<string, string?>();

    public Task InitializeAsync() => Task.CompletedTask;

    async Task IAsyncLifetime.DisposeAsync()
    {
        try
        {
            if (ExistingDatabase is not { Length: > 0 })
            {
                using var scope = Services.CreateScope();
                await scope.ServiceProvider.GetRequiredService<PiranhaCommerceDbContext>().Database.EnsureDeletedAsync();
            }
        }
        finally
        {
            SqlConnection.ClearAllPools();
            Npgsql.NpgsqlConnection.ClearAllPools();
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

    /// <summary>Creates another administrator, so a test can have two different people (sending and receiving a stock transfer).</summary>
    public async Task<(Guid Id, string Email, string Password)> CreateAdministratorAsync()
    {
        using var scope = Services.CreateScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<PiranhaCommerceUser>>();
        var email = $"admin-{Guid.NewGuid():N}@piranhacommerce.local";
        const string password = "Admin2-Passw0rd!";
        var user = new PiranhaCommerceUser { UserName = email, Email = email, EmailConfirmed = true };
        var result = await users.CreateAsync(user, password);
        Assert.True(result.Succeeded, string.Join(";", result.Errors.Select(e => e.Description)));
        Assert.True((await users.AddToRoleAsync(user, PiranhaCommerceRoles.Administrator)).Succeeded);
        return (user.Id, email, password);
    }

    /// <summary>The user the "read-only" tests sign in as: every view permission and nothing that changes anything.</summary>
    public static readonly string[] ViewPermissions =
    [
        Permissions.CatalogView, Permissions.OrdersView, Permissions.CustomersView, Permissions.ReportsView,
    ];

    /// <summary>Creates a read-only user: a role with every view permission (and no permission that changes anything).</summary>
    public Task<(string Email, string Password)> CreateReadOnlyUserAsync() => CreateStaffAsync(ViewPermissions);

    /// <summary>Creates a user who can sign in but holds no role at all, so no permission.</summary>
    public async Task<(string Email, string Password)> CreateUserWithNoRoleAsync()
    {
        var (email, password, _) = await CreateStaffCoreAsync(null);
        return (email, password);
    }

    /// <summary>Creates a user whose own role holds exactly these permissions.</summary>
    public async Task<(string Email, string Password)> CreateStaffAsync(params string[] permissions)
    {
        var (email, password, _) = await CreateStaffCoreAsync(permissions);
        return (email, password);
    }

    /// <summary>As <see cref="CreateStaffAsync"/>, also returning the user's id and the name of the role, so a test can change the role later.</summary>
    public async Task<(string Email, string Password, Guid UserId, string Role)> CreateStaffWithRoleAsync(params string[] permissions)
    {
        var (email, password, ids) = await CreateStaffCoreAsync(permissions);
        return (email, password, ids!.Value.UserId, ids.Value.Role);
    }

    private async Task<(string Email, string Password, (Guid UserId, string Role)? Ids)> CreateStaffCoreAsync(string[]? permissions)
    {
        using var scope = Services.CreateScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<PiranhaCommerceUser>>();
        var roles = scope.ServiceProvider.GetRequiredService<RoleManager<PiranhaCommerceRole>>();
        var email = $"viewer-{Guid.NewGuid():N}@piranhacommerce.local";
        const string password = "Viewer-Passw0rd!";
        var user = new PiranhaCommerceUser { UserName = email, Email = email, EmailConfirmed = true };
        var result = await users.CreateAsync(user, password);
        Assert.True(result.Succeeded, string.Join(";", result.Errors.Select(e => e.Description)));
        if (permissions is null) return (email, password, null);

        var roleName = $"Role-{Guid.NewGuid():N}";
        var role = new PiranhaCommerceRole(roleName);
        Assert.True((await roles.CreateAsync(role)).Succeeded);
        foreach (var key in permissions)
            Assert.True((await roles.AddClaimAsync(role, new System.Security.Claims.Claim(StaffPermissionService.ClaimType, key))).Succeeded);
        Assert.True((await users.AddToRoleAsync(user, roleName)).Succeeded);
        return (email, password, (user.Id, roleName));
    }
}
