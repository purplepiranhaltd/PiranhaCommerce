---
title: Quickstart from NuGet
order: 35
description: Build a new application from the published packages, from an empty folder to a working admin and API.
---

# Quickstart from NuGet

This builds a new application from the published packages, with nothing from the repository. It ends with a working admin and API on your own database, ready for [Your first store](first-store.md). It takes about ten minutes.

You need the **.NET 10 SDK** and SQL Server LocalDB (installed with Visual Studio, or on its own). For PostgreSQL or Azure SQL, see [Configuration](configuration.md) and [Migrations](../advanced/migrations.md).

The packages are in alpha (`0.1.0-alpha.1`), so each `dotnet add package` below carries `--prerelease`.

## 1. Create the project

```
dotnet new web -n MyShop -f net10.0
cd MyShop
```

Name the framework with `-f net10.0`. Without it the template uses the newest .NET your SDK knows, and PiranhaCommerce targets .NET 10.

## 2. Add the packages

```
dotnet add package PiranhaCommerce.Core --prerelease
dotnet add package PiranhaCommerce.Data.SqlServer --prerelease
dotnet add package PiranhaCommerce.Api --prerelease
dotnet add package PiranhaCommerce.Admin --prerelease
dotnet add package PiranhaCommerce.Ecommerce --prerelease
dotnet add package PiranhaCommerce.Ecommerce.Admin --prerelease
dotnet add package Microsoft.EntityFrameworkCore.Design --version "10.0.*"
```

Keep the last line's `--version "10.0.*"`. A newer SDK would otherwise pick EF Core 11, which does not work on .NET 10. The Design package only builds migrations, so mark it as a development dependency in `MyShop.csproj`, and add the web assets property that the admin needs (see [Hosting requirements](../advanced/hosting.md)):

```xml
<PropertyGroup>
  <TargetFramework>net10.0</TargetFramework>
  <Nullable>enable</Nullable>
  <ImplicitUsings>enable</ImplicitUsings>
  <RequiresAspNetWebAssets>true</RequiresAspNetWebAssets>
</PropertyGroup>
...
<PackageReference Include="Microsoft.EntityFrameworkCore.Design" Version="10.0.*">
  <PrivateAssets>all</PrivateAssets>
</PackageReference>
```

## 3. Write `Program.cs`

Replace the file's contents with:

```csharp
using Microsoft.EntityFrameworkCore;
using PiranhaCommerce.Admin;
using PiranhaCommerce.Api;
using PiranhaCommerce.Core.DependencyInjection;
using PiranhaCommerce.Data;
using PiranhaCommerce.Data.SqlServer;
using PiranhaCommerce.Ecommerce;
using PiranhaCommerce.Ecommerce.Admin;

var builder = WebApplication.CreateBuilder(args);

var connectionString = builder.Configuration.GetConnectionString("PiranhaCommerce")
    ?? throw new InvalidOperationException("ConnectionStrings:PiranhaCommerce is not configured.");

builder.Services.AddPiranhaCommerceCore()
    .AddPiranhaCommerceSqlServer(connectionString, typeof(Program).Assembly)
    .AddPiranhaCommerceApi()
    .AddPiranhaCommerceAdmin()
    .AddPlugin<EcommercePlugin>()
    .AddPlugin<EcommerceAdminPlugin>();

var app = builder.Build();

if (app.Environment.IsDevelopment() && !EF.IsDesignTime) // EF's tools run this file too
{
    // Development only: in production, apply migrations as a deployment step.
    using (var scope = app.Services.CreateScope())
        await scope.ServiceProvider.GetRequiredService<PiranhaCommerceDbContext>().Database.MigrateAsync();

    var seed = app.Configuration.GetSection("PiranhaCommerce:SeedAdmin");
    if (seed["Email"] is { Length: > 0 } email && seed["Password"] is { Length: > 0 } password)
        await app.Services.EnsureAdministratorAsync(email, password);
}

app.UseStaticFiles();
app.UseAuthentication();
app.UseAuthorization();
app.UseAntiforgery();

app.MapStaticAssets();
app.MapPiranhaCommerceApi();
app.MapPiranhaCommerceAdmin();

app.Run();
```

Naming `EcommercePlugin` brings in the catalogue and customers it needs. This is the same host as [Installation](installation.md) describes, written out in full.

## 4. Settings

Create `appsettings.Development.json`:

```json
{
  "ConnectionStrings": {
    "PiranhaCommerce": "Server=(localdb)\\MSSQLLocalDB;Database=MyShop;Trusted_Connection=True;TrustServerCertificate=True"
  },
  "PiranhaCommerce": {
    "SeedAdmin": { "Email": "admin@myshop.local", "Password": "Use-a-Different-Passw0rd!" },
    "Staff": { "RequireTwoFactor": false }
  }
}
```

- **`SeedAdmin`** creates the first administrator when the app starts in `Development`. Choose your own password; never use one like it in production.
- **`Staff:RequireTwoFactor`** is `false` here for the walkthrough only. Two-factor sign-in is **compulsory for staff by default**, so with the default a new administrator cannot get an API token until they have set up an authenticator app in the admin (Account security). In production leave it on. See [Staff and permissions](../guides/staff.md#two-factor-sign-in).

The API signs its tokens with a key of at least 32 characters. **In Development you can skip this:** a temporary key is made when the app starts (the log says so), and tokens stop working when the app restarts, so sign in again. For a fixed key, and always outside Development, keep it out of your files, in user secrets or your host's secret store:

```
dotnet user-secrets init
dotnet user-secrets set "PiranhaCommerce:Jwt:SigningKey" "<a long random string, 32 characters or more>"
```

Outside Development the app refuses to start without a valid key, and says so.

## 5. Create the migrations

The host owns the database schema (see [Migrations](../advanced/migrations.md)). Install the EF tool at the same major version as the packages, in this project only, and create the first migration:

```
dotnet new tool-manifest
dotnet tool install dotnet-ef --version "10.0.*"
dotnet dotnet-ef migrations add Initial --output-dir Migrations
```

Use the project's own tool as shown. A `dotnet-ef` installed globally may be an older version that cannot read the project. Repeat this step whenever you add or upgrade a package, then apply it (in `Development`, starting the app does).

## 6. Run it

```
dotnet run --no-launch-profile --urls http://localhost:5000
```

Starting the app creates the database, applies the migration and creates your administrator. Then:

- `http://localhost:5000/admin`: sign in with the email and password from `SeedAdmin`.
- `http://localhost:5000/openapi/v1.json`: the API, described.

Now follow [Your first store](first-store.md), using `http://localhost:5000` where it says `http://localhost:5280`, and your own email and password for the sign-in.

## What is not here yet

This host has the catalogue, customers, orders, the API and the admin. It has no shop front: pages for shoppers are yours to build on the [storefront API](first-store.md), or to copy from the [sample shop](sample-shop.md). Add pictures with a storage plugin, email with the SMTP plugin and payments with Stripe when you need them (see [Installation](installation.md)).

The admin shows a "Built on PiranhaCommerce" notice, and so must any shop front you build. See [Licensing](licensing.md) for how, and for removing it.
