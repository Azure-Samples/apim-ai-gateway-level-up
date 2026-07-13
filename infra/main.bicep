
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
// "Cognitive Services User" built-in role (used for the Content Safety data plane).
var cognitiveServicesUserRoleId = 'a97b65f3-24c7-4388-baec-2e87135dc908'

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

// Additional role assignment: Cognitive Services User on the Foundry account.
// Required for the Foundry AI Inference API (paths under /models/*, e.g. /models/chat/completions,
// /models/embeddings) — the "OpenAI User" role only covers the classic /openai/deployments/* paths.
resource apimFoundryInferenceRoleAssignment 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  scope: foundry
  name: guid(foundry.id, apim.id, cognitiveServicesUserRoleId)
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', cognitiveServicesUserRoleId)
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
// AI-gateway backends and external cache used by the aigwlvlup-foundry API policy
//   (llm-emit-token-metric, llm-token-limit, llm-semantic-cache-lookup/store, llm-content-safety)
// ------------------------------------------------------------------------------------------------

// Foundry AI endpoint backend (services.ai.azure.com root). Authenticates to
// Cognitive Services with APIM's system-assigned managed identity.
resource foundryAiEndpointBackend 'Microsoft.ApiManagement/service/backends@2024-05-01' = {
  parent: apim
  name: 'aigwlvlup-foundry-ai-endpoint'
  properties: {
    protocol: 'http'
    url: foundry.properties.endpoint
    resourceId: '${environment().resourceManager}${foundry.id}'
    tls: {
      validateCertificateChain: true
      validateCertificateName: true
    }
    credentials: {
      managedIdentity: {
        resource: 'https://cognitiveservices.azure.com/'
      }
    }
  }
}

// Embedding backend used by llm-semantic-cache-lookup (embeddings-backend-auth="system-assigned"
// on the policy handles the MI token, so no credentials are set on the backend itself).
resource embeddingBackend 'Microsoft.ApiManagement/service/backends@2024-05-01' = {
  parent: apim
  name: 'embedding-backend'
  properties: {
    description: 'Embedding model'
    protocol: 'http'
    url: '${foundry.properties.endpoint}openai/deployments/${embeddingDeployment.name}/embeddings'
    tls: {
      validateCertificateChain: true
      validateCertificateName: true
    }
  }
}

// Content Safety backend used by llm-content-safety. Authenticates with APIM's MI.
resource contentSafetyBackend 'Microsoft.ApiManagement/service/backends@2024-05-01' = {
  parent: apim
  name: 'content-safety-backend'
  properties: {
    protocol: 'http'
    url: contentSafety.properties.endpoint
    tls: {
      validateCertificateChain: true
      validateCertificateName: true
    }
    credentials: {
      managedIdentity: {
        resource: 'https://cognitiveservices.azure.com'
      }
    }
  }
}

// External cache — Azure Managed Redis, used by llm-semantic-cache-lookup/store.
// The connection string embeds the Redis primary access key retrieved via listKeys().
resource apimExternalCache 'Microsoft.ApiManagement/service/caches@2024-05-01' = {
  parent: apim
  name: 'default'
  properties: {
    description: 'AI Gateway external cache'
    useFromLocation: 'default'
    connectionString: '${redis.properties.hostName}:10000,password=${redisDatabase.listKeys().primaryKey},ssl=True,abortConnect=False'
  }
}

// ------------------------------------------------------------------------------------------------
// APIM API: aigwlvlup-foundry (path /foundrygateway)
//   Mirrors the portal-imported Foundry API. Subscription-key required. All AI-gateway policies
//   (token limit, emit-token-metric, semantic cache, content safety) are applied via the policy
//   XML file loaded below.
// ------------------------------------------------------------------------------------------------
resource aigwFoundryApi 'Microsoft.ApiManagement/service/apis@2024-05-01' = {
  parent: apim
  name: 'aigwlvlup-foundry'
  properties: {
    displayName: 'aigwlvlup-foundry'
    path: 'foundrygateway'
    protocols: [
      'https'
    ]
    subscriptionRequired: true
    subscriptionKeyParameterNames: {
      header: 'api-key'
      query: 'subscription-key'
    }
    // Import all operations from the Foundry OpenAPI spec (chat completions, embeddings,
    // image generations, image embeddings, model info, Anthropic messages).
    format: 'openapi'
    value: loadTextContent('policies/aigwlvlup-foundry.openapi.yaml')
  }
}

resource aigwFoundryApiPolicy 'Microsoft.ApiManagement/service/apis/policies@2024-05-01' = {
  parent: aigwFoundryApi
  name: 'policy'
  properties: {
    format: 'rawxml'
    value: loadTextContent('policies/aigwlvlup-foundry.xml')
  }
  dependsOn: [
    foundryAiEndpointBackend
    embeddingBackend
    contentSafetyBackend
    apimExternalCache
  ]
}

// Role assignment: APIM MI -> Cognitive Services User on the Content Safety account
// (required so llm-content-safety can call the Content Safety data plane with MI).
resource apimContentSafetyRoleAssignment 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  scope: contentSafety
  name: guid(contentSafety.id, apim.id, cognitiveServicesUserRoleId)
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', cognitiveServicesUserRoleId)
    principalId: apim.identity.principalId
    principalType: 'ServicePrincipal'
  }
}

// ------------------------------------------------------------------------------------------------
// Application Insights integration for APIM
//   - Logger: wires APIM to the App Insights component (required by llm-emit-token-metric so
//     custom token metrics land in App Insights).
//   - Diagnostic: emits gateway request/response telemetry for the aigwlvlup-foundry API.
// ------------------------------------------------------------------------------------------------
resource apimAppInsightsLogger 'Microsoft.ApiManagement/service/loggers@2024-05-01' = {
  parent: apim
  name: appInsights.name
  properties: {
    loggerType: 'applicationInsights'
    description: 'Application Insights logger for AI-gateway metrics'
    resourceId: appInsights.id
    credentials: {
      instrumentationKey: appInsights.properties.InstrumentationKey
    }
  }
}

resource aigwFoundryApiDiagnostic 'Microsoft.ApiManagement/service/apis/diagnostics@2024-05-01' = {
  parent: aigwFoundryApi
  name: 'applicationinsights'
  properties: {
    loggerId: apimAppInsightsLogger.id
    alwaysLog: 'allErrors'
    sampling: {
      samplingType: 'fixed'
      percentage: 100
    }
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
