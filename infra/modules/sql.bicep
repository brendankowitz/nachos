param serverName string
param databaseName string
param location string
param tags object

@description('Entra object id of the SQL administrator (the deploying principal).')
param adminPrincipalId string

@description('Entra login (UPN or group/app display name) of the SQL administrator.')
param adminLogin string

@allowed(['User', 'Group', 'Application'])
param adminPrincipalType string = 'User'

// Entra-only: there is deliberately no SQL administrator login or password anywhere. The managed
// identity's contained user is created by the postprovision hook, not here (spec F3).
resource server 'Microsoft.Sql/servers@2025-01-01' = {
  name: serverName
  location: location
  tags: tags
  properties: {
    version: '12.0'
    minimalTlsVersion: '1.2'
    publicNetworkAccess: 'Enabled'
    administrators: {
      administratorType: 'ActiveDirectory'
      azureADOnlyAuthentication: true
      login: adminLogin
      // For adminPrincipalType 'Application' the SID Azure SQL expects may be the app (client) id rather than
      // the object id; that is unverified offline, so the object id is passed as-is for every type.
      sid: adminPrincipalId
      tenantId: tenant().tenantId
      principalType: adminPrincipalType
    }
  }
}

// M1 trade-off: the server keeps a public endpoint (Entra-only auth, TLS 1.2) and this rule admits
// Azure services, which is how Container Apps reaches it. Private networking is out of scope for M1.
resource allowAzureServices 'Microsoft.Sql/servers/firewallRules@2025-01-01' = {
  parent: server
  name: 'AllowAllWindowsAzureIps'
  properties: {
    startIpAddress: '0.0.0.0'
    endIpAddress: '0.0.0.0'
  }
}

resource database 'Microsoft.Sql/servers/databases@2025-01-01' = {
  parent: server
  name: databaseName
  location: location
  tags: tags
  sku: {
    name: 'GP_S_Gen5'
    tier: 'GeneralPurpose'
    family: 'Gen5'
    capacity: 1
  }
  properties: {
    // Auto-pause stays off (-1): a paused serverless database makes the first request time out (spec R8).
    autoPauseDelay: -1
    minCapacity: json('0.5')
  }
}

output serverName string = server.name
output serverFqdn string = server.properties.fullyQualifiedDomainName
output databaseName string = database.name
