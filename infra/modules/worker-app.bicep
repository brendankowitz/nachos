param name string
param location string
param tags object
param environmentId string

@description('Resource id of the user-assigned managed identity.')
param identityId string

@description('Client id of the user-assigned managed identity. The worker uses it (AZURE_CLIENT_ID) for SQL and Azure OpenAI.')
param identityClientId string

param registryLoginServer string
param appInsightsConnectionString string
param sqlServerFqdn string
param sqlDatabaseName string

@description('Azure OpenAI endpoint. When empty the worker runs the offline fake AI, which is not a memory-quality mode.')
param openAiEndpoint string = ''

@description('Azure OpenAI chat deployment name (a deployment on the existing account, not a model name).')
param openAiChatDeployment string

@description('Azure OpenAI embedding deployment name; the model must produce 1536 dimensions (for example text-embedding-3-small).')
param openAiEmbeddingDeployment string

@description('True once the app exists (azd sets SERVICE_WORKER_RESOURCE_EXISTS). Then the image currently on the app is kept.')
param workerExists bool

@description('Image for the very first revision, before `azd deploy` replaces it (main.bicep: placeholderImage).')
param containerImage string

// The queue is claimed with leases, so more than one replica is safe. Zero would stop memory formation until the R7
// KEDA scale rule below is verified, because nothing else wakes the app.
@description('Lower bound for replicas. At least 1 until the KEDA mssql rule is verified (plan risk R7).')
@minValue(1)
param workerMinReplicas int = 1

@description('Upper bound for replicas; the CPU rule scales between the bounds.')
@minValue(1)
param workerMaxReplicas int = 3

module existingImage 'fetch-container-image.bicep' = if (workerExists) {
  name: '${name}-image'
  params: {
    name: name
  }
}

var image = workerExists ? existingImage!.outputs.image : containerImage

// Passwordless, like the API: the managed identity is the contained SQL user the postprovision hook creates.
var sqlConnectionString = 'Server=tcp:${sqlServerFqdn},1433;Database=${sqlDatabaseName};Authentication=Active Directory Managed Identity;User Id=${identityClientId};Encrypt=True;TrustServerCertificate=False;Connection Timeout=30'

var baseEnv = [
  { name: 'AZURE_CLIENT_ID', value: identityClientId }
  { name: 'APPLICATIONINSIGHTS_CONNECTION_STRING', value: appInsightsConnectionString }
  { name: 'Nachos__SqlServer__ConnectionString', value: sqlConnectionString }
  { name: 'Nachos__SqlServer__AutomaticSchemaDeploymentEnabled', value: 'false' }
]
// Without an endpoint the offline fake AI runs; say so explicitly so a deployment never falls into it by accident.
var openAiEnv = empty(openAiEndpoint) ? [
  { name: 'Nachos__Ai__Mode', value: 'Fake' }
] : [
  { name: 'AzureOpenAI__Endpoint', value: openAiEndpoint }
  { name: 'AzureOpenAI__ChatDeployment', value: openAiChatDeployment }
  { name: 'AzureOpenAI__EmbeddingDeployment', value: openAiEmbeddingDeployment }
]

resource worker 'Microsoft.App/containerApps@2026-01-01' = {
  name: name
  location: location
  // azd finds the app to deploy to by this tag.
  tags: union(tags, { 'azd-service-name': 'worker' })
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
      // No ingress: the worker only makes outbound calls (SQL, Azure OpenAI).
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
          name: 'worker'
          image: image
          resources: {
            cpu: json('0.5')
            memory: '1Gi'
          }
          env: concat(baseEnv, openAiEnv)
        }
      ]
      scale: {
        minReplicas: workerMinReplicas
        maxReplicas: workerMaxReplicas
        rules: [
          {
            name: 'cpu'
            custom: {
              type: 'cpu'
              metadata: {
                type: 'Utilization'
                value: '70'
              }
            }
          }
          // R7 gate: enable only after an owner-approved run proves the mssql scaler works with the managed identity on
          // Container Apps, then allow workerMinReplicas = 0. The GetDueWorkCount procedure does not exist yet (plan D2).
          // {
          //   name: 'queue'
          //   custom: {
          //     type: 'mssql'
          //     identity: identityId
          //     metadata: {
          //       query: 'EXEC dbo.GetDueWorkCount'
          //       targetValue: '1'
          //     }
          //   }
          // }
        ]
      }
    }
  }
}

output name string = worker.name
