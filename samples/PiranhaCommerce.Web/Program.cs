using PiranhaCommerce.Admin;
using PiranhaCommerce.Api;
using PiranhaCommerce.Core.DependencyInjection;
using PiranhaCommerce.Customers.Admin;
using PiranhaCommerce.Data;
using PiranhaCommerce.Data.PostgreSql;
using PiranhaCommerce.Data.SqlServer;
using PiranhaCommerce.Ecommerce;
using PiranhaCommerce.Ecommerce.Admin;
using PiranhaCommerce.Example.Warranty;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);

var connectionString = builder.Configuration.GetConnectionString("Piranha")
    ?? throw new InvalidOperationException("ConnectionStrings:Piranha is not configured.");

var piranha = builder.Services.AddPiranhaCore();

// One migration set per database provider, each in an assembly the host owns (it knows which plugins are installed).
switch (builder.Configuration["Piranha:Provider"] ?? "SqlServer")
{
    case "SqlServer":   // also Azure SQL
        piranha.AddPiranhaSqlServer(connectionString, typeof(Program).Assembly);
        break;
    case "PostgreSql":
        piranha.AddPiranhaPostgreSql(connectionString, typeof(PiranhaCommerce.Web.Migrations.PostgreSql.MigrationsAssemblyMarker).Assembly);
        break;
    default:
        throw new InvalidOperationException("Piranha:Provider must be SqlServer or PostgreSql.");
}

piranha
    .AddPiranhaApi()
    .AddPiranhaAdmin()
    // Plugins are compiled in via NuGet, never uploaded. Each plugin registers the plugins it requires
    // (here: Catalog and Catalog.Admin), so only the ones you want are listed.
    .AddPlugin<EcommercePlugin>()
    .AddPlugin<CustomersAdminPlugin>()
    .AddPlugin<EcommerceAdminPlugin>()
    .AddPlugin<WarrantyPlugin>();

var app = builder.Build();

if (app.Environment.IsDevelopment() && !EF.IsDesignTime) // EF tooling executes Program.cs too
{
    // Development convenience only: production deployments apply migrations as a deployment step.
    using (var scope = app.Services.CreateScope())
        await scope.ServiceProvider.GetRequiredService<PiranhaDbContext>().Database.MigrateAsync();

    var seed = app.Configuration.GetSection("Piranha:SeedAdmin");
    if (seed["Email"] is { Length: > 0 } email && seed["Password"] is { Length: > 0 } password)
        await app.Services.EnsureAdministratorAsync(email, password);
}

app.UseStaticFiles();
app.UseAuthentication();
app.UseAuthorization();
app.UseAntiforgery();

app.MapStaticAssets();
app.MapPiranhaApi();
app.MapPiranhaAdmin();
app.MapGet("/", () => Results.Redirect("/admin"));

app.Run();

// Exposed for WebApplicationFactory<Program> in integration tests.
public partial class Program;
