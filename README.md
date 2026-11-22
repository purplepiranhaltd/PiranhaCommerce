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
- **Refresh tokens** (`POST /auth/refresh`, `POST /auth/logout`, both with `{ "refreshToken" }`): signing in, registering and changing a password also return a
  `refreshToken`. Swap it at `/auth/refresh` for a new access token and the next refresh token (each is single use; the response is the same shape as
  signing in). Only a hash is stored. Using a token a second time means it was copied, so that device's whole chain is revoked (note that two refreshes at
  the same moment look like this, so an app should refresh from one place). A refresh token stops working when sessions are ended (sign out everywhere,
  password change or reset, switching the customer off, a staff email change), after `Piranha:Customers:RefreshTokenDays` (default 30, counted from each
  use; 0 switches refresh tokens off) and after `RefreshTokenMaxDays` (default 180) since that sign-in began. Anything not live is the same `401`.
  `/auth/logout` ends one device's chain (always `204`). Refresh tokens are for the shopper API only, never staff.
- **Their devices:** `GET /me/sessions` lists the devices that can still refresh (id, a readable `device` such as "Chrome on Windows" taken from the
  User-Agent when they signed in, `current` for the device making the call, started, last used, expires). The User-Agent is cleaned and cut to 200 characters; no IP address is kept. A shop's own app can send a User-Agent like `MyShop/2.1` and that is shown as it is; `DELETE /me/sessions/{id}` ends one
  (`404` if it is not theirs). `POST /me/sign-out-everywhere` ends every session, access tokens included, and returns a new token pair for the device that
  asked. It is recorded in the sign-in history as done by the shopper.
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

**An email is a key plus data, never words.** `EmailMessage` is `(To, TemplateKey, Variables)`: there is no subject or body in it. The package
that sends an email declares it (`IEmailTemplateContributor`: key, name, and each variable with a description and sample value) and fills in the
variables; the words belong to whichever email sender is installed. The variable names are a public contract, since templates refer to them.

- **SMTP** (`PiranhaCommerce.Email.Smtp`, with `PiranhaCommerce.Email.Smtp.Admin` for the admin section): sends through any SMTP server using
  MailKit, and keeps the wording itself. Each email's subject, text and optional HTML are Liquid templates (via Fluid: loops and conditions, no
  way to run code), edited in **Email > Email templates** or through `/api/v1/email-templates`. Each starts with the shipped wording; "reset"
  goes back to it. A template that does not parse, or cannot render with the email's sample data, is refused on save. HTML bodies HTML-encode
  every variable; text bodies and subjects do not, and a subject is forced onto one line. An email with no stored or shipped wording
  (a third-party package's key you have not written yet) fails to send with a clear message, and the admin lists it as "not set up".
  Settings under `Piranha:Email:Smtp`: `Host`, `Port` (587), `Security` (`StartTls` default, `SslOnConnect`, `Auto`, or `None` for a local mail catcher
  only), `Username`, `Password`, `FromAddress`, `FromName`, `ReplyToAddress`, `TimeoutSeconds`. Keep the password in user secrets or your host's secret
  store. Server certificates are always validated. The recipient must be exactly one valid address, and neither recipient nor subject is logged.
  Run a mail catcher such as smtp4dev or Papercut and set `Security` to `None` for development.
- **A provider with its own templates (Mailjet, planned)**: its package maps each key from `IEmailTemplateCatalog` to a template id and sends the
  variables; the words are edited in the provider's own admin, so with it installed (instead of SMTP) there is no template section here.

Today it sends password-reset links and, for every order placed, a plain-text order confirmation (`Piranha:Ecommerce:SendOrderConfirmations`,
default on). A failing mail server never fails the request that triggered it: the order is still placed, and the failure is logged.
The email is sent before the checkout response returns, so a slow provider slows checkout; queue inside your sender if that matters.
It also emails the buyer when **a parcel is sent** (`order.shipped`: carrier, tracking, what is in it and what is still to come), when **an order
is cancelled** (`order.cancelled`, saying what is still to be refunded if they paid) and when **a refund is recorded** (`order.refunded`: the
amount and the total refunded so far), however the refund was booked (by hand, started from the admin, or the provider's webhook). These
go out after the change is saved and are controlled by `Piranha:Ecommerce:SendOrderUpdates` (default on). Marking an order completed by hand
sends no shipped email (there is no parcel or tracking to tell them), and crediting a gift voucher on cancel is not announced as a refund.
Each is a template in Email > Email templates (SMTP) with its variables listed there; the variable names are the contract for other senders.

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
- **Promo codes** (`/promotions`, `POST|DELETE /baskets/{id}/promo`): a percentage or fixed amount off the **goods**, never off postage.
  Optional minimum spend, start/end dates and a limit on uses. The discount comes off before shipping and tax: it is shared across
  the lines in proportion to price (to the penny, exactly), postage "free over X" is judged on the goods after the discount, and tax is
  charged on what each line costs after its share. A code is checked again at checkout; if it has stopped working, checkout answers
  409 instead of quietly charging full price. A limited code is used up with one conditional update, so simultaneous orders cannot
  exceed the limit, and cancelling an order gives its use back (once). A code cannot be renamed once created. Every order satisfies
  Total = Subtotal - DiscountTotal + ShippingTotal + TaxTotal.
- **Payment gateways** (plugin point): a gateway package implements `IPaymentGateway` (`StartAsync` opens a payment and returns where to
  send the shopper; `ReadWebhookAsync` verifies the gateway's signature and says what happened) and registers with
  `services.AddPaymentGateway<T>()`. The core does the rest: `POST /orders/{id}/payments/start` (staff) and
  `POST /storefront/orders/{id}/pay` (the shopper's own order) ask for exactly what is still owed, and the public
  `POST /payments/webhooks/{provider}` records confirmed payments and refunds. Replays are harmless (unique provider reference), a bad
  signature is a bare 400, bodies over 256 KB are refused, and money the gateway confirms that cannot be booked (cancelled order,
  over-payment, wrong currency) is logged as an error and counted as `unreconciled` rather than retried. No gateway ships in the box.
- **Sending, returns and refunds** (admin: the order page; API under `/orders/{id}`):
  - **Parcels** (`POST /shipments`): record what left, with an optional carrier, tracking number and https tracking link. Only a Processing
    order ships. Leave the lines out to send everything left. When the last item goes the order becomes Completed in the same transaction;
    marking an order completed by hand records anything not yet sent as one shipment, so Completed always means everything has been sent.
    Sending does not touch stock (it came off the shelf when the order was placed). Once anything has been sent the order can no longer be
    cancelled (a cancel would put sent goods back on the shelf): record a return instead. Shoppers see their own order's parcels and
    tracking, without the member of staff. Simultaneous parcels cannot send more than was bought (a conditional update guards each line).
  - **Returns** (`POST /returns`): record goods when they arrive back, per line, with a reason. Only what was sent and not already returned
    can come back, on Completed orders too. Each line is put back on the shelf by default; untick for damaged goods (recorded, not
    restocked). The return suggests a refund (price less its share of discount, plus tax; pieces are worked out from the cumulative share so
    three returns of one-of-three refund the line exactly, with the delivery charge optional and allowed once). A return moves no money.
  - **Refunds**: a refund is a payment record, as before (`POST /refunds` for money returned elsewhere). When the payment was taken through a
    gateway that can refund (`IRefundingGateway`, which Stripe implements), `POST /orders/{id}/payments/{paymentId}/refund` sends the money
    back through the provider and books it with the provider's refund id, so the provider's own webhook is recognised as the same refund.
    The amount is capped by what that provider took and has not returned, a repeated request with the same `idempotencyKey` is the same
    refund, and a refund the provider is still working on is booked when its webhook says it succeeded. A refund made in the provider's
    dashboard is booked automatically; do not also record it by hand. A gift voucher's portion is not refunded to a card: use the voucher return below.
- **Shopper sign-ins, for staff** (`/customers/{id}/sign-in`, admin: the Sign-in card on a customer and a Sign-in column in the list): see whether a
  customer has no sign-in, can sign in, is locked out or is switched off; **unlock** a lockout; **sign out everywhere**; **send a reset link** (for a
  customer who has no sign-in, such as one staff made, this is the invitation: the link creates it, the address being the proof); and **remove the
  sign-in** (the customer, addresses and orders stay, and any link already emailed is spent). Sending a link tells staff what happened (sent, customer
  switched off, hourly limit reached, email provider failed). Sessions end at once, not after the token's hour: every shopper token carries the
  sign-in's session version, which is checked on every storefront call along with the customer being active and the sign-in existing. Signing out
  everywhere, switching the customer off, removing the sign-in, changing the password and using a reset link each end every token. Changing
  the password returns a fresh token (the response is 200 with a token, not 204) so the device that changed it stays signed in. Tokens issued before
  this existed carry no version and count as 0. The password hash and the version are never in any response. Every staff action (unlock, sign out, reset link
  with its outcome, removal, switching off or on) is recorded with who did it and when, shown in the card and at `GET /customers/{id}/sign-in/history`,
  included in the customer's export, and deleted with them on erasure. Customers' own sign-ins and password changes are not logged.
- **Abandoned baskets**: open baskets nobody has touched for `Piranha:Ecommerce:AbandonedBasketDays` (default 60; 0 keeps them for ever) are deleted with
  their lines by a background job that runs every six hours (first run a minute after start). Any change to a basket (a line, a promo code, a voucher, a
  shopper claiming it) counts as being active. Checked-out baskets are never touched. Baskets hold no stock, so this is housekeeping only. `IBasketHousekeeper`
  does the work if you want to run it from your own scheduler.
- **Customer group prices**: a customer group can give its members a percentage off every product (`discountPercent` on the group, 0 to 100, 2 decimal
  places) and a product can carry a fixed price for a group (`PUT|DELETE /products/{id}/group-prices/{groupId}` with `{ "price" }`, `GET` lists every group
  with what it pays; admin: the "Customer group prices" panel on the product page and the % field on Customer groups). What a customer pays: for each
  group they belong to, the fixed price if that product has one for the group, otherwise the regular price less the group's percentage (rounded to 2
  places); they pay the **lowest** of those and the regular price, so a group never makes anything dearer. Baskets, quotes and checkout all use the same
  rule, so they cannot disagree; the basket line shows the `regularPrice` when a group price applies, and an order keeps the price it was charged. A
  promo code then discounts the group price (10% off a 100 product at 20% group price is 8, not 10). The price follows membership: join or leave a group
  and an open basket re-prices. Only identified customers get group prices: a guest basket pays the regular price. Prices are tax-exclusive, like all
  prices here. To show a shopper their prices, call `GET /storefront/prices?productIds=...` (their own token) or, from a shop front acting for them,
  `GET /customers/{id}/prices?productIds=...`: each answer is `{ productId, regularPrice, price, discounted }`. (There is no shopper-facing catalogue
  endpoint, so this is the way to price a list of products for a signed-in shopper.)
  **By category:** a group can also have a percentage for a whole category (`GET /customer-groups/{id}/category-discounts`, `PUT|DELETE
  .../{categoryId}` with `{ "percent" }`; admin: Orders > Group category discounts). It covers the category's sub-categories (the nearest category that
  has one set wins), replaces the group's own percentage for products in it, and `0` means no discount there. A product in several categories gets
  the largest percentage. Within one group the most specific thing wins: fixed product price, then category percentage, then the group's own percentage.
- **Guest baskets and checkout** (anonymous, under `/api/v1/storefront/guest/baskets`): a shopper who is not signed in can `POST` a basket, add and remove
  lines (`PUT|DELETE /{id}/lines/{productId}`), use a promo code or gift voucher, see shipping options and a quote, and check out with an email address and
  shipping address (`POST /{id}/checkout`, same body as the signed-in checkout; any `customerId` in it is ignored). The order belongs to no customer, the
  confirmation goes to the email given, and group prices and group-limited promo codes do not apply (nobody is identified). Knowing the basket id is the
  whole of the proof, as it is for claiming a basket; these routes only ever touch a basket that has no owner, so a signed-in shopper's basket answers
  `404` here, and the signed-in routes still need a token. Starting a guest basket is limited per client address (`Piranha:RateLimit:GuestBasketAttempts`,
  default 120 a minute: a shop front on one server address makes one per visitor), and promo and voucher codes share the sign-in limit. Set
  `Piranha:Ecommerce:AllowGuestCheckout` to `false` to require an account to check out (guests can still build a basket; checkout answers `403`). There is
  no guest order lookup afterwards: the order comes back from the checkout call and by email. Old unowned baskets are cleared like any (`AbandonedBasketDays`).
- **Merging baskets**: `POST /storefront/baskets/{id}/merge` with `{ "fromBasketId" }`, signed in, moves the lines of a guest basket into the shopper's own
  basket and deletes the guest one. Quantities add up (a line never goes past 10,000), a product that can no longer be bought is left behind, and a promo code
  or voucher on the guest basket comes along only when the shopper's basket has none (and a code limited to a group only if they are in it). Anything
  that is not their own basket or not an open guest basket is `404`, and two merges of the same guest basket at once move its lines once. Use `claim` instead
  when the shopper has no basket yet and the whole guest basket should simply become theirs.
- **Unpaid orders** (off by default): with `Piranha:Ecommerce:UnpaidOrderHours` above 0, an order still at Placed with no payment of any kind after that many
  hours is cancelled by a background job (every 15 minutes), exactly as if staff had cancelled it: the stock goes back on the shelf, the promo use is given
  back and the buyer gets the cancellation email. The movement is recorded as done by "system (unpaid order expired)". Part-paid orders, orders already in
  progress and orders with nothing to pay are never touched, and a payment that lands a moment before is detected inside the cancel and wins. A payment
  that arrives after is refused like any payment on a cancelled order (a gateway one lands on the unreconciled list). Leave it off if you take orders on
  account or record payments by hand later. `IUnpaidOrderExpiry` does the work if you want to run it from your own scheduler.
- **Stock transfers** (`/api/v1/stock/transfers`, admin: Catalog > Stock transfers): move stock between locations in two steps by two people.
  `POST` sends it: the stock leaves the sending location at once (refused if there is not enough) and the transfer is **in transit**, on neither shelf but
  listed at `GET /stock/in-transit` (per product and route) and as `inTransit` on a product's stock level. `POST /{id}/receive` (by a different user, unless
  `Piranha:Stock:AllowSameUserToReceive` is true) adds what arrived to the receiving location; send `{ "lines": [{ "productId", "quantity" }] }` to say
  less (or more) arrived than was sent, and the transfer is flagged (`hasDiscrepancy`, filter `?discrepancies=true`). `POST /{id}/cancel` while in
  transit puts the stock back. Every step is a ledger movement ("Transfer sent", "Transfer received", "Transfer cancelled") carrying the `TRF-` reference
  and the user. A transfer is received or cancelled once however many people try at the same moment. The menu shows how many are in transit.
- **Staff locations** (`GET /staff/locations`, `PUT /staff/{userId}/locations` with `{ "locationIds": [...] }`, admin: Catalog > Staff locations): limit a
  staff user to certain stock locations. They can then only send, receive, cancel and manually adjust stock at those (others get `403`). A user with none
  ticked is not limited. The location is named on each action, so there is no choose-a-location step at sign-in.
- **Admin menu badges**: a menu entry can show a count of things waiting for a person (`AdminMenuItem.BadgeKey` plus an `IAdminBadgeProvider`
  registered by the plugin; zero shows nothing and a failing provider is logged and skipped). Orders > Unreconciled payments, Disputes (open) and
  Return requests (waiting) use it; the numbers refresh when you move between pages. Disputes and Return requests also have list pages.
- **Customer groups** (`/customer-groups`, `/customer-groups/{id}/members/{customerId}` (PUT adds, DELETE removes), `/customers/{id}/groups`; admin: Customers >
  Groups and a panel on each customer): named sets of customers (trade, staff, VIP) that staff fill and other features can use. Names are unique whatever the letter
  case; adding someone twice is harmless; deleting a customer or a group removes the memberships (the customers stay). A group cannot be deleted while a
  promotion is limited to it. A promotion can be limited to a group (`customerGroupId`): only signed-in customers in it can apply the code, and everyone
  else is told the same "That code is not valid." as for any wrong code. It is checked again at checkout, so leaving the group after applying the code stops the
  order (409) without using up the code. A customer's export lists their groups. Groups hold no prices: group pricing is not built.
- **Disputes and failed refunds** (Stripe: `charge.dispute.created`, `charge.dispute.closed`, `refund.failed` / a failed `refund.updated`; `GET /disputes`,
  `GET /orders/{id}/disputes`): a buyer disputing a card payment with their bank is recorded once (by the provider's id, so replays are harmless) and the
  shop's alert address is emailed (`alert.dispute-opened`) with the reason and the date to answer by: you respond in the provider's own dashboard.
  Winning moves no money. Losing is booked as a refund on the order (the money has gone back to the card) with no email to the buyer; if it cannot be
  booked (for example the shop already refunded in full) it still closes and waits on the unreconciled list. A refund that fails at the provider is
  listed there too, and alerts the shop, because the buyer has not been paid back: deal with it at the provider, then resolve it (retrying cannot book
  these). Gateways report these through `GatewayEventKind.DisputeOpened | DisputeWon | DisputeLost | RefundFailed`. The provider's fee for the dispute is kept on it (`fee`, in the dispute's currency, updated as the provider reports it, so it shows
  a fee that is returned when the shop wins) and is a cost to you, not part of the order. Evidence: `POST /disputes/{id}/evidence` (write access) sends what the
  shop gives (product description, customer name and email, shipping address, carrier, tracking number, shipping date, notes) through the provider when it supports it
  (Stripe does; other gateways opt in with `IDisputeEvidenceGateway`). `submit: false` saves it at the provider and can be repeated; `submit: true` sends it to the bank
  and is final (`evidenceSubmittedUtc`). A decided dispute, one already sent, or an empty form is refused, and so is evidence the provider refuses (409). The
  Disputes page has an Evidence form. Not built: uploading files (receipts, proof of delivery), as that needs file storage.
- **Buyer-requested returns** (storefront `POST|GET /orders/{id}/return-requests`, `DELETE .../{requestId}`; staff `/return-requests`,
  `/return-requests/{id}/approve|reject`, `GET /orders/{id}/return-requests`; admin: Returns on the order page): a signed-in shopper asks to send
  back items of their own order. Only goods that were sent and are not already returned or asked for can be requested (open requests hold their
  quantity back; a cancelled order cannot be asked about). The shop's alert address is emailed (`alert.return-requested`); staff approve or turn
  down with an optional message and the buyer is emailed (`return.answered`); the buyer can withdraw a request nobody has answered. A request
  moves no stock and no money: when the goods arrive, record the return as before, naming the approved request (`returnRequestId`) so it is
  marked Received, then refund as usual. Shoppers never see which staff member answered. Erasing a shopper clears both sides' notes and
  withdraws unanswered requests.
- **Shipping by destination and weight** (`/shipping-methods/{id}/rates`, admin: edit a method on Orders > Shipping, and a product's weight in its Online
  store settings): a method with no rates charges its flat price everywhere, as before. Give it rate rows (a country, or `*` for every other country,
  and a weight band in grams: from the first figure up to, but not including, the second; empty for no limit) and it charges by where the parcel goes and
  what the basket weighs (each product's weight times the quantity; a product with none set counts as nothing). Rows for the destination country take
  precedence over the `*` rows, and a destination or weight no row covers is not served: the method is refused at quote and checkout, and left out of
  `GET /baskets/{id}/shipping-options?countryCode=GB` (also under `/storefront/baskets`), which lists what a basket can use with what each costs now. A
  method's "free when the basket reaches" still applies on top of a rate. Rates are replaced as a whole set (a PUT; an empty list goes back to the flat
  price) and bands for one country may not overlap. A product's weight is part of its listing, so a PUT of the listing without it clears it.
- **Verified email** (`Piranha:Customers:RequireVerifiedEmail`, off by default): when on, registering returns 202 with no token and emails a link
  (template `customer.verify-email`, page set by `VerifyUrlTemplate`, lasts `VerifyTokenMinutes`, default a day); the shopper posts it to
  `/storefront/auth/verify-email` and can ask for another at `/auth/resend-verification` (silent, hourly limit counted apart from password
  resets). Until then a correct password is answered 403 `email_not_verified`; a wrong password or unknown address is the usual 401, so it tells nobody
  that an address has an account. Verification links and reset links are not interchangeable. Setting a password from an emailed link also verifies the
  address (receiving it is the proof), and staff can vouch for one in the Sign-in card. A staff edit that changes the address clears the verification and
  ends the sessions (the new address is unproven). Sign-ins that existed when this was added count as verified, so turning the option on locks no one
  out; with it off, new shoppers are simply recorded as unverified.
- **Shoppers' own privacy** (`GET /storefront/me/export`, `POST /storefront/me/erase`): a signed-in shopper can fetch everything held about
  them (the same export staff see, including staff-only notes: it is their data, and a formal subject-access request may require it; change that
  here if your legal advice differs) and erase their own account. Erasing needs the password again (checked and counted towards the lockout like
  a sign-in, so a stolen token cannot wipe an account) and follows the same rules as staff erasure: orders are anonymised, never deleted, and it is
  refused with the order references while any order is open or a refund is owed (they are told to contact the shop). The erasure record names the
  actor "customer (self-service)". Success is 204 and the token stops working. A storefront needs its own confirmation screen.
- **Customer data export and erasure** (`GET /customers/{id}/export`, `POST /customers/{id}/erase`, admin: the Privacy card on a customer):
  staff can see everything held about a customer (their record including staff notes, address book, whether they can sign in but never the
  password hash, and each package's part: for the store, their orders and open baskets) and erase them. Erasing needs the customer's email typed
  as confirmation and runs in one transaction. The customer record, address book, sign-in and reset links are deleted. **Orders are never
  deleted** (the shop must keep its accounts): the buyer's name, address lines, region, postcode, email and notes are replaced (the email by the
  undeliverable `erased@erased.invalid`) while totals, items, payments, shipments and the **country** stay. That covers orders on the account and
  guest orders under the same email address (any letter case). Notes staff typed on manual payments and on returns are cleared (the payment ledger
  is otherwise append-only, so this is the one place its text is edited); return reasons are kept, so do not write personal data in them. Open baskets
  are deleted and finished ones let go of the person. Erasure is refused, with the reasons, while any of their orders is Placed or Processing or
  is cancelled with money not yet refunded (their contact details are the only way to return it). Nothing is emailed about an erased order, even
  when a late refund arrives, and the SMTP sender also refuses any address on the reserved `.invalid` domain before connecting (an email sender
  should check `UndeliverableAddress.IsUndeliverable`, since a send can cost money). A record of each erasure (the old id, when, by whom, counts) is kept with no personal data in it. A package that
  holds personal data takes part by registering an `ICustomerDataContributor` (check, export, erase). **What this does not reach:** copies held
  by your payment provider (Stripe keeps the checkout email), your email provider (send logs), server logs and backups. Deleting a customer with
  `DELETE /customers/{id}` is not erasure: it leaves their orders as they were. Known gap: a checkout that lands at the very moment its customer
  is being erased can create an order with the real email after the anonymising has run; erase again once it has been cancelled or completed.
- **Unreconciled payments** (`/unreconciled-payments`, admin: Orders > Unreconciled payments): money a gateway confirmed that could not be booked
  (cancelled order, over-payment, wrong currency, unknown order) is saved here instead of being lost in a log. Each item can be retried
  (it books if the order can now take it) or resolved with a note of what you did. A replay of the same webhook never lists it twice, and
  a resolved one is not brought back. `GET /unreconciled-payments/open-count` gives the number still open, for a health check.
  Set `Piranha:Ecommerce:AlertEmail` (one or more addresses, comma-separated) and the shop is emailed (template `alert.unreconciled-payment`, editable
  like the others) the first time money is listed, with the provider, amount, order reference, reason and the provider's id. A retried webhook for the
  same money does not email again, and a mail failure never fails the webhook (the item is on the list and in the log either way).
- **Stripe** (`PiranhaCommerce.Payments.Stripe`, `.AddPlugin<StripePlugin>()`): the first gateway, using Stripe Checkout (a page Stripe hosts, so card
  numbers never touch your server). Settings under `Piranha:Stripe`: `SecretKey`, `WebhookSecret`, `SuccessUrl`, `CancelUrl` (set by you, never by
  a caller). Keep the two secrets in user secrets or your host's secret store, never in source control. Point a Stripe webhook endpoint at
  `/api/v1/payments/webhooks/stripe` for `checkout.session.completed`, `checkout.session.async_payment_succeeded`, `refund.created` and
  `refund.updated`. Locally: `stripe listen --forward-to localhost:5280/api/v1/payments/webhooks/stripe` prints the `whsec_...` to use. Webhook
  signatures are checked in constant time with a 5 minute tolerance; an unset webhook secret rejects everything. Amounts go to Stripe in
  minor units (pence; whole yen); three-decimal currencies are refused. Not built: saved cards, Stripe Tax, 3-decimal currencies.
  The sample app serves plain `/thanks` and `/cart` pages (fixed text, nothing from the address echoed) for `SuccessUrl` and `CancelUrl`
  (`http://localhost:5280/thanks?session={CHECKOUT_SESSION_ID}` and `http://localhost:5280/cart` locally); a real shop points them at its own pages.
- **Gift vouchers** (`/vouchers`, `POST|DELETE /baskets/{id}/voucher`): a way of PAYING, applied after tax. A voucher never changes the
  price or the tax; it pays part or all of the total and the order records a `voucher` payment. The code (80 bits of randomness) is
  shown once at issue and only its hash is stored, plus the last four characters; every failure (unknown, off, expired, empty) gets the
  same answer. The balance moves with one conditional update and every change is a ledger row (balance = sum of the ledger), so
  simultaneous orders cannot overspend it. Cancelling an order returns what it paid onto the voucher. Staff can also return part of it without cancelling
  (`POST /orders/{id}/refunds/voucher`, admin: Payment > Return to the gift voucher): never more than the voucher paid less what already went back, a later
  cancel returns only the rest, it is a `voucher` refund on the order and a Returned row in the voucher's ledger, and the buyer is not emailed. Staff cannot record a `voucher`
  payment or refund by hand. Vouchers are never deleted; switch them off.
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
