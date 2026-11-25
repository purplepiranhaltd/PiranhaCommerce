// The second half of customdomain.bicep: the same binding, now with the certificate's thumbprint.

param appName string
param slotName string = ''
param hostName string
param thumbprint string

resource appBinding 'Microsoft.Web/sites/hostNameBindings@2023-12-01' = if (empty(slotName)) {
  name: '${appName}/${hostName}'
  properties: { siteName: appName, hostNameType: 'Verified', sslState: 'SniEnabled', thumbprint: thumbprint }
}

resource slotBinding 'Microsoft.Web/sites/slots/hostNameBindings@2023-12-01' = if (!empty(slotName)) {
  name: '${appName}/${slotName}/${hostName}'
  properties: { siteName: '${appName}/${slotName}', hostNameType: 'Verified', sslState: 'SniEnabled', thumbprint: thumbprint }
}
