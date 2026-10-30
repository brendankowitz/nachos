targetScope = 'subscription'

@description('azd environment name; names the resource group and tags every resource.')
@minLength(1)
@maxLength(64)
param environmentName string

@description('Azure region for all resources.')
param location string

@description('Entra object id of the deploying principal. It becomes the SQL Entra admin and a Key Vault Secrets Officer.')
param principalId string

@description('Entra login (UPN, or display name for a group/app) of the deploying principal.')
param principalLogin string

@description('Kind of principal that principalId identifies; SQL requires it for the Entra admin.')
@allowed(['User', 'Group', 'Application'])
param principalType string = 'User'

// azd-templates convention: a bool parameter fed by "${SERVICE_API_RESOURCE_EXISTS=false}" in
// main.parameters.json (azd converts the substituted text to the declared type). Not provable offline.
@description('Whether the API container app has already been deployed (azd: SERVICE_API_RESOURCE_EXISTS).')
param apiExists bool

@description('Endpoint of an EXISTING Azure OpenAI account (bring your own; Bicep never provisions one). Empty runs the offline fake AI, which is not a memory-quality mode.')
param openAiEndpoint string = ''

@description('Name of that existing account. When set, the managed identity is granted Cognitive Services OpenAI User on it; leave empty if the role is granted elsewhere.')
param openAiAccountName string = ''

@description('Resource group of that account in this subscription; empty means the deployment resource group.')
param openAiResourceGroupName string = ''

@description('Chat deployment name on the account (a deployment name, not a model name).')
param openAiChatDeployment string = 'gpt-4.1-mini'

@description('Embedding deployment name on the account; the model must produce 1536 dimensions, for example text-embedding-3-small.')
param openAiEmbeddingDeployment string = 'text-embedding-3-small'

// Same azd-templates convention as apiExists (SERVICE_WORKER_RESOURCE_EXISTS); not provable offline.
@description('Whether the worker container app has already been deployed (azd: SERVICE_WORKER_RESOURCE_EXISTS).')
param workerExists bool

@description('Lower bound for worker replicas; at least 1 until the KEDA mssql rule is verified.')
@minValue(1)
param workerMinReplicas int = 1

// Image of the very first revision, until `azd deploy` replaces it. mendhak/http-https-echo (MIT) listens on 8080
// and answers 200 on every path, so the always-on /health probes pass from revision 1 and azd copies them onto the
// first real revision. It is third-party code running in an app that carries the managed identity (Key Vault and
// SQL access) and echoes request headers to callers until the first deploy: hence the digest pin (a tag can be
// re-pushed) and an owner-visible exposure window. Overridable (NACHOS_PLACEHOLDER_IMAGE) if GHCR is unreachable.
// To replace it, keep a digest-only reference and re-run the Placeholder_Answers_ProbePaths test.
@description('Digest-pinned placeholder image for the first revision (answers 200 on /health/live and /health/ready on 8080).')
param placeholderImage string = 'ghcr.io/mendhak/http-https-echo@sha256:a265f55c86cb3baead76fdf379dc8e9e6440ed121874e101e60b833e05bce8d4'

var tags = { 'azd-env-name': environmentName }
var resourceToken = toLower(uniqueString(subscription().id, environmentName, location))
var sqlDatabaseName = 'nachos'

resource rg 'Microsoft.Resources/resourceGroups@2025-04-01' = {
  name: 'rg-${environmentName}'
  location: location
  tags: tags
}

module identity 'modules/identity.bicep' = {
  scope: rg
  name: 'identity'
  params: {
    name: 'id-${resourceToken}'
    location: location
    tags: tags
  }
}

module monitoring 'modules/monitoring.bicep' = {
  scope: rg
  name: 'monitoring'
  params: {
    logAnalyticsName: 'log-${resourceToken}'
    applicationInsightsName: 'appi-${resourceToken}'
    location: location
    tags: tags
  }
}

module registry 'modules/registry.bicep' = {
  scope: rg
  name: 'registry'
  params: {
    name: 'acr${resourceToken}'
    location: location
    tags: tags
    pullPrincipalId: identity.outputs.principalId
  }
}

module keyVault 'modules/keyvault.bicep' = {
  scope: rg
  name: 'keyvault'
  params: {
    name: 'kv-${resourceToken}'
    location: location
    tags: tags
    readerPrincipalId: identity.outputs.principalId
    officerPrincipalId: principalId
    officerPrincipalType: principalType
  }
}

module sql 'modules/sql.bicep' = {
  scope: rg
  name: 'sql'
  params: {
    serverName: 'sql-${resourceToken}'
    databaseName: sqlDatabaseName
    location: location
    tags: tags
    adminPrincipalId: principalId
    adminLogin: principalLogin
    adminPrincipalType: principalType
  }
}

module containerAppsEnvironment 'modules/containerapps-env.bicep' = {
  scope: rg
  name: 'containerapps-env'
  params: {
    name: 'cae-${resourceToken}'
    location: location
    tags: tags
    logAnalyticsName: monitoring.outputs.logAnalyticsName
  }
}

module apiApp 'modules/api-app.bicep' = {
  scope: rg
  name: 'api-app'
  params: {
    name: 'nachos-api'
    location: location
    tags: tags
    environmentId: containerAppsEnvironment.outputs.id
    identityId: identity.outputs.id
    identityClientId: identity.outputs.clientId
    registryLoginServer: registry.outputs.loginServer
    keyVaultEndpoint: keyVault.outputs.endpoint
    appInsightsConnectionString: monitoring.outputs.applicationInsightsConnectionString
    sqlServerFqdn: sql.outputs.serverFqdn
    sqlDatabaseName: sql.outputs.databaseName
    openAiEndpoint: openAiEndpoint
    openAiChatDeployment: openAiChatDeployment
    openAiEmbeddingDeployment: openAiEmbeddingDeployment
    apiExists: apiExists
    containerImage: placeholderImage
  }
}

module workerApp 'modules/worker-app.bicep' = {
  scope: rg
  name: 'worker-app'
  params: {
    name: 'nachos-worker'
    location: location
    tags: tags
    environmentId: containerAppsEnvironment.outputs.id
    identityId: identity.outputs.id
    identityClientId: identity.outputs.clientId
    registryLoginServer: registry.outputs.loginServer
    appInsightsConnectionString: monitoring.outputs.applicationInsightsConnectionString
    sqlServerFqdn: sql.outputs.serverFqdn
    sqlDatabaseName: sql.outputs.databaseName
    openAiEndpoint: openAiEndpoint
    openAiChatDeployment: openAiChatDeployment
    openAiEmbeddingDeployment: openAiEmbeddingDeployment
    workerExists: workerExists
    workerMinReplicas: workerMinReplicas
    containerImage: placeholderImage
  }
}

// The API and the worker share one identity, so one grant covers both. The account belongs to the owner, so only the
// role assignment is Bicep's; the account may live in another resource group of the subscription.
module openAiAccess 'modules/openai-access.bicep' = if (!empty(openAiAccountName)) {
  scope: resourceGroup(empty(openAiResourceGroupName) ? rg.name : openAiResourceGroupName)
  name: 'openai-access'
  params: {
    accountName: openAiAccountName
    principalId: identity.outputs.principalId
  }
}

// Outputs become azd environment values, which the hooks and `azd deploy` read.
output AZURE_LOCATION string = location
output AZURE_TENANT_ID string = tenant().tenantId
output AZURE_RESOURCE_GROUP string = rg.name
output AZURE_CONTAINER_REGISTRY_ENDPOINT string = registry.outputs.loginServer
output AZURE_CONTAINER_REGISTRY_NAME string = registry.outputs.name
output AZURE_KEY_VAULT_NAME string = keyVault.outputs.name
output AZURE_KEY_VAULT_ENDPOINT string = keyVault.outputs.endpoint
output AZURE_SQL_SERVER_NAME string = sql.outputs.serverName
output AZURE_SQL_SERVER_FQDN string = sql.outputs.serverFqdn
output AZURE_SQL_DATABASE_NAME string = sql.outputs.databaseName
output AZURE_MANAGED_IDENTITY_NAME string = identity.outputs.name
output AZURE_MANAGED_IDENTITY_CLIENT_ID string = identity.outputs.clientId
output NACHOS_API_URI string = 'https://${apiApp.outputs.fqdn}'
