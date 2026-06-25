
// ------------------------------------------------------------------------------------------------
// Session Two starter infrastructure
//   - Azure API Management (Standard V2) with system-assigned managed identity
//   - Azure AI Foundry (Cognitive Services AIServices account) + Foundry project
//   - gpt-4.1-mini model deployment
//   - Role assignment: APIM managed identity -> "Cognitive Services OpenAI User" on the Foundry account
//   - APIM "FoundryPortal" API (path /foundry) + foundry-backend + managed-identity routing policy
//
// NOTE: Unlike Session One, the APIM API, backend, and AI-gateway routing policy ARE included here
//       so Session Two starts with a working APIM -> Foundry gateway. Additional AI-gateway policies
//       (token limits, semantic caching, content safety, etc.) are layered on during the session.
// ------------------------------------------------------------------------------------------------

targetScope = 'resourceGroup'

@description('Azure region for all resources.')
param location string = resourceGroup().location

@description('A short prefix used to name resources (3-12 lowercase alphanumeric chars).')
@minLength(3)
@maxLength(12)
param namePrefix string = 'aigwlvlup'

@description('Publisher email for API Management. Override at deploy time with --parameters apimPublisherEmail=you@example.com')
param apimPublisherEmail string = 'admin@contoso.com'

@description('Publisher name for API Management.')
param apimPublisherName string = 'AI Gateway Level Up'

@description('Name of the model deployment.')
param modelDeploymentName string = 'gpt-4.1-mini'

@description('Model name to deploy.')
param modelName string = 'gpt-4.1-mini'

@description('Model version to deploy.')
param modelVersion string = '2025-04-14'

@description('Capacity (TPM in thousands) for the model deployment.')
param modelCapacity int = 10

@description('Name of the embedding model deployment.')
param embeddingDeploymentName string = 'text-embedding-ada-002'

@description('Embedding model name to deploy.')
param embeddingModelName string = 'text-embedding-ada-002'

@description('Embedding model version to deploy.')
param embeddingModelVersion string = '2'

@description('Capacity (TPM in thousands) for the embedding model deployment.')
param embeddingModelCapacity int = 10

@description('Object ID (principal) to grant "Cognitive Services OpenAI User" on the Foundry account, e.g. your user so you can test locally with DefaultAzureCredential. Leave empty to skip. Get yours with: az ad signed-in-user show --query id -o tsv')
param inferenceUserPrincipalId string = ''

@description('Principal type for inferenceUserPrincipalId.')
@allowed([
  'User'
  'Group'
  'ServicePrincipal'
])
param inferenceUserPrincipalType string = 'User'

// ---- Additional resources (Azure Managed Redis, Azure AI Content Safety, Application Insights) ----

@description('SKU for the Redis Enterprise cluster (e.g. Balanced_B0, MemoryOptimized_M10).')
param redisSkuName string = 'Balanced_B0'

@description('Azure region for the Redis Enterprise cluster (separate from `location` to work around capacity issues).')
param redisLocation string = location

@description('SKU for the Content Safety account.')
param contentSafetySkuName string = 'S0'

// Stable, unique-ish suffix for globally-scoped names.
var uniqueSuffix = uniqueString(resourceGroup().id)
var foundryAccountName = '${namePrefix}-foundry-${uniqueSuffix}'
var foundryProjectName = '${namePrefix}-project'
var apimServiceName = '${namePrefix}-apim-${uniqueSuffix}'
var contentSafetyName = '${namePrefix}-cs-${uniqueSuffix}'
var appInsightsName = '${namePrefix}-appinsights-${uniqueSuffix}'
var logAnalyticsWorkspaceName = '${namePrefix}-law-${uniqueSuffix}'
var redisName = '${namePrefix}-redis-${uniqueSuffix}'

// "Cognitive Services OpenAI User" built-in role.
var openAiUserRoleId = '5e0bd9bd-7b93-4f28-af87-19fc36ad61bd'

// ------------------------------------------------------------------------------------------------
// Azure AI Foundry account (Cognitive Services, kind = AIServices)
// ------------------------------------------------------------------------------------------------
resource foundry 'Microsoft.CognitiveServices/accounts@2025-04-01-preview' = {
  name: foundryAccountName
  location: location
  kind: 'AIServices'
  sku: {
    name: 'S0'
  }
  identity: {
    type: 'SystemAssigned'
  }
  properties: {
    // Custom subdomain is required for Entra ID (AAD) token authentication.
    customSubDomainName: foundryAccountName
    publicNetworkAccess: 'Enabled'
    // Allow Foundry projects on this account.
    allowProjectManagement: true
  }
}

// Foundry project (child of the account).
resource foundryProject 'Microsoft.CognitiveServices/accounts/projects@2025-04-01-preview' = {
  parent: foundry
  name: foundryProjectName
  location: location
  identity: {
    type: 'SystemAssigned'
  }
  properties: {
    displayName: foundryProjectName
    description: 'Session One Foundry project'
  }
}

// gpt-4.1-mini model deployment.
resource modelDeployment 'Microsoft.CognitiveServices/accounts/deployments@2025-04-01-preview' = {
  parent: foundry
  name: modelDeploymentName
  sku: {
    name: 'GlobalStandard'
    capacity: modelCapacity
  }
  properties: {
    model: {
      format: 'OpenAI'
      name: modelName
      version: modelVersion
    }
  }
}

// text-embedding-ada-002 model deployment.
resource embeddingDeployment 'Microsoft.CognitiveServices/accounts/deployments@2025-04-01-preview' = {
  parent: foundry
  name: embeddingDeploymentName
  sku: {
    name: 'Standard'
    capacity: embeddingModelCapacity
  }
  properties: {
    model: {
      format: 'OpenAI'
      name: embeddingModelName
      version: embeddingModelVersion
    }
  }
  dependsOn: [
    modelDeployment
  ]
}

// ------------------------------------------------------------------------------------------------
// API Management (Standard V2) with system-assigned managed identity
// ------------------------------------------------------------------------------------------------
resource apim 'Microsoft.ApiManagement/service@2024-05-01' = {
  name: apimServiceName
  location: location
  sku: {
    name: 'StandardV2'
    capacity: 1
  }
  identity: {
    type: 'SystemAssigned'
  }
  properties: {
    publisherEmail: apimPublisherEmail
    publisherName: apimPublisherName
  }
}

// ------------------------------------------------------------------------------------------------
// APIM backend -> Foundry (Cognitive Services data plane)
//   Referenced by the API policy via <set-backend-service backend-id="foundry-backend" />.
// ------------------------------------------------------------------------------------------------
resource foundryBackend 'Microsoft.ApiManagement/service/backends@2024-05-01' = {
  parent: apim
  name: 'foundry-backend'
  properties: {
    protocol: 'http'
    url: foundry.properties.endpoint
    tls: {
      validateCertificateChain: true
      validateCertificateName: true
    }
  }
}

// ------------------------------------------------------------------------------------------------
// APIM API: FoundryPortal
//   A subscription-key-protected API at path /foundry. Routing to the Foundry backend and
//   managed-identity auth are handled entirely by the API-level policy (serviceUrl is null).
//   A single catch-all POST operation (/*) forwards any Foundry data-plane path.
// ------------------------------------------------------------------------------------------------
resource foundryApi 'Microsoft.ApiManagement/service/apis@2024-05-01' = {
  parent: apim
  name: 'foundryportal'
  properties: {
    displayName: 'FoundryPortal'
    path: 'foundry'
    protocols: [
      'https'
    ]
    // No subscription key required on the FoundryPortal API — APIM authenticates to
    // Foundry with its managed identity (see the API policy), so clients call the
    // gateway without a key. (The chat app still has an optional api-key field for
    // other gateways, e.g. the Foundry-managed AI Gateway, that do require one.)
    subscriptionRequired: false
  }
}

resource foundryApiPostOperation 'Microsoft.ApiManagement/service/apis/operations@2024-05-01' = {
  parent: foundryApi
  name: 'postfoundry'
  properties: {
    displayName: 'PostFoundry'
    method: 'POST'
    urlTemplate: '/*'
    request: {
      queryParameters: [
        {
          name: 'api-version'
          type: 'string'
          values: [
            '2024-10-21'
          ]
        }
      ]
      headers: [
        {
          name: 'Content-Type'
          type: 'string'
          values: [
            'application/json'
          ]
        }
      ]
    }
    responses: [
      {
        statusCode: 200
      }
    ]
  }
}

// API-level policy applied across all operations: routes to foundry-backend and injects a
// managed-identity bearer token for the Cognitive Services data plane.
resource foundryApiPolicy 'Microsoft.ApiManagement/service/apis/policies@2024-05-01' = {
  parent: foundryApi
  name: 'policy'
  properties: {
    format: 'rawxml'
    value: loadTextContent('policies/foundryportal.xml')
  }
  dependsOn: [
    foundryBackend
    foundryApiPostOperation
  ]
}

// ------------------------------------------------------------------------------------------------
// Role assignment: APIM managed identity -> Cognitive Services OpenAI User on the Foundry account
// (Used during the live demo when APIM routes to the model using its managed identity.)
// ------------------------------------------------------------------------------------------------
resource apimFoundryRoleAssignment 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  scope: foundry
  name: guid(foundry.id, apim.id, openAiUserRoleId)
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', openAiUserRoleId)
    principalId: apim.identity.principalId
    principalType: 'ServicePrincipal'
  }
}

// Optional role assignment: a user/group/SP you pass in -> Cognitive Services OpenAI User
// on the Foundry account, so you can test the chat app locally with DefaultAzureCredential.
resource userFoundryRoleAssignment 'Microsoft.Authorization/roleAssignments@2022-04-01' = if (!empty(inferenceUserPrincipalId)) {
  scope: foundry
  name: guid(foundry.id, inferenceUserPrincipalId, openAiUserRoleId)
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', openAiUserRoleId)
    principalId: inferenceUserPrincipalId
    principalType: inferenceUserPrincipalType
  }
}

// ------------------------------------------------------------------------------------------------
// Additional resources (additive — independent of APIM / Foundry above):
//   - Log Analytics workspace + workspace-based Application Insights
//   - Azure AI Content Safety (Cognitive Services account, kind = ContentSafety)
//   - Azure Managed Redis (Redis Enterprise cluster + default DB with RediSearch)
//
// Modeled after the reference resources in subscription c6a8ee28-19ad-41b6-a129-4a6e1c15ef34
// / RG apim-aoairg: apimaoaiappinsights, apimcs, apimredis.
// ------------------------------------------------------------------------------------------------
resource logAnalytics 'Microsoft.OperationalInsights/workspaces@2023-09-01' = {
  name: logAnalyticsWorkspaceName
  location: location
  properties: {
    sku: {
      name: 'PerGB2018'
    }
    retentionInDays: 30
  }
}

resource appInsights 'Microsoft.Insights/components@2020-02-02' = {
  name: appInsightsName
  location: location
  kind: 'web'
  properties: {
    Application_Type: 'web'
    WorkspaceResourceId: logAnalytics.id
    publicNetworkAccessForIngestion: 'Enabled'
    publicNetworkAccessForQuery: 'Enabled'
  }
}

resource contentSafety 'Microsoft.CognitiveServices/accounts@2025-04-01-preview' = {
  name: contentSafetyName
  location: location
  kind: 'ContentSafety'
  sku: {
    name: contentSafetySkuName
  }
  identity: {
    type: 'SystemAssigned'
  }
  properties: {
    customSubDomainName: contentSafetyName
    publicNetworkAccess: 'Enabled'
  }
}

resource redis 'Microsoft.Cache/redisEnterprise@2024-10-01' = {
  name: redisName
  location: redisLocation
  sku: {
    name: redisSkuName
  }
}

resource redisDatabase 'Microsoft.Cache/redisEnterprise/databases@2024-10-01' = {
  parent: redis
  name: 'default'
  properties: {
    clientProtocol: 'Encrypted'
    port: 10000
    clusteringPolicy: 'EnterpriseCluster'
    evictionPolicy: 'NoEviction'
    accessKeysAuthentication: 'Enabled'
    persistence: {
      aofEnabled: false
      rdbEnabled: false
    }
    modules: [
      {
        name: 'RediSearch'
      }
    ]
  }
}

// ------------------------------------------------------------------------------------------------
// Outputs
// ------------------------------------------------------------------------------------------------
@description('Foundry account endpoint (use this directly, or swap to the APIM URL in the app).')
output foundryEndpoint string = foundry.properties.endpoint

@description('Foundry account name.')
output foundryAccountName string = foundry.name

@description('Foundry project name.')
output foundryProjectName string = foundryProject.name

@description('Deployed model deployment name.')
output modelDeploymentName string = modelDeployment.name

@description('Deployed embedding model deployment name.')
output embeddingDeploymentName string = embeddingDeployment.name

@description('API Management service name.')
output apimName string = apim.name

@description('API Management gateway URL (base for the APIM endpoint you will configure live).')
output apimGatewayUrl string = apim.properties.gatewayUrl

@description('FoundryPortal API endpoint on APIM (point the chat app here, with a subscription key).')
output foundryApiUrl string = '${apim.properties.gatewayUrl}/${foundryApi.properties.path}'

@description('Azure Managed Redis (Redis Enterprise) cluster name.')
output redisName string = redis.name

@description('Azure Managed Redis host name.')
output redisHostName string = redis.properties.hostName

@description('Azure AI Content Safety account name.')
output contentSafetyName string = contentSafety.name

@description('Azure AI Content Safety endpoint.')
output contentSafetyEndpoint string = contentSafety.properties.endpoint

@description('Application Insights component name.')
output appInsightsName string = appInsights.name

@description('Application Insights connection string.')
output appInsightsConnectionString string = appInsights.properties.ConnectionString

@description('Log Analytics workspace resource ID backing Application Insights.')
output logAnalyticsWorkspaceId string = logAnalytics.id
