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

To run on a real SQL Server or **Azure SQL**, set `PIRANHA_TEST_SQLSERVER` to a connection string without a database name.
By default each test class creates and drops its own database. Where the login cannot create databases (Azure SQL),
also set `PIRANHA_TEST_SQLSERVER_DATABASE=<existing db>`: the suite then uses that database and never drops it, but leaves
its test rows behind, so point it at a disposable dev database. Keep credentials in environment variables, never in files.

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

## CI

`.github/workflows/ci.yml` (GitHub Actions) runs on every push to `main` and every pull request:

- **test** (matrix: SQL Server, PostgreSQL): builds, runs all tests against a real database container, and fails if the
  model has changed without a migration for that provider.
- **packages**: runs `build/verify-packages.ps1` against a SQL Server container and uploads the `.nupkg` files.

The workflow itself has not been run on GitHub yet; its steps were exercised locally in Docker.

## Catalog features

The shared `PiranhaCommerce.Catalog` package (used by every sales channel) provides, besides products:

- **Categories**: a tree (`ParentId`), with products in any number of categories. Cycles are rejected; a category with
  sub-categories cannot be deleted (409). `PUT /api/v1/products/{id}/categories` replaces the whole set.
- **Barcodes**: several per product. GTIN-8/12/13/14 codes are check-digit validated and stored left-padded to 14 digits, so a
  UPC-A and its EAN-13 form are the same barcode and cannot belong to two products. Look a product up with
  `GET /api/v1/products/lookup?barcode=...` (a query string, because Code 128 values can contain `/`).
- **Stock**: a quantity per product (`decimal(18,4)`, so weighed goods work) changed only by adjustments that are recorded in an
  append-only ledger (`StockMovement`). Adjustments are atomic and safe under concurrency and transient-failure retries. By
  default stock cannot go below zero; callers that must record physical reality (EPOS) pass `AllowNegative`.
  A product with stock history cannot be deleted (409), because the ledger is an audit trail.
- **Stock locations**: stock is held per location (a warehouse, a shop). A default location always exists and is used when a
  caller names none, so single-site shops never need to think about it. Locations are deactivated, not deleted. Each movement
  records the balance at its location afterwards.
- **Negative stock report**: `GET /api/v1/stock/negative` (and Catalog > Negative stock in the admin) lists every product and
  location below zero, for channels such as EPOS that must record a sale even when the system thought it had none.
  A product drops off the list once a recount brings it back to zero or more.

All three have admin panels on the product edit page and API endpoints; API errors map to 400 (validation) and 409 (conflict).

## Writing a plugin

`samples/PiranhaCommerce.Example.Warranty` is a complete third-party plugin in one project, touching nothing in core. It adds a
product warranty: an entity in its own schema (`IModelContributor`), API endpoints (`IApiEndpointContributor`) and an admin panel
on the product edit page (`IAdminSlotContributor` into `AdminSlots.ProductEditPanels`). Steps for your own:

1. Implement `IPiranhaPlugin`; list plugins you depend on in `Requires` (they are registered first, once).
2. Reference the plugin's package from the host and call `.AddPlugin<YourPlugin>()`.
3. **Add a migration in the host** (`dotnet ef migrations add AddYourPlugin`, once per provider). The host owns the schema, so a new
   plugin shows up as a pending model change until you do, and CI's drift check will tell you.
4. Give Razor components an explicit `@namespace` so they do not depend on the project they are compiled into.

Removing a plugin is also a host migration: remove the registration, then add a migration that drops its tables.
