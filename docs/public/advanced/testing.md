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
docker run -d --name piranhacommerce-pg -e POSTGRES_PASSWORD=<pw> -p 15432:5432 postgres:17-alpine
PIRANHACOMMERCE_TEST_PROVIDER=PostgreSql PIRANHACOMMERCE_TEST_PG_SERVER="Host=localhost;Port=15432;Username=postgres;Password=<pw>"   dotnet test tests/PiranhaCommerce.IntegrationTests
```

(Windows reserves some port ranges; if Docker reports a port is forbidden, pick another.)

To run on a real SQL Server or **Azure SQL**, set `PIRANHACOMMERCE_TEST_SQLSERVER` to a connection string without a database name.
By default each test class creates and drops its own database. Where the login cannot create databases (Azure SQL),
also set `PIRANHACOMMERCE_TEST_SQLSERVER_DATABASE=<existing db>`: the suite then uses that database and never drops it. Test classes take
turns, and each one starts by **deleting every table in that database** and migrating it afresh (the same as the per-class databases elsewhere),
so point it at a disposable dev database. The full suite takes longer this way, because the classes no longer run side by side. Keep credentials
in environment variables, never in files.

Behaviour that differs between providers (e.g. case-sensitive text comparison) is normalised in the services and covered by tests
that run on both.

## Verifying the packages

```
pwsh build/verify-packages.ps1
```

Packs every library, builds a fresh app outside the repo that references **only** the `.nupkg` files (isolated NuGet
cache), publishes it, runs the published output and smoke-tests the API, sign-in, plugin panels and static assets.
`-OmitWebAssetsProperty` runs the negative check. The packages carry the `LICENSE` file.

## Continuous integration

`.github/workflows/ci.yml` (GitHub Actions) runs on every push to `main` and every pull request:

- **test** (matrix: SQL Server, PostgreSQL): builds, runs all tests against a real database container, and fails if the
  model has changed without a migration for that provider.
- **packages**: runs `build/verify-packages.ps1` against a SQL Server container and uploads the `.nupkg` files.

## Releasing

`.github/workflows/release.yml` publishes the packages. Push a tag that starts with `v` and the workflow does the rest:

```
git tag v0.1.0-alpha.1
git push origin v0.1.0-alpha.1
```

- The **version is the tag without the `v`**, so `v1.2.0` publishes `1.2.0` and `v1.2.0-beta.1` publishes a prerelease. Every package gets that version, whatever `VersionPrefix` says in `Directory.Build.props`.
- The workflow first checks that the tagged commit **passed CI**, packs every library, and runs the same consumer-app check as above against the packed files.
- It then pushes the `.nupkg` files (and their `.snupkg` symbol packages, which let a consumer step into the source) to nuget.org and creates a GitHub release with the packages attached. A version with a `-label` is marked as a prerelease.
- **No API key to store.** Publishing uses nuget.org **Trusted Publishing**: the workflow proves to nuget.org which repository and workflow it is, and gets a key that lasts about an hour. The one-off setup:
  1. On nuget.org, open your user menu, **Trusted Publishing**, and add a policy: repository owner `purplepiranhaltd`, repository `PiranhaCommerce`, workflow file `release.yml`, environment `nuget`.
  2. In the repository settings (Secrets and variables, Actions, **Variables**), add `NUGET_USER` with your nuget.org profile name (not your email address).
  3. Reserve the `PiranhaCommerce.` package prefix on nuget.org so nobody else can publish under it.

  The publish step runs in a GitHub environment called `nuget`; add required reviewers to it if you want to approve each release by hand.
- **Rehearse first:** *Run workflow* on the Actions tab, with a version, does everything except publish.
- A release cannot be taken back: nuget.org lets you hide a version ("unlist") but never reuse its number.
