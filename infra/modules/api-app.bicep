param name string
param location string
param tags object
param environmentId string

@description('Resource id of the user-assigned managed identity.')
param identityId string

@description('Client id of the user-assigned managed identity. The API uses it (AZURE_CLIENT_ID) for Key Vault and SQL.')
param identityClientId string

param registryLoginServer string
param keyVaultEndpoint string
param appInsightsConnectionString string
param sqlServerFqdn string
param sqlDatabaseName string

@description('Azure OpenAI endpoint; empty in M1, in which case the variable is not set.')
param openAiEndpoint string = ''

@description('True once the app exists (azd sets SERVICE_API_RESOURCE_EXISTS). Then the image and the key ring currently on the app are kept.')
param apiExists bool

@description('Image for the very first revision, before `azd deploy` replaces it (main.bicep: placeholderImage).')
param containerImage string

var targetPort = 8080

// Without this, every later `azd provision` would put the placeholder back over the deployed image and reset
// the signing-key ring.
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

// Signing-key contract (issue #2): with auth enabled, the API reads Key Vault secret
// `nachos-signing-key-{Keys[i].Kid}` from AZURE_KEY_VAULT_ENDPOINT as the user-assigned identity AZURE_CLIENT_ID,
// for each ring entry in order. It fails closed: no ring or an unreadable secret stops startup, and there is no
// local-secret fallback. No secret value is declared in Bicep.
var baseEnv = [
  { name: 'AZURE_CLIENT_ID', value: identityClientId }
  { name: 'AZURE_KEY_VAULT_ENDPOINT', value: keyVaultEndpoint }
  { name: 'APPLICATIONINSIGHTS_CONNECTION_STRING', value: appInsightsConnectionString }
  { name: 'Nachos__SqlServer__ConnectionString', value: sqlConnectionString }
  { name: 'Nachos__SqlServer__AutomaticSchemaDeploymentEnabled', value: 'false' }
  { name: 'ASPNETCORE_FORWARDEDHEADERS_ENABLED', value: 'true' }
  { name: 'Nachos__Auth__Enabled', value: 'true' }
]

// Ownership rule: Bicep owns every env var EXCEPT the Nachos__Auth__NachosKey__Keys__* namespace, which is
// rotation state owned by the running app. Bicep copies that namespace verbatim (in order, whole objects, so a
// secretRef entry stays one; note its secret would also have to be kept in configuration.secrets, which the
// Key Vault contract does not use) and only seeds Keys__0__Kid=0 when the namespace is empty. baseEnv must
// never set a name with this prefix.
var ringPrefix = 'Nachos__Auth__NachosKey__Keys__'
var existingEnv = apiExists ? existingImage!.outputs.env : []
var existingRing = filter(existingEnv, e => startsWith(e.name, ringPrefix))
var ringEnv = empty(existingRing) ? [ { name: '${ringPrefix}0__Kid', value: '0' } ] : existingRing
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
          env: concat(baseEnv, ringEnv, openAiEnv)
          // Always on. `azd deploy` copies the live app and swaps only the image, so the first real revision gets
          // exactly these probes; the placeholder answers 200 on every path, so revision 1 is healthy too.
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
