// ------------------------------------------------------------------------------------------------
// Session One starter infrastructure
//   - Azure API Management (Standard V2) with system-assigned managed identity
//   - Azure AI Foundry (Cognitive Services AIServices account) + Foundry project
//   - gpt-4.1-mini model deployment
//   - Role assignment: APIM managed identity -> "Cognitive Services OpenAI User" on the Foundry account
//
// NOTE: The APIM API definition and AI-gateway policies are intentionally NOT included here.
//       Those are configured live during the session demo.
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

// Stable, unique-ish suffix for globally-scoped names.
var uniqueSuffix = uniqueString(resourceGroup().id)
var foundryAccountName = '${namePrefix}-foundry-${uniqueSuffix}'
var foundryProjectName = '${namePrefix}-project'
var apimServiceName = '${namePrefix}-apim-${uniqueSuffix}'

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

@description('API Management service name.')
output apimName string = apim.name

@description('API Management gateway URL (base for the APIM endpoint you will configure live).')
output apimGatewayUrl string = apim.properties.gatewayUrl
