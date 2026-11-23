# Piranha Commerce

A commerce engine for .NET, delivered as NuGet packages. Closed source for now; designed so it can be opened later.

## Documentation

The documentation covers installation, your first store, configuration, every feature and advanced setup. It is Markdown in
[`docs/public`](docs/public/index.md), served by the site in `site/PiranhaCommerce.Site` (at `docs.<your domain>`, or under `/docs`).

- Start here: [installation](docs/public/getting-started/installation.md) and [your first store](docs/public/getting-started/first-store.md).
- Features: [catalogue](docs/public/guides/catalogue.md), [customers](docs/public/guides/shoppers.md), [baskets and orders](docs/public/guides/baskets-orders.md),
  [prices and discounts](docs/public/guides/pricing.md), [shipping](docs/public/guides/shipping.md), [payments](docs/public/guides/payments.md),
  [email](docs/public/guides/email.md), [reviews, wishlists and suggestions](docs/public/guides/engagement.md), [reports, webhooks and audit](docs/public/guides/operations.md).
- Advanced: [plugins](docs/public/advanced/plugins.md), [migrations](docs/public/advanced/migrations.md), [hosting](docs/public/advanced/hosting.md), [testing and CI](docs/public/advanced/testing.md).

**Changing a feature means changing its page in `docs/public` in the same commit.** A test in `tests/PiranhaCommerce.Site.Tests` fails if a page, link or heading anchor is broken.
`docs/roadmap.md` and `docs/vocabulary.md` are internal and are not published.

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
| `PiranhaCommerce.Storage.LocalDisk` | Where uploaded files (product pictures) live: a folder on the server (`Piranha:Storage:LocalDisk:Path`, default `App_Data/files`). Back it up; share it or use Azure Blob when there are several servers. |
| `PiranhaCommerce.Storage.AzureBlob` | The same, in an Azure storage container (`Piranha:Storage:AzureBlob:ConnectionString`, or `AccountUri` to use the host's Azure identity, and `Container`). Use instead of LocalDisk, not with it. |
| `PiranhaCommerce.Imaging.Skia` | Reads image sizes and makes smaller copies when a picture is uploaded, with SkiaSharp (MIT licensed). Optional: without it the original is stored as it is. |
| `PiranhaCommerce.Customers` | Customers and their address books, shared by every sales channel, with API. Customers are not admin users. |
| `PiranhaCommerce.Customers.Admin` | Customer list/edit pages and an address book panel (contributed through the `customer.edit.panels` slot). |
| `PiranhaCommerce.Ecommerce` | Online store layered on the catalogue: listings (`ecom.ProductListing`), baskets, checkout and orders. Needs Catalog and Customers. A future `PiranhaCommerce.Epos` is a sibling with its own tables. |
| `PiranhaCommerce.Ecommerce.Admin` | Order list and detail pages, plus an "Online store" panel on the product edit page through the `ProductEditPanels` admin slot. |
| `PiranhaCommerce.Audit` | The audit log: who changed what and when, for every record marked `[Audited]`. Optional. |
| `PiranhaCommerce.Audit.Admin` | The Audit log page. |
| `PiranhaCommerce.Webhooks` | Signed, retried notifications to other systems when orders, payments and shipments change. Optional. |
| `PiranhaCommerce.Webhooks.Admin` | The Webhooks page. |
| `PiranhaCommerce.Email.Smtp` | Sends email through any SMTP server, with editable templates. Optional: until an email sender is registered, mail is not sent. |
| `PiranhaCommerce.Email.Smtp.Admin` | The Email templates page. |
| `PiranhaCommerce.Payments.Stripe` | Card payments through Stripe Checkout, refunds and disputes. Inert until its keys are set. |

## Run the sample

Requires the .NET 10 SDK and SQL Server LocalDB.

```
dotnet run --project samples/PiranhaCommerce.Web      # http://localhost:5280/ (shop) and /admin
dotnet build
dotnet test
```
