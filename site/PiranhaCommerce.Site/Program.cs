using PiranhaCommerce.Site;
using PiranhaCommerce.Site.Components;
using PiranhaCommerce.Site.Docs;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddRazorComponents();
builder.Services.Configure<SiteOptions>(builder.Configuration.GetSection(SiteOptions.SectionName));
builder.Services.AddSingleton(sp => DocsLibrary.Load(DocsLocator.Find(builder.Environment, builder.Configuration)));

var app = builder.Build();

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
