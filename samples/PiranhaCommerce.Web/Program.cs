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

var commerce = builder.Services.AddPiranhaCommerceCore();

// One migration set per database provider, each in an assembly the host owns (it knows which plugins are installed).
switch (builder.Configuration["PiranhaCommerce:Provider"] ?? "SqlServer")
{
    case "SqlServer":   // also Azure SQL
        commerce.AddPiranhaCommerceSqlServer(connectionString, typeof(Program).Assembly);
        break;
    case "PostgreSql":
        commerce.AddPiranhaCommercePostgreSql(connectionString, typeof(PiranhaCommerce.Web.Migrations.PostgreSql.MigrationsAssemblyMarker).Assembly);
        break;
    default:
        throw new InvalidOperationException("PiranhaCommerce:Provider must be SqlServer or PostgreSql.");
}

commerce
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
    .AddPlugin<PiranhaCommerce.Customers.SocialSignIn.SocialSignInPlugin>() // inert until a provider is set under PiranhaCommerce:Customers:Social:Providers
    .AddPlugin<PiranhaCommerce.Customers.SocialSignIn.Admin.SocialSignInAdminPlugin>() // linked social accounts on the customer page
    .AddPlugin<PiranhaCommerce.Email.Smtp.Admin.SmtpEmailAdminPlugin>() // SMTP sending (needs PiranhaCommerce:Email:Smtp settings) and its template editor
    .AddPlugin<PiranhaCommerce.Imaging.Skia.SkiaImagingPlugin>() // makes sizes of product images
    .AddPlugin<WarrantyPlugin>();

// Uploaded files: Azure Blob Storage when PiranhaCommerce:Storage:Provider is AzureBlob (the hosted demo), otherwise a folder on disk
// (PiranhaCommerce:Storage:LocalDisk:Path, default App_Data/files).
if (builder.Configuration["PiranhaCommerce:Storage:Provider"] == "AzureBlob")
    commerce.AddPlugin<PiranhaCommerce.Storage.AzureBlob.AzureBlobStoragePlugin>();
else
    commerce.AddPlugin<PiranhaCommerce.Storage.LocalDisk.LocalDiskStoragePlugin>();

// Behind App Service's front end: trust X-Forwarded-For/Proto (ASPNETCORE_FORWARDEDHEADERS_ENABLED=true) so the shopper's address and https are right.

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

// Hosted under two domains (.com and .co.uk): send the secondary one to the main one, keeping the path and query.
// Set Hosting:RedirectFrom (piranhacommerce.co.uk) and Hosting:RedirectTo (piranhacommerce.com); both empty means no redirect.
// The same suffix swap covers staging.* and demo.*. Certificate and domain-ownership checks under /.well-known are left alone.
var redirectFrom = app.Configuration["Hosting:RedirectFrom"];
var redirectTo = app.Configuration["Hosting:RedirectTo"];
if (!string.IsNullOrEmpty(redirectFrom) && !string.IsNullOrEmpty(redirectTo))
    app.Use(async (context, next) =>
    {
        var host = context.Request.Host.Host;
        if (host.EndsWith(redirectFrom, StringComparison.OrdinalIgnoreCase) && !context.Request.Path.StartsWithSegments("/.well-known"))
        {
            var target = host[..^redirectFrom.Length] + redirectTo;
            context.Response.Redirect($"https://{target}{context.Request.PathBase}{context.Request.Path}{context.Request.QueryString}", permanent: true);
            return;
        }
        await next();
    });

// The hosted demo sets PiranhaCommerce:Demo:SeedOnStartup so its administrator and sample catalogue exist. Real shops never set it.
var seedDemo = app.Configuration.GetValue<bool>("PiranhaCommerce:Demo:SeedOnStartup");

if ((app.Environment.IsDevelopment() || seedDemo) && !EF.IsDesignTime) // EF tooling executes Program.cs too
{
    // Development convenience only: deployments apply migrations as a pipeline step, and the app's database login cannot change the schema.
    if (app.Environment.IsDevelopment())
        using (var scope = app.Services.CreateScope())
            await scope.ServiceProvider.GetRequiredService<PiranhaCommerceDbContext>().Database.MigrateAsync();

    var seed = app.Configuration.GetSection("PiranhaCommerce:SeedAdmin");
    if (seed["Email"] is { Length: > 0 } email && seed["Password"] is { Length: > 0 } password)
        await app.Services.EnsureAdministratorAsync(email, password);

    // The sample shop's catalogue (PiranhaCommerce:SampleData:Enabled).
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
