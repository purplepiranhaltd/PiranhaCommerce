---
title: The packages
order: 20
description: What each package does.
---

| Package | Purpose |
|---|---|
| `PiranhaCommerce.Core` | Domain base types, plugin model, admin extension contracts. No ecommerce concepts, so an EPOS can reuse it. |
| `PiranhaCommerce.Data` | EF Core `PiranhaCommerceDbContext`; packages contribute entities via `IModelContributor`. |
| `PiranhaCommerce.Data.SqlServer` | SQL Server + Azure SQL (priority target). |
| `PiranhaCommerce.Data.PostgreSql` | PostgreSQL. |
| `PiranhaCommerce.Api` | REST API host plumbing and `IApiEndpointContributor` (OpenAPI included). |
| `PiranhaCommerce.Admin` | Blazor admin (Razor class library): menu + slots that plugins contribute to. |
| `PiranhaCommerce.Catalog` | Products shared by every sales channel (name, SKU, price), with API. Channels never add columns to it. |
| `PiranhaCommerce.Catalog.Admin` | Product list/edit pages. |
| `PiranhaCommerce.Storage.LocalDisk` | Where uploaded files (product pictures) live: a folder on the server (`PiranhaCommerce:Storage:LocalDisk:Path`, default `App_Data/files`). Back it up; share it or use Azure Blob when there are several servers. |
| `PiranhaCommerce.Storage.AzureBlob` | The same, in an Azure storage container (`PiranhaCommerce:Storage:AzureBlob:ConnectionString`, or `AccountUri` to use the host's Azure identity, and `Container`). Use instead of LocalDisk, not with it. |
| `PiranhaCommerce.Imaging.Skia` | Reads image sizes and makes smaller copies when a picture is uploaded, with SkiaSharp (MIT licensed). Optional: without it the original is stored as it is. |
| `PiranhaCommerce.Customers` | Customers and their address books, shared by every sales channel, with API. Customers are not admin users. |
| `PiranhaCommerce.Customers.SocialSignIn` | Shopper sign-in with Google, Microsoft, Apple or any OpenID Connect provider, by ID-token exchange. Optional, and inert until a provider is set. |
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
