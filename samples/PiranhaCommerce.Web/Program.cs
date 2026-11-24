using PiranhaCommerce.Admin;
using PiranhaCommerce.Api;
using PiranhaCommerce.Core.DependencyInjection;
using PiranhaCommerce.Audit.Admin;
using PiranhaCommerce.Customers.Admin;
using PiranhaCommerce.Data;
using PiranhaCommerce.Data.PostgreSql;
using PiranhaCommerce.Data.SqlServer;
using PiranhaCommerce.Ecommerce;
using PiranhaCommerce.Ecommerce.Admin;
using PiranhaCommerce.Example.Warranty;
using PiranhaCommerce.Web.SampleData;
using PiranhaCommerce.Web.Shop;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);

var connectionString = builder.Configuration.GetConnectionString("PiranhaCommerce")
    ?? throw new InvalidOperationException("ConnectionStrings:PiranhaCommerce is not configured.");

var piranha = builder.Services.AddPiranhaCommerceCore();

// One migration set per database provider, each in an assembly the host owns (it knows which plugins are installed).
switch (builder.Configuration["PiranhaCommerce:Provider"] ?? "SqlServer")
{
    case "SqlServer":   // also Azure SQL
        piranha.AddPiranhaCommerceSqlServer(connectionString, typeof(Program).Assembly);
        break;
    case "PostgreSql":
        piranha.AddPiranhaCommercePostgreSql(connectionString, typeof(PiranhaCommerce.Web.Migrations.PostgreSql.MigrationsAssemblyMarker).Assembly);
        break;
    default:
        throw new InvalidOperationException("PiranhaCommerce:Provider must be SqlServer or PostgreSql.");
}

piranha
    .AddPiranhaCommerceApi()
    .AddPiranhaCommerceAdmin()
    // Plugins are compiled in via NuGet, never uploaded. Each plugin registers the plugins it requires
    // (here: Catalog and Catalog.Admin), so only the ones you want are listed.
    .AddPlugin<EcommercePlugin>()
    .AddPlugin<AuditAdminPlugin>()
    .AddPlugin<PiranhaCommerce.Webhooks.Admin.WebhooksAdminPlugin>() // signed, retried notifications to other systems (Settings > Webhooks)
    .AddPlugin<CustomersAdminPlugin>()
    .AddPlugin<EcommerceAdminPlugin>()
    .AddPlugin<PiranhaCommerce.Payments.Stripe.StripePlugin>() // inert until PiranhaCommerce:Stripe keys are set
    .AddPlugin<PiranhaCommerce.Email.Smtp.Admin.SmtpEmailAdminPlugin>() // SMTP sending (needs PiranhaCommerce:Email:Smtp settings) and its template editor
    .AddPlugin<PiranhaCommerce.Storage.LocalDisk.LocalDiskStoragePlugin>() // uploaded files (PiranhaCommerce:Storage:LocalDisk:Path, default App_Data/files)
    .AddPlugin<PiranhaCommerce.Imaging.Skia.SkiaImagingPlugin>() // makes sizes of product images
    .AddPlugin<WarrantyPlugin>();

builder.Services.Configure<SampleDataOptions>(builder.Configuration.GetSection(SampleDataOptions.SectionName));
builder.Services.AddSingleton<SampleDataSeeder>();
builder.Services.AddHttpContextAccessor();
builder.Services.AddScoped<ShopSession>();
builder.Services.AddScoped<ShopBaskets>();
ShopAuth.Add(builder.Services.AddAuthentication()); // the shopper's own sign-in cookie, separate from the admin's
builder.Services.AddScoped<ShopFront>();
builder.Services.AddSingleton<ShopRateLimiter>();
builder.Services.Configure<ShopOptions>(builder.Configuration.GetSection(ShopOptions.SectionName));

var app = builder.Build();

if (app.Environment.IsDevelopment() && !EF.IsDesignTime) // EF tooling executes Program.cs too
{
    // Development convenience only: production deployments apply migrations as a deployment step.
    using (var scope = app.Services.CreateScope())
        await scope.ServiceProvider.GetRequiredService<PiranhaDbContext>().Database.MigrateAsync();

    var seed = app.Configuration.GetSection("PiranhaCommerce:SeedAdmin");
    if (seed["Email"] is { Length: > 0 } email && seed["Password"] is { Length: > 0 } password)
        await app.Services.EnsureAdministratorAsync(email, password);

    // The sample shop's catalogue (PiranhaCommerce:SampleData:Enabled). Only ever in Development.
    if (app.Configuration.GetValue<bool>($"{SampleDataOptions.SectionName}:Enabled"))
        await app.Services.GetRequiredService<SampleDataSeeder>().SeedAsync();
}

app.UseStaticFiles();
app.UseAuthentication();
app.UseAuthorization();
app.UseAntiforgery();

app.MapStaticAssets();
app.MapPiranhaCommerceApi();
app.MapPiranhaCommerceAdmin();
// The sample shop front: server-rendered pages that call the shop services in-process (no JavaScript needed).
app.MapRazorComponents<ShopApp>();
ShopForms.Map(app);
PiranhaCommerce.Web.ReturnPages.Map(app); // where a payment gateway sends the shopper back to

app.Run();

// Exposed for WebApplicationFactory<Program> in integration tests.
public partial class Program;
