// The first half of a custom domain: bind the host name to the app (or its slot) without TLS, so a certificate can be issued for it.
// Kept apart from customdomain.bicep so that the certificate can depend on this one module, whichever of the two resources exists.

param appName string
@description('Leave empty for the production slot.')
param slotName string = ''
param hostName string

resource appBinding 'Microsoft.Web/sites/hostNameBindings@2023-12-01' = if (empty(slotName)) {
  name: '${appName}/${hostName}'
  properties: { siteName: appName, hostNameType: 'Verified', sslState: 'Disabled' }
}

resource slotBinding 'Microsoft.Web/sites/slots/hostNameBindings@2023-12-01' = if (!empty(slotName)) {
  name: '${appName}/${slotName}/${hostName}'
  properties: { siteName: '${appName}/${slotName}', hostNameType: 'Verified', sslState: 'Disabled' }
}
