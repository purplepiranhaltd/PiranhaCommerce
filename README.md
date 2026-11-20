# Piranha Commerce

A commerce engine for .NET, delivered as NuGet packages. Closed source for now; designed so it can be opened later.

## Packages

| Package | Purpose |
|---|---|
| `PiranhaCommerce.Core` | Domain base types, plugin model, admin extension contracts. No ecommerce concepts, so an EPOS can reuse it. |
| `PiranhaCommerce.Data` | EF Core `PiranhaDbContext`; packages contribute entities via `IModelContributor`. |
| `PiranhaCommerce.Data.SqlServer` | SQL Server + Azure SQL (priority target). |
| `PiranhaCommerce.Data.PostgreSql` | PostgreSQL. |
| `PiranhaCommerce.Api` | REST API host plumbing and `IApiEndpointContributor` (OpenAPI included). |
| `PiranhaCommerce.Admin` | Blazor admin (Razor class library): menu + widget zones fed by plugins. |
| `PiranhaCommerce.Catalog` | Products shared by every sales channel (name, SKU, price), with API. Channels never add columns to it. |
| `PiranhaCommerce.Catalog.Admin` | Product list/edit pages. |
| `PiranhaCommerce.Ecommerce` | Online-store data layered on the catalogue (e.g. `ecom.ProductListing`: published online). A future `PiranhaCommerce.Epos` is a sibling with its own tables. |
| `PiranhaCommerce.Ecommerce.Admin` | Adds an "Online store" panel to the product edit page through the `ProductEditTabs` widget zone. |

## Plugin model

Plugins are NuGet packages referenced at build time and registered in `Program.cs`:

```csharp
builder.Services.AddPiranhaCore()
    .AddPiranhaSqlServer(connectionString)
    .AddPiranhaApi()
    .AddPiranhaAdmin()
    .AddPlugin<CatalogPlugin>()
    .AddPlugin<CatalogAdminPlugin>()
    .AddPlugin<EcommercePlugin>()
    .AddPlugin<EcommerceAdminPlugin>();
```

A plugin declares `DependsOn` in its descriptor; registering it before its dependencies throws a clear error.
Channel-specific product data goes in the channel's own table keyed by `ProductId` (cascade delete), and its admin UI
joins the product page through a widget zone.

There is no runtime upload or assembly scanning: only code built by the dev team runs. A plugin registers
`IModelContributor`, `IApiEndpointContributor`, `IAdminMenuContributor` and `IAdminWidgetContributor` services.

## Build

```
dotnet build
dotnet test
```

## Migrations (host-owned)

All packages and plugins contribute to one shared `PiranhaDbContext`, so the **host application owns the migrations**:
it is the only place that knows which plugins are installed. Pass the host assembly when configuring the provider
(`typeof(Program).Assembly`). Workflow after installing or upgrading a plugin package:

```
dotnet tool restore            # or install dotnet-ef 10.x
dotnet ef migrations add <Name> --project <host project> --output-dir Migrations
```

Each database provider needs its own migration set, so a host targeting both SQL Server/Azure SQL and PostgreSQL
keeps one migrations project per provider (the sample host only targets SQL Server). Plugin authors cannot ship
schema changes themselves; they document required model changes in release notes.

## Running the sample

Requires .NET 10 SDK and SQL Server LocalDB.

```
dotnet run --project samples/PiranhaCommerce.Web      # http://localhost:5280/admin
```

In `Development` the host applies migrations on startup and seeds an administrator from `appsettings.Development.json`
(`admin@piranha.local` / `Dev-Passw0rd!`; **development only**). In any other environment set
`Piranha:Jwt:SigningKey` (32+ chars, from a secret store) and create users yourself; the API fails closed without a key.

- **API:** `POST /api/v1/auth/token` with `{ "email", "password" }`, then `Authorization: Bearer <token>`.
  All `/api/v1` endpoints need a token; writes need the `Administrator` role. OpenAPI document at `/openapi/v1.json`.
  API keys for system-to-system integrations are not implemented yet.
- **Admin:** cookie auth, Blazor Interactive Server. Pages from plugins appear once the plugin registers an
  `AdminPageAssembly` (see `EcommerceAdminPlugin`). Pages require the Administrator role unless marked `[AllowAnonymous]`.

## Tests

```
dotnet test
```

Integration tests run the real host against a throwaway LocalDB database per test class (created, migrated, dropped).
