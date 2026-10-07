param name string
param location string
param tags object
param environmentId string

@description('Resource id of the user-assigned managed identity.')
param identityId string

@description('Client id of the managed identity; DefaultAzureCredential and SQL use it.')
param identityClientId string

param registryLoginServer string
param keyVaultEndpoint string
param appInsightsConnectionString string
param sqlServerFqdn string
param sqlDatabaseName string

@description('Azure OpenAI endpoint; empty in M1, in which case the variable is not set.')
param openAiEndpoint string = ''

// Placeholder only: `azd deploy` replaces the image with the one built from src/Nachos.Api.
param containerImage string = 'mcr.microsoft.com/azuredocs/containerapps-helloworld:latest'

var targetPort = 8080

// Passwordless: the managed identity authenticates to SQL (its contained user is created by the
// postprovision hook), so this connection string holds no secret.
var sqlConnectionString = 'Server=tcp:${sqlServerFqdn},1433;Database=${sqlDatabaseName};Authentication=Active Directory Managed Identity;User Id=${identityClientId};Encrypt=True;TrustServerCertificate=False;Connection Timeout=30'

// TODO(task-10): how the API loads the JWT signing secret from Key Vault is undecided (Key Vault
// configuration provider using AZURE_KEY_VAULT_ENDPOINT + AZURE_CLIENT_ID, versus an ACA secret
// reference). Only the inputs for the former are provided here; no secret is declared in Bicep.
var baseEnv = [
  { name: 'AZURE_CLIENT_ID', value: identityClientId }
  { name: 'AZURE_KEY_VAULT_ENDPOINT', value: keyVaultEndpoint }
  { name: 'APPLICATIONINSIGHTS_CONNECTION_STRING', value: appInsightsConnectionString }
  { name: 'Nachos__SqlServer__ConnectionString', value: sqlConnectionString }
  { name: 'Nachos__SqlServer__AutomaticSchemaDeploymentEnabled', value: 'false' }
  { name: 'ASPNETCORE_FORWARDEDHEADERS_ENABLED', value: 'true' }
]
var openAiEnv = empty(openAiEndpoint) ? [] : [
  { name: 'AZURE_OPENAI_ENDPOINT', value: openAiEndpoint }
]

resource api 'Microsoft.App/containerApps@2026-01-01' = {
  name: name
  location: location
  // azd finds the app to deploy to by this tag.
  tags: union(tags, { 'azd-service-name': 'api' })
  identity: {
    type: 'UserAssigned'
    userAssignedIdentities: {
      '${identityId}': {}
    }
  }
  properties: {
    environmentId: environmentId
    workloadProfileName: 'Consumption'
    configuration: {
      activeRevisionsMode: 'Single'
      ingress: {
        external: true
        targetPort: targetPort
        transport: 'auto'
        allowInsecure: false
      }
      registries: [
        {
          server: registryLoginServer
          identity: identityId
        }
      ]
    }
    template: {
      containers: [
        {
          name: 'api'
          image: containerImage
          resources: {
            cpu: json('0.5')
            memory: '1Gi'
          }
          env: concat(baseEnv, openAiEnv)
          probes: [
            {
              type: 'Liveness'
              httpGet: {
                path: '/health/live'
                port: targetPort
              }
              initialDelaySeconds: 10
              periodSeconds: 15
            }
            {
              type: 'Readiness'
              httpGet: {
                path: '/health/ready'
                port: targetPort
              }
              initialDelaySeconds: 5
              periodSeconds: 10
            }
          ]
        }
      ]
      scale: {
        minReplicas: 1
        maxReplicas: 10
        rules: [
          {
            name: 'http'
            http: {
              metadata: {
                concurrentRequests: '50'
              }
            }
          }
        ]
      }
    }
  }
}

output name string = api.name
output fqdn string = api.properties.configuration.ingress.fqdn
