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

@description('True once the app exists (azd sets SERVICE_API_RESOURCE_EXISTS). Then the image currently on the app is kept.')
param apiExists bool

// Placeholder for the very first provision only. It listens on 8080 like the real API (the older
// containerapps-helloworld image listens on 80, so its revision could never become ready).
param containerImage string = 'mcr.microsoft.com/dotnet/samples:aspnetapp'

var targetPort = 8080

// Without this, every later `azd provision` would put the placeholder back over the deployed image.
module existingImage 'fetch-container-image.bicep' = if (apiExists) {
  name: '${name}-image'
  params: {
    name: name
  }
}

// The image this revision runs: the one already on the app, else the placeholder.
var image = apiExists ? existingImage!.outputs.image : containerImage

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
          image: image
          resources: {
            cpu: json('0.5')
            memory: '1Gi'
          }
          env: concat(baseEnv, openAiEnv)
          // Probes follow the image in use, not apiExists: azd sets apiExists as soon as the app exists, which can
          // be before any real image was deployed (a failed postprovision, or a second `azd provision`). While the
          // placeholder runs there are no /health routes, so explicit HTTP probes would keep the revision from ever
          // becoming ready (ACA's default TCP probes on the target port apply instead). They switch on at the first
          // provision after `azd deploy` replaced the image; the postdeploy hook smoke-tests /health/ready.
          probes: image != containerImage ? [
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
          ] : []
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
