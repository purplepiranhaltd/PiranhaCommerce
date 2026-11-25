// The two sample-shop databases, on the existing SQL server and in its existing elastic pool.
// Deploy to the resource group PROD-SQL in the SQL subscription. Both databases live in the pool, which already caps each database.

targetScope = 'resourceGroup'

param location string = 'westeurope'

@description('The existing SQL server (name only). Not kept in the repository: the pipeline passes it in.')
param serverName string
@description('The existing elastic pool on that server. Also passed in.')
param poolName string

@description('The tier of the pool (Basic, Standard or Premium). A database in a pool must say it.')
param poolTier string = 'Basic'

param productionDatabaseName string = 'piranhacommerce_live'
param stagingDatabaseName string = 'piranhacommerce_staging'

param tags object = {}

resource server 'Microsoft.Sql/servers@2023-08-01' existing = {
  name: serverName
}

resource pool 'Microsoft.Sql/servers/elasticPools@2023-08-01' existing = {
  parent: server
  name: poolName
}

resource databases 'Microsoft.Sql/servers/databases@2023-08-01' = [for name in [productionDatabaseName, stagingDatabaseName]: {
  parent: server
  name: name
  location: location
  tags: tags
  sku: {
    name: 'ElasticPool'
    tier: poolTier
  }
  properties: {
    elasticPoolId: pool.id
    collation: 'SQL_Latin1_General_CP1_CI_AS'
    maxSizeBytes: 1073741824 // 1 GB: plenty for the sample, and well inside the pool
    zoneRedundant: false
  }
}]
