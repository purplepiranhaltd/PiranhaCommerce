// The Azure DNS records that point custom domains at a web app and prove the domain belongs to us.
// Deploy to the resource group that holds the DNS zones.

@description('One entry per custom domain: zone (piranhacommerce.com), host (the full name), target (the default host name to point a subdomain at), ip (for the zone apex) and verificationId.')
param domains array

resource zones 'Microsoft.Network/dnsZones@2018-05-01' existing = [for d in domains: {
  name: d.zone
}]

// "@" is the zone apex (a CNAME is not allowed there, so it gets an A record); anything else is a CNAME.
var relativeNames = [for d in domains: d.host == d.zone ? '@' : substring(d.host, 0, length(d.host) - length(d.zone) - 1)]

resource addresses 'Microsoft.Network/dnsZones/A@2018-05-01' = [for (d, i) in domains: if (relativeNames[i] == '@') {
  parent: zones[i]
  name: '@'
  properties: {
    TTL: 3600
    ARecords: [{ ipv4Address: d.ip }]
  }
}]

resource aliases 'Microsoft.Network/dnsZones/CNAME@2018-05-01' = [for (d, i) in domains: if (relativeNames[i] != '@') {
  parent: zones[i]
  name: relativeNames[i]
  properties: {
    TTL: 3600
    CNAMERecord: { cname: d.target }
  }
}]

resource proofs 'Microsoft.Network/dnsZones/TXT@2018-05-01' = [for (d, i) in domains: {
  parent: zones[i]
  name: relativeNames[i] == '@' ? 'asuid' : 'asuid.${relativeNames[i]}'
  properties: {
    TTL: 3600
    TXTRecords: [{ value: [d.verificationId] }]
  }
}]
