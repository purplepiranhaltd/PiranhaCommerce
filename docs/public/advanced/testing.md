---
title: Testing, packages and CI
order: 40
description: Running the tests on each database, verifying the packages, and what CI does.
---

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

## Verifying the packages

```
pwsh build/verify-packages.ps1
```

Packs every library, builds a fresh app outside the repo that references **only** the `.nupkg` files (isolated NuGet
cache), publishes it, runs the published output and smoke-tests the API, sign-in, plugin panels and static assets.
`-OmitWebAssetsProperty` runs the negative check. No license is chosen yet (closed source); set one before publishing.

## Continuous integration

`.github/workflows/ci.yml` (GitHub Actions) runs on every push to `main` and every pull request:

- **test** (matrix: SQL Server, PostgreSQL): builds, runs all tests against a real database container, and fails if the
  model has changed without a migration for that provider.
- **packages**: runs `build/verify-packages.ps1` against a SQL Server container and uploads the `.nupkg` files.

The workflow itself has not been run on GitHub yet; its steps were exercised locally in Docker.
