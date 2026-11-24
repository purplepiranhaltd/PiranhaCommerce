---
title: Installation
order: 30
description: Run the sample shop, then add PiranhaCommerce to your own application.
---

# Installation

## What you need

- The **.NET 10 SDK**.
- A database: SQL Server (LocalDB is enough for development), Azure SQL, or PostgreSQL.

## Run the sample

The repository contains a complete sample host (`samples/PiranhaCommerce.Web`) with the admin, the API and an example shop front.

```
dotnet run --project samples/PiranhaCommerce.Web      # http://localhost:5280
```

In `Development` the host applies migrations on startup and creates an administrator from `appsettings.Development.json` (`admin@piranha.local` / `Dev-Passw0rd!`; **development only**). In any other environment, set `Piranha:Jwt:SigningKey` (32 or more characters, from a secret store) and create users yourself; the API fails closed without a key.

- **Admin:** `/admin`, cookie sign-in, Blazor Interactive Server. Pages from plugins appear once the plugin registers an admin page assembly. Pages require the Administrator role unless marked anonymous.
- **API:** `POST /api/v1/auth/token` with `{ "email", "password" }`, then send `Authorization: Bearer <token>`. All `/api/v1` endpoints need a token, and writes need the `Administrator` role. The OpenAPI document is at `/openapi/v1.json`. API keys for system-to-system integrations are not built yet.

To build and test everything:

```
dotnet build
dotnet test
```

## Add it to your own application

Create an ASP.NET Core web application, reference the packages you want (see [the packages](packages.md)) and register them:

```csharp
builder.Services.AddPiranhaCore()
    .AddPiranhaSqlServer(connectionString, typeof(Program).Assembly)
    .AddPiranhaApi()
    .AddPiranhaAdmin()
    .AddPlugin<EcommercePlugin>()
    .AddPlugin<EcommerceAdminPlugin>();
```

A plugin lists the plugins it needs, and they are registered automatically, so naming `EcommercePlugin` brings in the catalogue and customers.

Then:

1. **Set the web assets property** in your `.csproj`, if you use the admin (see [Hosting requirements](../advanced/hosting.md)):

   ```xml
   <RequiresAspNetWebAssets>true</RequiresAspNetWebAssets>
   ```

2. **Add the connection string** (`ConnectionStrings:Piranha`) and, for PostgreSQL, set `Piranha:Provider` to `PostgreSql`.
3. **Create the migrations** in your host: `dotnet ef migrations add Initial --project <your host> --output-dir Migrations`. See [Migrations](../advanced/migrations.md). The database provider's migration set must live in an assembly you own.
4. **Apply them** as a deployment step. (The sample applies them on startup in Development only.)
5. **Create an administrator** and set `Piranha:Jwt:SigningKey` from a secret store.
6. **Choose where files go:** add a storage plugin (`LocalDiskStoragePlugin` or the Azure Blob one) for product pictures, and `SkiaImagingPlugin` if you want smaller copies made on upload.
7. **Choose email and payments** if you need them: the SMTP plugin, and the Stripe plugin (which stays inert until its keys are set).

When you upgrade or add a package, repeat step 3 and apply the new migration.

Next: [Your first store](first-store.md) and [Configuration](configuration.md).
