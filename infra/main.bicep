// Azure resources for the AppointmentsApi sample:
//   Log Analytics -> Container Apps environment -> Container App (the API)
//   Azure SQL server + serverless database (auto-pauses when idle to keep cost low)
//
// Deploy:
//   az deployment group create -g <resource-group> --template-file infra/main.bicep \
//     --parameters containerImage=ghcr.io/<you>/mininet-appointments:<tag> sqlAdminPassword=<strong password>

@description('Azure region for all resources.')
param location string = resourceGroup().location

@description('Base name used for resource names.')
param appName string = 'mininet-appointments'

@description('Container image to run, for example ghcr.io/<user>/mininet-appointments:abc1234.')
param containerImage string

@description('SQL administrator login name.')
param sqlAdminLogin string = 'mininetadmin'

@secure()
@description('SQL administrator password. Azure requires 8+ characters from three of: upper case, lower case, digits, symbols.')
param sqlAdminPassword string

@description('Container registry user name. Leave the password empty if the image is public.')
param registryUsername string = ''

@secure()
@description('Token for pulling a private image from ghcr.io (a GitHub PAT with read:packages). Leave empty for a public image.')
param registryPassword string = ''

var suffix = uniqueString(resourceGroup().id)
var useRegistryCredentials = !empty(registryPassword)

resource logs 'Microsoft.OperationalInsights/workspaces@2023-09-01' = {
  name: '${appName}-logs-${suffix}'
  location: location
  properties: {
    sku: { name: 'PerGB2018' }
    retentionInDays: 30
  }
}

resource sqlServer 'Microsoft.Sql/servers@2021-11-01' = {
  name: '${appName}-sql-${suffix}'
  location: location
  properties: {
    administratorLogin: sqlAdminLogin
    administratorLoginPassword: sqlAdminPassword
    minimalTlsVersion: '1.2'
    publicNetworkAccess: 'Enabled'
  }
}

// 0.0.0.0 - 0.0.0.0 is Azure's special rule for "allow other Azure services", which includes Container Apps.
resource allowAzureServices 'Microsoft.Sql/servers/firewallRules@2021-11-01' = {
  parent: sqlServer
  name: 'AllowAzureServices'
  properties: {
    startIpAddress: '0.0.0.0'
    endIpAddress: '0.0.0.0'
  }
}

resource database 'Microsoft.Sql/servers/databases@2021-11-01' = {
  parent: sqlServer
  name: 'appointments'
  location: location
  sku: {
    name: 'GP_S_Gen5_1'
    tier: 'GeneralPurpose'
  }
  properties: {
    autoPauseDelay: 60 // minutes idle before pausing; a paused database costs only storage
    minCapacity: json('0.5')
  }
}

resource environment 'Microsoft.App/managedEnvironments@2024-03-01' = {
  name: '${appName}-env'
  location: location
  properties: {
    appLogsConfiguration: {
      destination: 'log-analytics'
      logAnalyticsConfiguration: {
        customerId: logs.properties.customerId
        sharedKey: logs.listKeys().primarySharedKey
      }
    }
  }
}

var connectionString = 'Server=tcp:${sqlServer.properties.fullyQualifiedDomainName},1433;Database=${database.name};User ID=${sqlAdminLogin};Password=${sqlAdminPassword};Encrypt=True;TrustServerCertificate=False;Connection Timeout=60;'

var appSecrets = [
  { name: 'sql-connection', value: connectionString }
]
var registrySecrets = useRegistryCredentials ? [ { name: 'registry-password', value: registryPassword } ] : []
var registries = useRegistryCredentials ? [ { server: 'ghcr.io', username: registryUsername, passwordSecretRef: 'registry-password' } ] : []

resource app 'Microsoft.App/containerApps@2024-03-01' = {
  name: appName
  location: location
  properties: {
    managedEnvironmentId: environment.id
    configuration: {
      ingress: {
        external: true
        targetPort: 8080
        transport: 'http' // MiniNet speaks HTTP/1.1; Azure terminates HTTPS in front of it
      }
      secrets: concat(appSecrets, registrySecrets)
      registries: registries
    }
    template: {
      containers: [
        {
          name: 'api'
          image: containerImage
          resources: {
            cpu: json('0.25')
            memory: '0.5Gi'
          }
          env: [
            { name: 'PORT', value: '8080' }
            { name: 'APPOINTMENTS_CONNECTION_STRING', secretRef: 'sql-connection' }
          ]
          probes: [
            {
              type: 'Liveness'
              httpGet: { path: '/health', port: 8080 }
              periodSeconds: 30
            }
          ]
        }
      ]
      scale: {
        minReplicas: 0 // scale to zero when idle
        maxReplicas: 2
      }
    }
  }
}

output url string = 'https://${app.properties.configuration.ingress.fqdn}'
output sqlServerName string = sqlServer.name
