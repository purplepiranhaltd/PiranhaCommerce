# Hosting the PiranhaCommerce site and demo shop on Azure

Two web apps on the shared App Service plan, each with a `staging` slot; two storage accounts; two databases in the shared elastic pool.

| | Production | Staging |
|---|---|---|
| Promo and docs site | `awa-piranhacommerce` | `awa-piranhacommerce` slot `staging` |
| Demo shop and admin | `awa-piranhacommerce-demo` | `awa-piranhacommerce-demo` slot `staging` |
| Files | storage `piranhacommerceprod` | storage `piranhacommercestaging` |
| Database | `piranhacommerce_live` | `piranhacommerce_staging` |

## Custom domains (both `.com` and `.co.uk`, zones in `PROD-DNS`)

| | Production | Staging slot |
|---|---|---|
| Promo and docs site | `piranhacommerce.com` | `staging.piranhacommerce.com` |
| Demo shop and admin | `demo.piranhacommerce.com` | `demo.staging.piranhacommerce.com` |

The same names under `piranhacommerce.co.uk`. `app.bicep` creates the DNS records (A at the apex, CNAME elsewhere, and the `asuid` TXT proof of ownership), binds each name and issues a free App Service managed certificate. `.com` is the main domain: the apps redirect any `.co.uk` request to the matching `.com` name (`Hosting__RedirectFrom` / `Hosting__RedirectTo` settings).

The names of the apps, storage and databases are agreed. Defaults can still be changed: change the defaults at the top of `app.bicep` and `sql.bicep`, and in `bootstrap.sh` and `deploy.yml`, before the first deployment.

## Analytics

Plausible, on the live promo site and the live demo shop only, through the proxy at `pa.purplepiranha.co.uk`. `app.bicep` sets `Analytics__PlausibleDomain` (`piranhacommerce.com` and `demo.piranhacommerce.com`; add both as sites in Plausible). It is blank on the staging slots, and a blank domain leaves the script out. The setting stays with its slot when slots are swapped.

The script reports a page's whole address, so the shop loads it only when every query parameter is on the harmless list in `ShopAnalytics.cs`: reset and unsubscribe links, and sign-in steps, are never reported. The admin does not load it.

## Files

- `app.bicep`: web apps, slots, storage and role assignments (deploys to resource group `PiranhaCommerce`, app subscription).
- `sql.bicep`: the two databases (deploys to `PROD-SQL`, SQL subscription).
- `bootstrap.sh`: one-time, run by you. Creates the resource group and the GitHub sign-in (an app registration with federated credentials, no secret), with least-privilege roles.
- `sql/grant-access.sql`: one-time, run by you in each new database as an Entra administrator. Gives the pipeline `db_owner` of that database only, and each app identity read/write.

## Order

1. `az login`, set `APP_SUB`, `SQL_SUB`, `SQL_SERVER` and `SQL_POOL` in your shell (the subscription ids and SQL server details are deliberately not in the repository), then `bash infra/bootstrap.sh`.
2. Create the GitHub environments `infra`, `staging`, `production` (required reviewers on `production` and `infra`) and add the secrets the script prints.
3. Run the **Infrastructure** workflow (preview first, then again with *apply*).
4. Run `sql/grant-access.sql` in both databases.
5. Merge to `main`: CI, then **Deploy sites** goes to staging, waits for approval, migrates production, swaps the slots.

## Safety

- Pull requests only compile the Bicep. The Azure sign-in has no credential for pull requests, and the deploy workflow ignores anything that is not a green CI run of a push to this repository's `main`.
- No passwords: the apps sign in to SQL and Blob Storage with managed identities, storage account keys are switched off.
- The app's database login cannot change the schema; migrations run from the pipeline.
- Secrets that remain: the two JWT signing keys (GitHub environment secrets, copied to app settings by the Bicep).

## Still to decide

- The demo administrator for staging and production (`PiranhaCommerce:SeedAdmin:*` app settings) and whether the public demo should be reset on a schedule.
- Tags and any mandatory-tag policy (`tags` parameter, currently empty).
