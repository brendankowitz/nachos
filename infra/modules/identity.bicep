@description('Name of the user-assigned managed identity.')
param name string
param location string
param tags object

// One identity for the apps: it pulls images, reads Key Vault secrets, and signs in to Azure SQL.
resource identity 'Microsoft.ManagedIdentity/userAssignedIdentities@2024-11-30' = {
  name: name
  location: location
  tags: tags
}

output id string = identity.id
output name string = identity.name
output clientId string = identity.properties.clientId
output principalId string = identity.properties.principalId
