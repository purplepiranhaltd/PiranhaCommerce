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
| `PiranhaCommerce.Admin` | Blazor admin (Razor class library): menu + slots that plugins contribute to. |
| `PiranhaCommerce.Catalog` | Products shared by every sales channel (name, SKU, price), with API. Channels never add columns to it. |
| `PiranhaCommerce.Catalog.Admin` | Product list/edit pages. |
| `PiranhaCommerce.Ecommerce` | Online-store data layered on the catalogue (e.g. `ecom.ProductListing`: published online). A future `PiranhaCommerce.Epos` is a sibling with its own tables. |
| `PiranhaCommerce.Ecommerce.Admin` | Adds an "Online store" panel to the product edit page through the `ProductEditPanels` admin slot. |

## Plugin model

Plugins are NuGet packages referenced at build time and registered in `Program.cs`:

```csharp
builder.Services.AddPiranhaCore()
    .AddPiranhaSqlServer(connectionString)
    .AddPiranhaApi()
    .AddPiranhaAdmin()
    .AddPlugin<EcommercePlugin>()
    .AddPlugin<EcommerceAdminPlugin>();
```

A plugin lists the plugins it needs in `Requires` (typed instances from packages it references). They are registered
automatically, once and first, so the host above gets Catalog and Catalog.Admin without naming them.
Channel-specific product data goes in the channel's own table keyed by `ProductId` (cascade delete), and its admin UI
joins the product page through an admin slot.

There is no runtime upload or assembly scanning: only code built by the dev team runs. A plugin registers
`IModelContributor`, `IApiEndpointContributor`, `IAdminMenuContributor` and `IAdminSlotContributor` services.

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

Each database provider needs its own migration set, in an assembly the host owns. The sample host keeps SQL Server /
Azure SQL migrations in the host project and PostgreSQL migrations in `samples/PiranhaCommerce.Web.Migrations.PostgreSql`,
selected with `Piranha:Provider` (`SqlServer` or `PostgreSql`). After a model change, add a migration for **each** provider:

```
dotnet ef migrations add <Name> --project samples/PiranhaCommerce.Web --output-dir Migrations
Piranha__Provider=PostgreSql ConnectionStrings__Piranha="Host=...;Database=...;Username=...;Password=..."   dotnet ef migrations add <Name> --project samples/PiranhaCommerce.Web.Migrations.PostgreSql   --startup-project samples/PiranhaCommerce.Web --output-dir Migrations
```

`dotnet ef migrations has-pending-model-changes` (same arguments) reports drift; run it for both providers in CI.
Plugin authors cannot ship schema changes themselves; they document required model changes in release notes.

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

Integration tests run the real host against a throwaway database per test class (created, migrated, dropped). The default is
SQL Server LocalDB. To run the same suite on PostgreSQL:

```
docker run -d --name piranha-pg -e POSTGRES_PASSWORD=<pw> -p 15432:5432 postgres:17-alpine
PIRANHA_TEST_PROVIDER=PostgreSql PIRANHA_TEST_PG_SERVER="Host=localhost;Port=15432;Username=postgres;Password=<pw>"   dotnet test tests/PiranhaCommerce.IntegrationTests
```

(Windows reserves some port ranges; if Docker reports a port is forbidden, pick another.)

Behaviour that differs between providers (e.g. case-sensitive text comparison) is normalised in the services and covered by tests
that run on both.

## Host requirements

A host application that uses `PiranhaCommerce.Admin` must set this in its `.csproj`:

```xml
<RequiresAspNetWebAssets>true</RequiresAspNetWebAssets>
```

The admin uses Blazor Interactive Server. Without the property the host does not receive `_framework/blazor.web.js`,
so pages render but never become interactive. A package cannot set it for you (it is decided at restore time, before
package build files exist). The admin logs a warning at startup if the script is missing.

## Packages

```
pwsh build/verify-packages.ps1
```

Packs every library, builds a fresh app outside the repo that references **only** the `.nupkg` files (isolated NuGet
cache), publishes it, runs the published output and smoke-tests the API, sign-in, plugin panels and static assets.
`-OmitWebAssetsProperty` runs the negative check. No license is chosen yet (closed source); set one before publishing.
