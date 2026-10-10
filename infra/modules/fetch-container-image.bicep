@description('Name of the container app that is already deployed.')
param name string

// Reads the image `azd deploy` put on the running app and its env (for the signing-key ring), so
// re-provisioning keeps both instead of reverting to the placeholder and a fresh ring. Only instantiated
// when the app exists.
resource app 'Microsoft.App/containerApps@2026-01-01' existing = {
  name: name
}

output image string = app.properties.template.containers[0].image
output env array = app.properties.template.containers[0].?env ?? []
