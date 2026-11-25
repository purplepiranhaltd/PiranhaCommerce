#!/usr/bin/env bash
# One-time set-up for deploying PiranhaCommerce to Azure from GitHub Actions. Run it yourself, signed in with `az login` as someone who may
# create resource groups, app registrations and role assignments in BOTH subscriptions. Nothing here runs in a pipeline.
#
# It creates:
#   - the resource group PiranhaCommerce (app subscription),
#   - an app registration with GitHub federated credentials (no secret is created),
#   - the least access the workflows need, scoped to the resources they touch.
# Read it before you run it. Safe to run again.
set -euo pipefail

# Not kept in the repository. Set them in your shell first, for example:
#   export APP_SUB=<subscription id of the web apps and storage>
#   export SQL_SUB=<subscription id of the SQL server>
#   export SQL_SERVER=<SQL server name, without .database.windows.net>
#   export SQL_POOL=<its elastic pool name>
: "${APP_SUB:?Set APP_SUB (the subscription of the web apps and storage)}"
: "${SQL_SUB:?Set SQL_SUB (the subscription of the SQL server)}"
: "${SQL_SERVER:?Set SQL_SERVER (the SQL server name)}"
: "${SQL_POOL:?Set SQL_POOL (the elastic pool name)}"
LOCATION="uksouth"
RG="PiranhaCommerce"
PLAN_RG="PROD-APP"; PLAN="puruksapp01"
SQL_RG="PROD-SQL"
REPO="purplepiranhaltd/PiranhaCommerce"
APP_NAME="github-piranhacommerce-deploy"

echo "1. Resource group $RG"
az group create --subscription "$APP_SUB" --name "$RG" --location "$LOCATION" --output none

echo "2. App registration $APP_NAME"
CLIENT_ID=$(az ad app list --display-name "$APP_NAME" --query "[0].appId" -o tsv)
if [ -z "$CLIENT_ID" ]; then CLIENT_ID=$(az ad app create --display-name "$APP_NAME" --query appId -o tsv); fi
az ad sp show --id "$CLIENT_ID" --output none 2>/dev/null || az ad sp create --id "$CLIENT_ID" --output none
SP_ID=$(az ad sp show --id "$CLIENT_ID" --query id -o tsv)

echo "3. Federated credentials (one per GitHub environment; no pull_request credential, so a pull request can never sign in)"
# GitHub can put the numeric ids of the owner and repository in the subject ("repo:owner@id/name@id:..."). Ask GitHub which form this repository uses.
SUBJECT_PREFIX=$(gh api "repos/$REPO/actions/oidc/customization/sub" --jq 'if .use_immutable_subject == true then "immutable" else "plain" end' 2>/dev/null || echo plain)
if [ "$SUBJECT_PREFIX" = "immutable" ]; then
  OWNER_ID=$(gh api "repos/$REPO" --jq .owner.id); REPO_ID=$(gh api "repos/$REPO" --jq .id)
  PREFIX="repo:${REPO%%/*}@$OWNER_ID/${REPO##*/}@$REPO_ID"
else
  PREFIX="repo:$REPO"
fi
for env in infra staging production; do
  name="github-$env"
  params="{\"name\":\"$name\",\"issuer\":\"https://token.actions.githubusercontent.com\",\"subject\":\"$PREFIX:environment:$env\",\"audiences\":[\"api://AzureADTokenExchange\"]}"
  if [ -z "$(az ad app federated-credential list --id "$CLIENT_ID" --query "[?name=='$name'].name" -o tsv)" ]; then
    az ad app federated-credential create --id "$CLIENT_ID" --parameters "$params" --output none
  else
    az ad app federated-credential update --id "$CLIENT_ID" --federated-credential-id "$name" --parameters "$params" --output none
  fi
done

echo "4. Access in the app subscription"
RG_ID="/subscriptions/$APP_SUB/resourceGroups/$RG"
PLAN_ID="/subscriptions/$APP_SUB/resourceGroups/$PLAN_RG/providers/Microsoft.Web/serverfarms/$PLAN"
# Manage everything in the new resource group, and hand out the one role the apps need on their storage.
az role assignment create --assignee-object-id "$SP_ID" --assignee-principal-type ServicePrincipal --role "Contributor" --scope "$RG_ID" --output none
az role assignment create --assignee-object-id "$SP_ID" --assignee-principal-type ServicePrincipal --role "Role Based Access Control Administrator" --scope "$RG_ID" \
  --condition "((!(ActionMatches{'Microsoft.Authorization/roleAssignments/write'})) OR (@Request[Microsoft.Authorization/roleAssignments:RoleDefinitionId] ForAnyOfAnyValues:GuidEquals {ba92f5b4-2d11-453d-a403-e96b0029c9fe})) AND ((!(ActionMatches{'Microsoft.Authorization/roleAssignments/delete'})) OR (@Resource[Microsoft.Authorization/roleAssignments:RoleDefinitionId] ForAnyOfAnyValues:GuidEquals {ba92f5b4-2d11-453d-a403-e96b0029c9fe}))" \
  --condition-version "2.0" --output none
# Put apps on the shared plan, and nothing else on it.
az role assignment create --assignee-object-id "$SP_ID" --assignee-principal-type ServicePrincipal --role "Contributor" --scope "$PLAN_ID" --output none

# Create the DNS records for the custom domains, in these two zones only.
for zone in piranhacommerce.com piranhacommerce.co.uk; do
  az role assignment create --assignee-object-id "$SP_ID" --assignee-principal-type ServicePrincipal --role "DNS Zone Contributor"     --scope "/subscriptions/$APP_SUB/resourceGroups/PROD-DNS/providers/Microsoft.Network/dnszones/$zone" --output none
done

echo "5. Access in the SQL subscription: create the two databases (this role cannot read their data, and has no access to the other databases)"
SQL_ID="/subscriptions/$SQL_SUB/resourceGroups/$SQL_RG/providers/Microsoft.Sql/servers/$SQL_SERVER"
az role assignment create --assignee-object-id "$SP_ID" --assignee-principal-type ServicePrincipal --role "SQL DB Contributor" --scope "$SQL_ID" --output none

TENANT_ID=$(az account show --query tenantId -o tsv)
cat <<EOF

Done. Two more steps are yours, in order:
  a) Run the "Infrastructure" workflow once. It creates the web apps, storage and the two databases.
  b) In EACH of the two new databases (piranhacommerce_staging, piranhacommerce_live) run infra/sql/grant-access.sql yourself, as an Entra
     administrator of the SQL server. The pipeline is deliberately NOT a server administrator: it only becomes db_owner of these two databases.

Create these three GitHub environments (Settings > Environments): infra, staging, production.
Add REVIEWERS to production (and to infra), so nothing reaches production or changes the infrastructure without approval.

In each environment add these secrets (secrets, not variables, so they are masked in logs):
  AZURE_CLIENT_ID             $CLIENT_ID
  AZURE_TENANT_ID             $TENANT_ID
  AZURE_APP_SUBSCRIPTION_ID   $APP_SUB
  AZURE_SQL_SUBSCRIPTION_ID   $SQL_SUB
  SQL_SERVER_NAME             $SQL_SERVER
  SQL_POOL_NAME               $SQL_POOL
In the infra environment also add the two signing keys (each 32+ random characters, different from each other):
  JWT_SIGNING_KEY_PRODUCTION
  JWT_SIGNING_KEY_STAGING
EOF
