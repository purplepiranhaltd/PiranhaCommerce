// PiranhaCommerce: the promo and docs site, and the sample shop, on Azure App Service, with their storage.
// Deploy to the resource group PiranhaCommerce in the app subscription (it is created once by infra/bootstrap.sh).
// The databases are in a different subscription and are made by sql.bicep.

targetScope = 'resourceGroup'

param location string = 'uksouth'

@description('The existing shared App Service plan (Linux, UK South).')
param planName string = 'puruksapp01'
param planResourceGroup string = 'PROD-APP'

@description('The promo and docs site.')
param siteAppName string = 'awa-piranhacommerce'

@description('The sample shop and its admin.')
param shopAppName string = 'awa-piranhacommerce-demo'

param productionStorageName string = 'piranhacommerceprod'
param stagingStorageName string = 'piranhacommercestaging'

@description('The runtime. Use DOTNETCORE|10.0 for zip deploys.')
param linuxFxVersion string = 'DOTNETCORE|10.0'

@description('The SQL server name, without the domain.')
param sqlServerName string
param productionDatabaseName string = 'piranhacommerce_live'
param stagingDatabaseName string = 'piranhacommerce_staging'

@secure()
@description('A secret of 32 or more characters that signs the shop API tokens. A different one per environment.')
param productionJwtSigningKey string

@secure()
param stagingJwtSigningKey string

@description('Seed the sample catalogue and administrator at startup (PiranhaCommerce:Demo:SeedOnStartup).')
param productionDemoSeed bool = false
param stagingDemoSeed bool = true

@description('The Azure DNS zones (piranhacommerce.com and piranhacommerce.co.uk) and the resource group that holds them.')
param dnsZones array = ['piranhacommerce.com', 'piranhacommerce.co.uk']
param dnsResourceGroup string = 'PROD-DNS'

param tags object = {}

var storageSuffix = environment().suffixes.storage
var sqlServerFqdn = '${sqlServerName}${environment().suffixes.sqlServerHostname}'
var blobContributorRoleId = 'ba92f5b4-2d11-453d-a403-e96b0029c9fe' // Storage Blob Data Contributor

resource plan 'Microsoft.Web/serverfarms@2023-12-01' existing = {
  name: planName
  scope: resourceGroup(planResourceGroup)
}

module productionStorage 'modules/storage.bicep' = {
  name: 'storage-production'
  params: { name: productionStorageName, location: location, tags: tags }
}

module stagingStorage 'modules/storage.bicep' = {
  name: 'storage-staging'
  params: { name: stagingStorageName, location: location, tags: tags }
}

var common = {
  ASPNETCORE_ENVIRONMENT: 'Production'
  ASPNETCORE_FORWARDEDHEADERS_ENABLED: 'true' // behind App Service's proxy: the real scheme and address
  Hosting__RedirectFrom: 'piranhacommerce.co.uk' // .com is the main domain; .co.uk redirects to it
  Hosting__RedirectTo: 'piranhacommerce.com'
}

module site 'modules/webapp.bicep' = {
  name: 'webapp-site'
  params: {
    name: siteAppName
    location: location
    planId: plan.id
    linuxFxVersion: linuxFxVersion
    productionSettings: common
    stagingSettings: common
    tags: tags
  }
}

func sqlConnection(server string, database string) string =>
  'Server=tcp:${server},1433;Database=${database};Authentication=Active Directory Managed Identity;Encrypt=True;TrustServerCertificate=False;Connection Timeout=30;'

module shop 'modules/webapp.bicep' = {
  name: 'webapp-shop'
  params: {
    name: shopAppName
    location: location
    planId: plan.id
    linuxFxVersion: linuxFxVersion
    withIdentity: true
    productionSettings: union(common, {
      PiranhaCommerce__Provider: 'SqlServer'
      ConnectionStrings__PiranhaCommerce: sqlConnection(sqlServerFqdn, productionDatabaseName)
      PiranhaCommerce__Jwt__SigningKey: productionJwtSigningKey
      PiranhaCommerce__Storage__AzureBlob__AccountUri: 'https://${productionStorageName}.blob.${storageSuffix}'
      PiranhaCommerce__Storage__Provider: 'AzureBlob'
      PiranhaCommerce__Demo__SeedOnStartup: string(productionDemoSeed)
      PiranhaCommerce__SampleData__Enabled: string(productionDemoSeed)
    })
    stagingSettings: union(common, {
      PiranhaCommerce__Provider: 'SqlServer'
      ConnectionStrings__PiranhaCommerce: sqlConnection(sqlServerFqdn, stagingDatabaseName)
      PiranhaCommerce__Jwt__SigningKey: stagingJwtSigningKey
      PiranhaCommerce__Storage__AzureBlob__AccountUri: 'https://${stagingStorageName}.blob.${storageSuffix}'
      PiranhaCommerce__Storage__Provider: 'AzureBlob'
      PiranhaCommerce__Demo__SeedOnStartup: string(stagingDemoSeed)
      PiranhaCommerce__SampleData__Enabled: string(stagingDemoSeed)
    })
    tags: tags
  }
}

// Each slot may use only its own storage account: production's identity reaches production storage, the staging slot's reaches staging.
resource productionAccount 'Microsoft.Storage/storageAccounts@2023-05-01' existing = {
  name: productionStorageName
  dependsOn: [productionStorage]
}

resource stagingAccount 'Microsoft.Storage/storageAccounts@2023-05-01' existing = {
  name: stagingStorageName
  dependsOn: [stagingStorage]
}

resource productionBlobAccess 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  scope: productionAccount
  name: guid(productionAccount.id, shopAppName, blobContributorRoleId)
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', blobContributorRoleId)
    principalId: shop.outputs.appPrincipalId
    principalType: 'ServicePrincipal'
  }
}

resource stagingBlobAccess 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  scope: stagingAccount
  name: guid(stagingAccount.id, shopAppName, 'staging', blobContributorRoleId)
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', blobContributorRoleId)
    principalId: shop.outputs.slotPrincipalId
    principalType: 'ServicePrincipal'
  }
}

// Custom domains, for each zone: the site at the apex (staging.<zone> for its slot) and the shop at demo.<zone> (demo.staging.<zone>).
var hostPlan = [
  { prefix: '', app: siteAppName, slot: '' }
  { prefix: 'staging.', app: siteAppName, slot: 'staging' }
  { prefix: 'demo.', app: shopAppName, slot: '' }
  { prefix: 'demo.staging.', app: shopAppName, slot: 'staging' }
]
// Known at the start of the deployment, so a loop may use it.
var domainNames = flatten(map(dnsZones, zone => map(hostPlan, h => {
  zone: zone
  host: '${h.prefix}${zone}'
  app: h.app
  slot: h.slot
})))

// What each name points at is only known once the apps exist.
var endpoints = {
  '${siteAppName}|': { target: site.outputs.defaultHostName, ip: site.outputs.inboundIp, id: site.outputs.verificationId }
  '${siteAppName}|staging': { target: site.outputs.slotHostName, ip: site.outputs.inboundIp, id: site.outputs.verificationId }
  '${shopAppName}|': { target: shop.outputs.defaultHostName, ip: shop.outputs.inboundIp, id: shop.outputs.verificationId }
  '${shopAppName}|staging': { target: shop.outputs.slotHostName, ip: shop.outputs.inboundIp, id: shop.outputs.verificationId }
}
var domains = map(domainNames, d => {
  zone: d.zone
  host: d.host
  target: endpoints['${d.app}|${d.slot}'].target
  ip: endpoints['${d.app}|${d.slot}'].ip
  verificationId: endpoints['${d.app}|${d.slot}'].id
})

module dns 'modules/dnsrecords.bicep' = {
  name: 'dns-records'
  scope: resourceGroup(dnsResourceGroup)
  params: { domains: domains }
}

// One at a time: App Service refuses a change to an app while another change to it is still in progress, so parallel bindings fail with Conflict.
@batchSize(1)
module customDomains 'modules/customdomain.bicep' = [for d in domainNames: {
  name: 'domain-${replace(d.host, '.', '-')}'
  params: {
    appName: d.app
    slotName: d.slot
    hostName: d.host
    location: location
    planId: plan.id
    isApex: d.host == d.zone
    tags: tags
  }
  dependsOn: [dns]
}]

output siteHost string = site.outputs.defaultHostName
output siteStagingHost string = site.outputs.slotHostName
output shopHost string = shop.outputs.defaultHostName
output shopStagingHost string = shop.outputs.slotHostName
output shopAppName string = shop.outputs.appName
