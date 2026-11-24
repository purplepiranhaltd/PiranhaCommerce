---
title: What PiranhaCommerce is
order: 10
description: What you get, how the pieces fit, and what you build yourself.
---

# What PiranhaCommerce is

PiranhaCommerce is a set of NuGet packages, not a hosted service and not a finished web shop. You create an ASP.NET application, reference the packages you need and register them in `Program.cs`. The packages give you:

- **A catalogue** shared by every sales channel: products, categories, barcodes, pictures, options and variants, stock at several locations.
- **An online store** layered on it: baskets, guest and signed-in checkout, orders, shipping, tax, promo codes, discounts, sales, gift vouchers, returns and refunds.
- **Customers** with their own sign-in, address books, groups and privacy tools (export and erasure).
- **An API** for everything, with an OpenAPI description, and a separate **storefront API** for shoppers.
- **An admin** (Blazor) for staff, which plugins extend.
- **Payments, email, storage and imaging** as optional packages: Stripe, SMTP, local disk or Azure Blob, and SkiaSharp.

## What you build

The shop front is yours. The packages give you the storefront API (`/api/v1/storefront`) and the services behind it; you decide the look, the pages and the technology. The sample host in the repository includes a complete example shop front.

## How it fits together

- **One database.** Every package adds its tables to one shared `PiranhaDbContext` through model contributors. SQL Server, Azure SQL and PostgreSQL are supported.
- **The host owns the migrations**, because only the host knows which packages are installed. See [Migrations](../advanced/migrations.md).
- **Plugins are compiled in.** There is no runtime upload or assembly scanning. See [The plugin model](../advanced/plugins.md).
- **Optional means optional.** A package that depends on another one (for example, webhooks) takes it as an optional dependency, so leaving a package out never breaks the others.

## What is not here yet

A multi-currency shop, translations, multi-store and vendors, and live carrier rates are on the roadmap but not built. Each guide lists what is "Not built" for its area.

Next: [the packages](packages.md), then [Installation](installation.md).
