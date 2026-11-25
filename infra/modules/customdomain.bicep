// A custom domain on a web app (or one of its slots) with a free App Service managed certificate.
// The DNS records must exist first (dnsrecords.bicep). The host name is bound without TLS, the certificate is issued for it,
// then the binding is made again with the certificate attached (a binding cannot be given a certificate that does not exist yet).

param appName string
@description('Leave empty for the production slot.')
param slotName string = ''
param hostName string
param location string
param planId string
@description('The zone apex has no CNAME to prove ownership with, so it is validated by an HTTP token; sub-domains by their CNAME.')
param isApex bool
param tags object = {}

module bind 'plainbinding.bicep' = {
  name: 'bind-${replace(hostName, '.', '-')}'
  params: { appName: appName, slotName: slotName, hostName: hostName }
}

resource certificate 'Microsoft.Web/certificates@2023-12-01' = {
  name: replace(hostName, '.', '-')
  location: location
  tags: tags
  properties: {
    serverFarmId: planId
    canonicalName: hostName
    domainValidationMethod: isApex ? 'http-token' : 'cname-delegation'
  }
  dependsOn: [bind]
}

module secure 'securebinding.bicep' = {
  name: 'secure-${replace(hostName, '.', '-')}'
  params: { appName: appName, slotName: slotName, hostName: hostName, thumbprint: certificate.properties.thumbprint }
}
