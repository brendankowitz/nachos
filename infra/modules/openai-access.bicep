@description('Name of the existing Azure OpenAI account (Cognitive Services, kind OpenAI). It is never created or changed here.')
param accountName string

@description('Principal id of the managed identity that calls Azure OpenAI.')
param principalId string

// Cognitive Services OpenAI User: call the data plane (chat, embeddings) without managing the account.
var openAiUserRoleId = '5e0bd9bd-7b93-4f28-af87-19fc36ad61bd'

resource account 'Microsoft.CognitiveServices/accounts@2025-06-01' existing = {
  name: accountName
}

resource openAiUser 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  scope: account
  name: guid(account.id, principalId, openAiUserRoleId)
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', openAiUserRoleId)
    principalId: principalId
    principalType: 'ServicePrincipal'
  }
}
