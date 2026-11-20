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
| `PiranhaCommerce.Customers` | Customers and their address books, shared by every sales channel, with API. Customers are not admin users. |
| `PiranhaCommerce.Customers.Admin` | Customer list/edit pages and an address book panel (contributed through the `customer.edit.panels` slot). |
| `PiranhaCommerce.Ecommerce` | Online store layered on the catalogue: listings (`ecom.ProductListing`), baskets, checkout and orders. Needs Catalog and Customers. A future `PiranhaCommerce.Epos` is a sibling with its own tables. |
| `PiranhaCommerce.Ecommerce.Admin` | Order list and detail pages, plus an "Online store" panel on the product edit page through the `ProductEditPanels` admin slot. |

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

### Sign-in limits and proxies

Accounts lock for a while after 5 wrong passwords. On top of that, `POST /api/v1/auth/token` and the admin sign-in form allow
10 attempts a minute per client address (answer `429` with `Retry-After`). Change it under `Piranha:RateLimit`
(`SignInAttempts`, `WindowSeconds`, or `Enabled: false` if a gateway already limits). It is on automatically once
`AddPiranhaApi()` is called; the host does not need to add middleware.

The limit counts the address the app sees. **Behind a load balancer or reverse proxy** (Azure App Service, Front Door, nginx),
configure forwarded headers so the app sees the real client. Otherwise every visitor shares the proxy's address and ten sign-ins
a minute is the limit for everybody:

```csharp
builder.Services.Configure<ForwardedHeadersOptions>(o => o.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto);
// ...and app.UseForwardedHeaders(); first in the pipeline, with KnownProxies/KnownNetworks set to your proxy.
```

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

## Customers

`PiranhaCommerce.Customers` holds people and businesses that buy, for every channel. A customer has an email (required, unique
whatever the letter case, enforced by a database index), optional name, phone and staff-only notes, and an address book of up to
50 addresses (country as an ISO two-letter code). Orders will copy the address they use, so editing an address never changes
history. API: `/api/v1/customers` and `/api/v1/customers/{id}/addresses` (reads need the read policy, changes the write policy).

A customer is deliberately not an admin user (see "Shopper accounts" below).

Open design notes (stock transfers, signing in at a location, erasing customer data) are in `docs/roadmap.md`.

## Shopper accounts (storefront API)

Shoppers sign in separately from staff. Under `/api/v1/storefront`:

- `POST /auth/register`, `POST /auth/token` (anonymous). Registration returns a token; a wrong password, unknown email, locked or
  deactivated account all answer the same `401`. 5 wrong passwords lock the account for 15 minutes (`Piranha:Customers`).
- `GET|PUT /me`, `POST /me/password`, `GET|POST|PUT|DELETE /me/addresses` - the signed-in shopper's own record only. Staff notes are never shown.
- `POST /baskets`, `GET /baskets/{id}`, `PUT|DELETE /baskets/{id}/lines/{productId}`, `POST /baskets/{id}/quote`,
  `POST /baskets/{id}/checkout`, `POST /baskets/{id}/claim`, `GET /orders`, `GET /orders/{id}`.

How it is kept apart from the staff API:

- A shopper's sign-in lives in its own table, not among the admin users, so a customer can never open the admin or hold a role.
- Shopper tokens have their own audience, signing key (derived from `Piranha:Jwt:SigningKey`) and scheme. The staff API refuses them
  and the storefront refuses staff tokens. Every storefront endpoint requires a shopper token unless it says otherwise.
- The customer is always the one in the token. Checkout ignores any `customerId` in the request, and another shopper's basket or
  order answers `404`, the same as one that does not exist.
- **Registration is refused for any email that already has a customer record** (made by staff or by an earlier order). Letting a
  stranger attach a password to it would hand them that customer's order history. Such a customer gets a sign-in the safe way:
  `POST /auth/forgot-password` emails a single-use link, and `POST /auth/reset-password` with it sets the password (creating the
  sign-in if there is none). Receiving the email is the proof of ownership. It also rescues someone whose address was registered
  by a stranger, and clears a lockout.
- Reset links work once, expire (default 60 minutes), die when another is used, are stored only as a hash, and are limited to 5
  emails an hour per customer. Asking never reveals whether an address is a customer. Set `Piranha:Customers:ResetUrlTemplate`
  (for example `https://shop.example.com/reset?email={email}&token={token}`) so the email carries a link to your storefront page;
  without it the email carries the code.
- A deactivated customer is cut off at once, even with an unexpired token. Tokens last `Piranha:Jwt:LifetimeMinutes` (60); changing a
  password does not end tokens already issued.
- An open basket with no owner can be claimed once with its id (`/claim`); two shoppers claiming at once leaves one owner.

## Email

The platform sends email only through `IEmailSender` (`PiranhaCommerce.Core.Email`): one method, `SendAsync(EmailMessage)`. Nothing
in the core knows about SMTP or any provider. Until you register a sender, mail is **not sent** and a warning is logged (without
the content, which may hold a reset link). To send real mail, add a plugin or your own class:

```csharp
builder.Services.AddSingleton<IEmailSender, MySmtpSender>();   // after AddPiranhaCore(): the last registration wins
```

Today it sends password-reset links and, for every order placed, a plain-text order confirmation (`Piranha:Ecommerce:SendOrderConfirmations`,
default on). A failing mail server never fails the request that triggered it: the order is still placed, and the failure is logged.
The email is sent before the checkout response returns, so a slow provider slows checkout; queue inside your sender if that matters.

## Baskets and orders

`PiranhaCommerce.Ecommerce` turns the shared catalogue into an online shop. API (under `/api/v1`):

- `POST /baskets`, `GET /baskets/{id}`, `PUT|DELETE /baskets/{id}/lines/{productId}` - only products published through the product's
  online listing can be added; prices are always read live from the catalogue.
- `POST /baskets/{id}/checkout` - email, shipping address (billing defaults to it), optional customer link and note. The order, a copy
  of every name, price and address, and the stock taken from the shelf are committed in **one transaction**: short stock or an
  unpublished product refuses the whole checkout (409) and nothing changes. Checking out the same basket again returns the same order.
- `GET /orders` (stage and search filters), `GET /orders/{id}`, `GET /orders/by-number/{number}`,
  `POST /orders/{id}/stage` - Placed to Processing or Cancelled, Processing to Completed or Cancelled. Cancelling puts stock back once.

Order numbers are sequential (first `ORD-1001`). Orders never change when a product is edited, and survive deleting a customer; a
product that has been sold cannot be deleted. Settings: `Piranha:Ecommerce:Currency` (default `GBP`) and
`Piranha:Ecommerce:StockLocationId` (default: the built-in location). Enums travel as names in the API (`"Placed"`).
What is not built yet (payments, shipping, tax, promotions) is listed in `docs/roadmap.md`.

### Shipping, tax and payments

- **Shipping methods** (`/shipping-methods`): a name and flat price, optionally free once the basket reaches a spend. Once any method is
  active, checkout must choose one; with none, shipping is free.
- **Tax classes and rules** (`/tax-classes`, `/tax-rules`): every store starts with a default class, "Standard". Add classes such as
  Reduced and Zero, set a product's class on its online listing (`taxClassId`, none = the default class), and give each class a rate
  per destination country (`*` for all others). So the UK can charge 20% on most goods, 5% on some and 0% on others in one basket.
  Prices are entered tax-exclusive.
  - Tax is worked out **per line**, by that product's class, each line rounded to the penny on its own (as an invoice adds up), then
    summed. Postage is taxed at the default class's rate. A class with no rate for the destination (and no `*` rate) is not taxed there.
  - The default class cannot be deleted or switched off (make another the default instead), and a class that rules or products still
    use cannot be deleted. Rules are unique per class and country.
- `POST /baskets/{id}/quote` returns exactly what checkout will charge (the same calculator), with a `taxes` list (name, rate, amount).
  Orders keep copies of the method name and, on every line, the tax name, rate and amount, so editing settings later never changes an
  order; the order's `taxes` summary is built from those copies. Orders placed before tax classes show their single flat tax row.
  Total = goods + shipping + tax.
- **Payments**: `POST /orders/{id}/payments` and `/refunds` record money in and out (append-only; mistakes are corrected with a refund).
  A payment can never take an order past its total, a refund can never exceed what was received, and simultaneous requests are checked
  one after another. A provider `reference` is unique per provider, so a retried webhook or double click is refused (409) instead of
  recording the money twice. The order shows `paymentStatus`: `Unpaid`, `PartlyPaid`, `Paid`, `Refunded`.
- Set `Piranha:Ecommerce:RequirePaymentBeforeProcessing` to `true` to stop unpaid orders moving to Processing.
- A gateway plugin registers an `IPaymentProvider` (`Name`, `DisplayName`) and calls `IPaymentService.RecordPaymentAsync` when its
  gateway confirms a payment.
