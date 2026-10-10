@description('Vault name: 3-24 characters.')
@minLength(3)
@maxLength(24)
param name string
param location string
param tags object

@description('Principal id of the managed identity that reads secrets at runtime.')
param readerPrincipalId string

@description('Principal id of the deploying user/group/app; it writes the bootstrap secrets from the postprovision hook.')
param officerPrincipalId string

@allowed(['User', 'Group', 'Application'])
param officerPrincipalType string = 'User'

// Defaults to false so `azd down --purge` can fully remove smoke environments; once purge protection
// is on it can never be turned off. Production environments should set this to true.
param enablePurgeProtection bool = false

// Key Vault Secrets User / Key Vault Secrets Officer
var secretsUserRoleId = '4633458b-17de-406a-b3d2-c1eb2f4a4d02'
var secretsOfficerRoleId = 'b86a8fe4-44ce-4948-aee5-eccb2c155cd7'

resource vault 'Microsoft.KeyVault/vaults@2026-02-01' = {
  name: name
  location: location
  tags: tags
  properties: {
    tenantId: tenant().tenantId
    sku: {
      family: 'A'
      name: 'standard'
    }
    enableRbacAuthorization: true
    enableSoftDelete: true
    softDeleteRetentionInDays: 7
    // ARM rejects an explicit false; the property may only be omitted or true.
    enablePurgeProtection: enablePurgeProtection ? true : null
  }
}

resource secretsUser 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  scope: vault
  name: guid(vault.id, readerPrincipalId, secretsUserRoleId)
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', secretsUserRoleId)
    principalId: readerPrincipalId
    principalType: 'ServicePrincipal'
  }
}

resource secretsOfficer 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  scope: vault
  name: guid(vault.id, officerPrincipalId, secretsOfficerRoleId)
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', secretsOfficerRoleId)
    principalId: officerPrincipalId
    // A role assignment calls an app registration a ServicePrincipal.
    principalType: officerPrincipalType == 'User' ? 'User' : officerPrincipalType == 'Group' ? 'Group' : 'ServicePrincipal'
  }
}

output name string = vault.name
output endpoint string = vault.properties.vaultUri
