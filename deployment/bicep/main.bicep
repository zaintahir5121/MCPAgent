// Avantra AI Advisory Platform - Azure infrastructure (single resource group).
// az deployment group create -g rg-advisory-test -f deployment/bicep/main.bicep -p environmentName=test entraTenantId=<tid>
//
// Provisions: Log Analytics + App Insights, ACR, Key Vault, Storage, Azure SQL (Entra-only auth),
// Azure AI Search, user-assigned managed identity with least-privilege role assignments,
// Container Apps environment with api + web apps and a scheduled ingestion job.
// Networking is public-with-identity for simplicity; production adds a VNet, private endpoints and
// an internal Container Apps environment behind Front Door/APIM (see docs/02 section 4.7).

targetScope = 'resourceGroup'

@description('Short environment name, e.g. dev, test, prod.')
param environmentName string = 'test'

param location string = resourceGroup().location

@description('Entra tenant ID used by the API to validate tokens.')
param entraTenantId string

@description('Application ID URI / audience of the API app registration.')
param apiAudience string = 'api://avantra-ai-advisory'

@description('Entra object ID and display name of the SQL administrator group.')
param sqlAdminGroupObjectId string = ''
param sqlAdminGroupName string = 'advisory-sql-admins'

@description('OpenAI-compatible endpoint (e.g. https://<aoai>.openai.azure.com/openai/v1/). Key is stored in Key Vault as AI--ApiKey.')
param aiEndpoint string = ''
param aiChatModel string = ''
param aiEmbeddingModel string = ''

@description('Container image tags (pushed to the ACR created here).')
param apiImage string = 'advisory-api:latest'
param webImage string = 'advisory-web:latest'

@allowed([ 'basic', 'standard' ])
param searchSku string = 'basic'

var suffix = uniqueString(resourceGroup().id, environmentName)
var tags = { application: 'avantra-ai-advisory', environment: environmentName }

// ---------------------------------------------------------------- Observability
resource logs 'Microsoft.OperationalInsights/workspaces@2023-09-01' = {
  name: 'log-advisory-${environmentName}-${suffix}'
  location: location
  tags: tags
  properties: { sku: { name: 'PerGB2018' }, retentionInDays: 30 }
}

resource appInsights 'Microsoft.Insights/components@2020-02-02' = {
  name: 'appi-advisory-${environmentName}-${suffix}'
  location: location
  kind: 'web'
  tags: tags
  properties: { Application_Type: 'web', WorkspaceResourceId: logs.id }
}

// ---------------------------------------------------------------- Identity
resource identity 'Microsoft.ManagedIdentity/userAssignedIdentities@2023-01-31' = {
  name: 'id-advisory-${environmentName}-${suffix}'
  location: location
  tags: tags
}

// ---------------------------------------------------------------- Registry, secrets, storage
resource acr 'Microsoft.ContainerRegistry/registries@2023-07-01' = {
  name: 'acradvisory${suffix}'
  location: location
  tags: tags
  sku: { name: 'Basic' }
  properties: { adminUserEnabled: false }
}

resource keyVault 'Microsoft.KeyVault/vaults@2023-07-01' = {
  name: 'kv-adv-${take(suffix, 10)}-${take(environmentName, 4)}'
  location: location
  tags: tags
  properties: {
    tenantId: subscription().tenantId
    sku: { family: 'A', name: 'standard' }
    enableRbacAuthorization: true
    enableSoftDelete: true
    enablePurgeProtection: true
  }
}

resource storage 'Microsoft.Storage/storageAccounts@2023-05-01' = {
  name: 'stadvisory${suffix}'
  location: location
  tags: tags
  kind: 'StorageV2'
  sku: { name: 'Standard_ZRS' }
  properties: {
    minimumTlsVersion: 'TLS1_2'
    allowBlobPublicAccess: false
    allowSharedKeyAccess: false
    supportsHttpsTrafficOnly: true
  }
}

resource blobService 'Microsoft.Storage/storageAccounts/blobServices@2023-05-01' = {
  parent: storage
  name: 'default'
  properties: { isVersioningEnabled: true, deleteRetentionPolicy: { enabled: true, days: 30 } }
}

resource documentsContainer 'Microsoft.Storage/storageAccounts/blobServices/containers@2023-05-01' = {
  parent: blobService
  name: 'knowledge-documents'
  properties: { publicAccess: 'None' }
}

// ---------------------------------------------------------------- Data
resource sqlServer 'Microsoft.Sql/servers@2023-08-01-preview' = {
  name: 'sql-advisory-${environmentName}-${suffix}'
  location: location
  tags: tags
  properties: {
    minimalTlsVersion: '1.2'
    publicNetworkAccess: 'Enabled'
    administrators: empty(sqlAdminGroupObjectId) ? null : {
      administratorType: 'ActiveDirectory'
      azureADOnlyAuthentication: true
      login: sqlAdminGroupName
      sid: sqlAdminGroupObjectId
      tenantId: subscription().tenantId
      principalType: 'Group'
    }
  }
}

resource sqlAllowAzure 'Microsoft.Sql/servers/firewallRules@2023-08-01-preview' = {
  parent: sqlServer
  name: 'AllowAzureServices'
  properties: { startIpAddress: '0.0.0.0', endIpAddress: '0.0.0.0' }
}

resource sqlDb 'Microsoft.Sql/servers/databases@2023-08-01-preview' = {
  parent: sqlServer
  name: 'advisory'
  location: location
  tags: tags
  sku: { name: 'GP_S_Gen5_2', tier: 'GeneralPurpose' }
  properties: { autoPauseDelay: environmentName == 'prod' ? -1 : 60, minCapacity: json('0.5'), zoneRedundant: false }
}

resource search 'Microsoft.Search/searchServices@2023-11-01' = {
  name: 'srch-advisory-${environmentName}-${suffix}'
  location: location
  tags: tags
  sku: { name: searchSku }
  properties: {
    replicaCount: 1
    partitionCount: 1
    semanticSearch: 'standard'
    authOptions: { aadOrApiKey: { aadAuthFailureMode: 'http401WithBearerChallenge' } }
  }
}

// ---------------------------------------------------------------- Role assignments (least privilege)
var roles = {
  acrPull: '7f951dda-4ed3-4680-a7ca-43fe172d538d'
  keyVaultSecretsUser: '4633458b-17de-408a-b874-0445c86b69e6'
  storageBlobDataContributor: 'ba92f5b4-2d11-453d-a403-e96b0029c9fe'
  searchIndexDataContributor: '8ebe5a00-799e-43f5-93ac-243d3dce84a7'
  searchServiceContributor: '7ca78c08-252a-4471-8644-bb5ff32d4ba0'
}

resource acrPull 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  scope: acr
  name: guid(acr.id, identity.id, roles.acrPull)
  properties: { principalId: identity.properties.principalId, principalType: 'ServicePrincipal', roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', roles.acrPull) }
}

resource kvSecrets 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  scope: keyVault
  name: guid(keyVault.id, identity.id, roles.keyVaultSecretsUser)
  properties: { principalId: identity.properties.principalId, principalType: 'ServicePrincipal', roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', roles.keyVaultSecretsUser) }
}

resource blobContributor 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  scope: storage
  name: guid(storage.id, identity.id, roles.storageBlobDataContributor)
  properties: { principalId: identity.properties.principalId, principalType: 'ServicePrincipal', roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', roles.storageBlobDataContributor) }
}

resource searchData 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  scope: search
  name: guid(search.id, identity.id, roles.searchIndexDataContributor)
  properties: { principalId: identity.properties.principalId, principalType: 'ServicePrincipal', roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', roles.searchIndexDataContributor) }
}

resource searchSvc 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  scope: search
  name: guid(search.id, identity.id, roles.searchServiceContributor)
  properties: { principalId: identity.properties.principalId, principalType: 'ServicePrincipal', roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', roles.searchServiceContributor) }
}
// SQL: grant the identity db access with T-SQL after deployment (CREATE USER [<identity name>] FROM EXTERNAL PROVIDER; ALTER ROLE db_datareader/db_datawriter/db_ddladmin ADD MEMBER ...).

// ---------------------------------------------------------------- Container Apps
resource caEnv 'Microsoft.App/managedEnvironments@2024-03-01' = {
  name: 'cae-advisory-${environmentName}-${suffix}'
  location: location
  tags: tags
  properties: {
    appLogsConfiguration: {
      destination: 'log-analytics'
      logAnalyticsConfiguration: { customerId: logs.properties.customerId, sharedKey: logs.listKeys().primarySharedKey }
    }
  }
}

var sqlConnection = 'Server=tcp:${sqlServer.properties.fullyQualifiedDomainName},1433;Database=advisory;Authentication=Active Directory Managed Identity;User Id=${identity.properties.clientId};Encrypt=True;'

var apiEnv = [
  { name: 'ASPNETCORE_ENVIRONMENT', value: environmentName == 'prod' ? 'Production' : 'Staging' }
  { name: 'APPLICATIONINSIGHTS_CONNECTION_STRING', value: appInsights.properties.ConnectionString }
  { name: 'AZURE_CLIENT_ID', value: identity.properties.clientId }
  { name: 'KeyVault__Uri', value: keyVault.properties.vaultUri }
  { name: 'Database__Provider', value: 'SqlServer' }
  { name: 'ConnectionStrings__Advisory', value: sqlConnection }
  { name: 'Knowledge__IndexProvider', value: 'AzureAiSearch' }
  { name: 'Knowledge__SeedDocumentsOnStartup', value: 'false' }
  { name: 'Knowledge__AzureAiSearch__Endpoint', value: 'https://${search.name}.search.windows.net' }
  { name: 'Auth__Mode', value: 'EntraId' }
  { name: 'Auth__EntraId__TenantId', value: entraTenantId }
  { name: 'Auth__EntraId__Audience', value: apiAudience }
  { name: 'AI__Endpoint', value: aiEndpoint }
  { name: 'AI__ChatModel', value: aiChatModel }
  { name: 'AI__EmbeddingModel', value: aiEmbeddingModel }
]

resource apiApp 'Microsoft.App/containerApps@2024-03-01' = {
  name: 'ca-advisory-api-${environmentName}'
  location: location
  tags: tags
  identity: { type: 'UserAssigned', userAssignedIdentities: { '${identity.id}': {} } }
  dependsOn: [ acrPull ]
  properties: {
    managedEnvironmentId: caEnv.id
    configuration: {
      ingress: { external: true, targetPort: 8080, transport: 'auto', allowInsecure: false }
      registries: [ { server: acr.properties.loginServer, identity: identity.id } ]
      activeRevisionsMode: 'Multiple'
    }
    template: {
      containers: [
        {
          name: 'api'
          image: '${acr.properties.loginServer}/${apiImage}'
          resources: { cpu: json('1.0'), memory: '2Gi' }
          env: apiEnv
          probes: [
            { type: 'Liveness', httpGet: { path: '/health', port: 8080 }, periodSeconds: 30 }
            { type: 'Readiness', httpGet: { path: '/health', port: 8080 }, periodSeconds: 10 }
          ]
        }
      ]
      scale: {
        minReplicas: environmentName == 'prod' ? 1 : 0
        maxReplicas: 5
        rules: [ { name: 'http', http: { metadata: { concurrentRequests: '50' } } } ]
      }
    }
  }
}

resource webApp 'Microsoft.App/containerApps@2024-03-01' = {
  name: 'ca-advisory-web-${environmentName}'
  location: location
  tags: tags
  identity: { type: 'UserAssigned', userAssignedIdentities: { '${identity.id}': {} } }
  dependsOn: [ acrPull ]
  properties: {
    managedEnvironmentId: caEnv.id
    configuration: {
      ingress: { external: true, targetPort: 8080, transport: 'auto', stickySessions: { affinity: 'sticky' } }
      registries: [ { server: acr.properties.loginServer, identity: identity.id } ]
    }
    template: {
      containers: [
        {
          name: 'web'
          image: '${acr.properties.loginServer}/${webImage}'
          resources: { cpu: json('0.5'), memory: '1Gi' }
          env: [
            { name: 'AdvisoryApi__BaseUrl', value: 'https://${apiApp.properties.configuration.ingress.fqdn}/' }
            { name: 'APPLICATIONINSIGHTS_CONNECTION_STRING', value: appInsights.properties.ConnectionString }
          ]
        }
      ]
      scale: { minReplicas: 0, maxReplicas: 3 }
    }
  }
}

// Ingestion job: same image in '--ingest-only' mode (seeds/refreshes vendor pack documents into SQL + Azure AI Search, then exits).
resource ingestionJob 'Microsoft.App/jobs@2024-03-01' = {
  name: 'caj-advisory-ingest-${environmentName}'
  location: location
  tags: tags
  identity: { type: 'UserAssigned', userAssignedIdentities: { '${identity.id}': {} } }
  dependsOn: [ acrPull ]
  properties: {
    environmentId: caEnv.id
    configuration: {
      triggerType: 'Schedule'
      replicaTimeout: 3600
      scheduleTriggerConfig: { cronExpression: '17 2 * * *', parallelism: 1, replicaCompletionCount: 1 }
      registries: [ { server: acr.properties.loginServer, identity: identity.id } ]
    }
    template: {
      containers: [
        {
          name: 'ingest'
          image: '${acr.properties.loginServer}/${apiImage}'
          args: [ '--ingest-only' ]
          resources: { cpu: json('1.0'), memory: '2Gi' }
          env: concat(filter(apiEnv, e => e.name != 'Knowledge__SeedDocumentsOnStartup'), [ { name: 'Knowledge__SeedDocumentsOnStartup', value: 'true' } ])
        }
      ]
    }
  }
}

output apiUrl string = 'https://${apiApp.properties.configuration.ingress.fqdn}'
output webUrl string = 'https://${webApp.properties.configuration.ingress.fqdn}'
output mcpEndpoint string = 'https://${apiApp.properties.configuration.ingress.fqdn}/mcp'
output keyVaultName string = keyVault.name
output managedIdentityClientId string = identity.properties.clientId
output acrLoginServer string = acr.properties.loginServer
