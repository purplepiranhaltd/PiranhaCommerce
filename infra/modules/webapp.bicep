// One Linux Web App on an existing App Service plan, with a "staging" slot.
// Production settings live on the app and staging settings on the slot, and every setting that differs is "sticky" to its slot,
// so a swap moves the code and leaves each slot pointing at its own database and storage.

@description('The Web App name, for example awa-piranhacommerce.')
param name string

param location string

@description('Resource id of the existing App Service plan (it can be in another resource group).')
param planId string

@description('The runtime, for example DOTNETCORE|10.0.')
param linuxFxVersion string

@description('Give the app and the slot each a system-assigned managed identity, so they can sign in to Azure SQL and Storage without a password.')
param withIdentity bool = false

@description('App settings for the production slot.')
param productionSettings object

@description('App settings for the staging slot. Must have the same names as productionSettings.')
param stagingSettings object

param tags object = {}

var siteConfig = {
  linuxFxVersion: linuxFxVersion
  alwaysOn: true
  http20Enabled: true
  minTlsVersion: '1.2'
  ftpsState: 'Disabled'
  scmMinTlsVersion: '1.2'
}

resource app 'Microsoft.Web/sites@2023-12-01' = {
  name: name
  location: location
  kind: 'app,linux'
  tags: tags
  identity: withIdentity ? { type: 'SystemAssigned' } : null
  properties: {
    serverFarmId: planId
    httpsOnly: true
    clientAffinityEnabled: false
    siteConfig: siteConfig
  }
}

resource appSettings 'Microsoft.Web/sites/config@2023-12-01' = {
  parent: app
  name: 'appsettings'
  properties: productionSettings
}

resource slot 'Microsoft.Web/sites/slots@2023-12-01' = {
  parent: app
  name: 'staging'
  location: location
  kind: 'app,linux'
  tags: tags
  identity: withIdentity ? { type: 'SystemAssigned' } : null
  properties: {
    serverFarmId: planId
    httpsOnly: true
    clientAffinityEnabled: false
    siteConfig: siteConfig
  }
}

resource slotSettings 'Microsoft.Web/sites/slots/config@2023-12-01' = {
  parent: slot
  name: 'appsettings'
  properties: stagingSettings
}

// The names listed here stay with their slot when the slots are swapped.
resource sticky 'Microsoft.Web/sites/config@2023-12-01' = {
  parent: app
  name: 'slotConfigNames'
  properties: {
    appSettingNames: objectKeys(productionSettings)
  }
}

output appName string = app.name
output slotName string = slot.name
output defaultHostName string = app.properties.defaultHostName
output slotHostName string = slot.properties.defaultHostName
output verificationId string = app.properties.customDomainVerificationId
output inboundIp string = reference(app.id, '2023-12-01').inboundIpAddress
output appPrincipalId string = withIdentity ? app.identity.principalId : ''
output slotPrincipalId string = withIdentity ? slot.identity.principalId : ''
