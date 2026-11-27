using PiranhaCommerce.Site;
using PiranhaCommerce.Site.Components;
using PiranhaCommerce.Site.Docs;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddRazorComponents();
builder.Services.Configure<SiteOptions>(builder.Configuration.GetSection(SiteOptions.SectionName));
builder.Services.AddSingleton(sp => DocsLibrary.Load(DocsLocator.Find(builder.Environment, builder.Configuration)));

var app = builder.Build();

// Asks search engines not to list this site: Hosting:NoIndex (set on staging), and always on the *.azurewebsites.net addresses, which would
// otherwise be a second copy of the live site. It is added as the response starts, so redirects, files and error pages carry it too.
// There is deliberately no robots.txt block: a crawler has to be able to fetch a page to see that it must not list it.
var noIndex = app.Configuration.GetValue<bool>("Hosting:NoIndex");
app.Use((context, next) =>
{
    if (noIndex || context.Request.Host.Host.EndsWith(".azurewebsites.net", StringComparison.OrdinalIgnoreCase))
        context.Response.OnStarting(() =>
        {
            context.Response.Headers["X-Robots-Tag"] = "noindex, nofollow";
            return Task.CompletedTask;
        });
    return next(context);
});

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

if (!app.Environment.IsDevelopment()) app.UseExceptionHandler("/error", createScopeForErrors: true);

// docs.<domain> serves the documentation at its root: /guides/pricing is answered as /docs/guides/pricing. Anything with a file extension
// (the stylesheet, images) is left alone. This must run before routing picks the page, so routing is added explicitly after it.
app.Use(async (context, next) =>
{
    if (SiteHost.IsDocsHost(context.Request.Host.Host)
        && !context.Request.Path.StartsWithSegments("/docs")
        && !context.Request.Path.StartsWithSegments("/_framework")
        && string.IsNullOrEmpty(Path.GetExtension(context.Request.Path.Value)))
        context.Request.Path = "/docs" + (context.Request.Path.Value == "/" ? "" : context.Request.Path.Value);
    await next();
});

app.UseStaticFiles();
app.UseRouting();
app.UseAntiforgery();
app.MapRazorComponents<App>();

app.Run();

// Exposed for WebApplicationFactory<Program> in tests.
public partial class Program;
