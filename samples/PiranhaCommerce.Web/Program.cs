using PiranhaCommerce.Admin;
using PiranhaCommerce.Api;
using PiranhaCommerce.Core.DependencyInjection;
using PiranhaCommerce.Data.SqlServer;
using PiranhaCommerce.Ecommerce;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddPiranhaCore()
    .AddPiranhaSqlServer(builder.Configuration.GetConnectionString("Piranha")
        ?? throw new InvalidOperationException("ConnectionStrings:Piranha is not configured."),
        typeof(Program).Assembly)   // the host owns migrations (it knows which plugins are installed)
    .AddPiranhaApi()
    .AddPiranhaAdmin()
    .AddPlugin<EcommercePlugin>();   // plugins are compiled in via NuGet, never uploaded

var app = builder.Build();
app.MapPiranhaApi();
app.Run();

