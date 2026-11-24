# Contributing to PiranhaCommerce

Thank you for wanting to help. Bug reports, questions, fixes and new features are all welcome.

## Before you start

- **Bugs and ideas:** open an issue and say what you expected and what happened. For a bug, the steps to see it and your database provider (SQL Server, Azure SQL or PostgreSQL) help most.
- **Larger changes:** open an issue first so we can agree the approach before you spend time on it.
- **Security problems:** do not open a public issue. See [SECURITY.md](SECURITY.md).

## The licence, and what you agree to

PiranhaCommerce is source-available under the [PiranhaCommerce Attribution Licence](LICENSE), and the Licensor sells a [Commercial Licence](COMMERCIAL-LICENSE.md) that removes the attribution notice.

By sending a change (a pull request, a patch or a suggestion with code in it) you agree to section 6 of the licence: you give the Licensor the right to include your change and to licence it under the Attribution Licence **and** under any Commercial Licence. You keep the copyright in what you wrote. If you cannot agree to that, please do not send code; an issue describing the idea is still very welcome.

Only send code you wrote yourself, or code under a licence that allows this, and say where it came from.

## Making a change

You need the .NET 10 SDK and SQL Server LocalDB (PostgreSQL is optional; see [testing](docs/public/advanced/testing.md)).

```
dotnet build
dotnet test
```

The rules that matter most:

1. **Update the documentation in the same change.** The Markdown in `docs/public` is the documentation. A feature, a setting or a behaviour that changes means its page changes too. A test in `tests/PiranhaCommerce.Site.Tests` fails if a page, link or heading anchor is broken.
2. **Add tests.** Behaviour gets an integration test in `tests/PiranhaCommerce.IntegrationTests`. Run the whole suite before you send the change.
3. **Both databases.** If you change the model, add a migration for **each** provider. The steps are in [migrations](docs/public/advanced/migrations.md).
4. **Admin pages and API endpoints need a permission.** Anything without one is for administrators only. See the [staff guide](docs/public/guides/staff.md).
5. **The name is PiranhaCommerce**, one word, in code, docs and text.
6. **Keep the attribution.** The "Built on PiranhaCommerce" notice in the admin and the sample shop stays in place.

Keep a change to one purpose, and describe in the pull request what it does and why.
