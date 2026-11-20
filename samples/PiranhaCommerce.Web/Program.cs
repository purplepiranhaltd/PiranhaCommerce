using PiranhaCommerce.Admin;
using PiranhaCommerce.Api;
using PiranhaCommerce.Core.DependencyInjection;
using PiranhaCommerce.Data;
using PiranhaCommerce.Data.SqlServer;
using PiranhaCommerce.Ecommerce;
using PiranhaCommerce.Ecommerce.Admin;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddPiranhaCore()
    .AddPiranhaSqlServer(builder.Configuration.GetConnectionString("Piranha")
        ?? throw new InvalidOperationException("ConnectionStrings:Piranha is not configured."),
        typeof(Program).Assembly)   // the host owns migrations (it knows which plugins are installed)
    .AddPiranhaApi()
    .AddPiranhaAdmin()
    // Plugins are compiled in via NuGet, never uploaded. Each plugin registers the plugins it requires
    // (here: Catalog and Catalog.Admin), so only the ones you want are listed.
    .AddPlugin<EcommercePlugin>()
    .AddPlugin<EcommerceAdminPlugin>();

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
