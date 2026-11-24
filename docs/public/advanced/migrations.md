---
title: Migrations
order: 20
description: The host owns the schema: how to add and check migrations for each database provider.
---

All packages and plugins contribute to one shared `PiranhaCommerceDbContext`, so the **host application owns the migrations**:
it is the only place that knows which plugins are installed. Pass the host assembly when configuring the provider
(`typeof(Program).Assembly`). Workflow after installing or upgrading a plugin package:

```
dotnet tool restore            # or install dotnet-ef 10.x
dotnet ef migrations add <Name> --project <host project> --output-dir Migrations
```

Each database provider needs its own migration set, in an assembly the host owns. The sample host keeps SQL Server /
Azure SQL migrations in the host project and PostgreSQL migrations in `samples/PiranhaCommerce.Web.Migrations.PostgreSql`,
selected with `PiranhaCommerce:Provider` (`SqlServer` or `PostgreSql`). After a model change, add a migration for **each** provider:

```
dotnet ef migrations add <Name> --project samples/PiranhaCommerce.Web --output-dir Migrations
PiranhaCommerce__Provider=PostgreSql ConnectionStrings__PiranhaCommerce="Host=...;Database=...;Username=...;Password=..."   dotnet ef migrations add <Name> --project samples/PiranhaCommerce.Web.Migrations.PostgreSql   --startup-project samples/PiranhaCommerce.Web --output-dir Migrations
```

`dotnet ef migrations has-pending-model-changes` (same arguments) reports drift; run it for both providers in CI.
Plugin authors cannot ship schema changes themselves; they document required model changes in release notes.

## Where EF Core keeps the list of applied migrations

PiranhaCommerce keeps it in **`core.Migrations`**, beside the other `core` tables, instead of EF Core's default `dbo.__EFMigrationsHistory` (`public.__EFMigrationsHistory` on PostgreSQL). The provider packages set this for you, so a host that calls `AddPiranhaCommerceSqlServer` or `AddPiranhaCommercePostgreSql` needs to do nothing.

A database that was set up before this change has the list in the old place, so EF Core would think nothing had been applied and try to create the tables again. Move the table once, before starting the new version:

```sql
-- SQL Server and Azure SQL
IF SCHEMA_ID('core') IS NULL EXEC('CREATE SCHEMA core');
ALTER SCHEMA core TRANSFER dbo.__EFMigrationsHistory;
EXEC sp_rename 'core.__EFMigrationsHistory', 'Migrations';

-- PostgreSQL
CREATE SCHEMA IF NOT EXISTS core;
ALTER TABLE public."__EFMigrationsHistory" SET SCHEMA core;
ALTER TABLE core."__EFMigrationsHistory" RENAME TO "Migrations";
```

