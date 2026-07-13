// ------------------------------------------------------------------------------------------------
// MCP APIM APIs module ("expose-as-MCP" pattern)
//   Adds the MCP surface to the EXISTING APIM instance (created in main.bicep). It wires up:
//     - named values used by the OBO / PRM policies
//     - a backend + REST API imported from the Function App OpenAPI (Echo, GetMe)
//     - an OBO token-exchange policy on the GetMe operation
//     - an OAuth Protected Resource Metadata (PRM, RFC 9728) endpoint
//     - an MCP-type API that exposes Echo + GetMe as MCP tools, JWT-validated
//   The Learn MCP passthrough pattern is intentionally NOT here — it targets an external server
//   and is configured live in the portal (see hol/walkthrough.md).
// ------------------------------------------------------------------------------------------------

@description('Name of the existing APIM instance.')
param apimName string

@description('Default hostname of the MCP Function App.')
param functionAppDefaultHostname string

@description('Microsoft Entra tenant ID used for token validation and OBO exchange.')
param entraIdTenantId string

@description('Client ID of the middle-tier (backend API) app registration used for OBO.')
param oboClientId string

@secure()
@description('Client secret of the middle-tier app registration used for OBO.')
param oboClientSecret string

@description('Audience to validate on inbound tokens — the Application ID URI of the backend API app (e.g. api://<obo-client-id>).')
param mcpClientAudience string

@description('Route prefix (base path) the MCP server is served under.')
param mcpApiPath string = 'obo-mcp-server'

resource apim 'Microsoft.ApiManagement/service@2024-06-01-preview' existing = {
  name: apimName
}

// ──────────────────────────────────────────────────────
// Named values used by the OBO + PRM + MCP policies
// ──────────────────────────────────────────────────────
resource namedValueTenantId 'Microsoft.ApiManagement/service/namedValues@2024-06-01-preview' = {
  parent: apim
  name: 'entraid-tenant'
  properties: {
    displayName: 'entraid-tenant'
    value: entraIdTenantId
    secret: false
  }
}

resource namedValueClientId 'Microsoft.ApiManagement/service/namedValues@2024-06-01-preview' = {
  parent: apim
  name: 'obo-client-id'
  properties: {
    displayName: 'obo-client-id'
    value: oboClientId
    secret: false
  }
}

resource namedValueClientSecret 'Microsoft.ApiManagement/service/namedValues@2024-06-01-preview' = {
  parent: apim
  name: 'obo-client-secret'
  properties: {
    displayName: 'obo-client-secret'
    value: oboClientSecret
    secret: true
  }
}

resource namedValueMcpClientAudience 'Microsoft.ApiManagement/service/namedValues@2024-06-01-preview' = {
  parent: apim
  name: 'mcp-client-audience'
  properties: {
    displayName: 'mcp-client-audience'
    value: mcpClientAudience
    secret: false
  }
}

resource apimGatewayUrlNamedValue 'Microsoft.ApiManagement/service/namedValues@2024-06-01-preview' = {
  parent: apim
  name: 'APIMGatewayURL'
  properties: {
    displayName: 'APIMGatewayURL'
    value: apim.properties.gatewayUrl
    secret: false
  }
}

resource mcpApiPathNamedValue 'Microsoft.ApiManagement/service/namedValues@2024-06-01-preview' = {
  parent: apim
  name: 'McpApiPath'
  properties: {
    displayName: 'McpApiPath'
    value: mcpApiPath
    secret: false
  }
}

// ──────────────────────────────────────────────────────
// Backend: Function App
// ──────────────────────────────────────────────────────
resource functionAppBackend 'Microsoft.ApiManagement/service/backends@2024-06-01-preview' = {
  parent: apim
  name: 'mcp-function-app-backend'
  properties: {
    protocol: 'http'
    url: 'https://${functionAppDefaultHostname}/api'
    tls: {
      validateCertificateChain: true
      validateCertificateName: true
    }
    type: 'Single'
  }
}

// ──────────────────────────────────────────────────────
// REST API imported from the Function App OpenAPI (Echo + GetMe)
// ──────────────────────────────────────────────────────
resource functionAppApi 'Microsoft.ApiManagement/service/apis@2024-06-01-preview' = {
  parent: apim
  name: 'mcp-function-app-api'
  properties: {
    displayName: 'MCP Function App API'
    description: 'REST API representation of the MCP Function App with Echo and GetMe endpoints.'
    path: 'mcp-function-app'
    protocols: [
      'https'
    ]
    subscriptionRequired: false
    format: 'openapi+json'
    value: loadTextContent('../../src/mcp-functionapp/openapi.json')
    serviceUrl: 'https://${functionAppDefaultHostname}/api'
  }
}

resource functionAppApiPolicy 'Microsoft.ApiManagement/service/apis/policies@2024-06-01-preview' = {
  parent: functionAppApi
  name: 'policy'
  properties: {
    format: 'rawxml'
    value: '<policies><inbound><base /><set-backend-service backend-id="${functionAppBackend.name}" /></inbound><backend><base /></backend><outbound><base /></outbound><on-error><base /></on-error></policies>'
  }
}

resource echoOperation 'Microsoft.ApiManagement/service/apis/operations@2024-06-01-preview' existing = {
  parent: functionAppApi
  name: 'echo'
}

resource getMeOperation 'Microsoft.ApiManagement/service/apis/operations@2024-06-01-preview' existing = {
  parent: functionAppApi
  name: 'getMe'
}

// OBO token-exchange policy on the GetMe operation.
resource getMeOperationPolicy 'Microsoft.ApiManagement/service/apis/operations/policies@2024-06-01-preview' = {
  parent: getMeOperation
  name: 'policy'
  properties: {
    format: 'rawxml'
    value: loadTextContent('../policies/obo-getme-policy.xml')
  }
  dependsOn: [
    namedValueTenantId
    namedValueClientId
    namedValueClientSecret
  ]
}

// ──────────────────────────────────────────────────────
// MCP Auth — Protected Resource Metadata (RFC 9728)
// ──────────────────────────────────────────────────────
resource mcpAuthApi 'Microsoft.ApiManagement/service/apis@2024-06-01-preview' = {
  parent: apim
  name: 'mcp-auth'
  properties: {
    displayName: 'MCP Auth'
    description: 'Protected Resource Metadata endpoint for MCP OAuth2 discovery (RFC 9728).'
    path: ''
    protocols: [
      'https'
    ]
    subscriptionRequired: false
  }
}

resource mcpAuthOperation 'Microsoft.ApiManagement/service/apis/operations@2024-06-01-preview' = {
  parent: mcpAuthApi
  name: 'get-prm'
  properties: {
    displayName: 'Get Protected Resource Metadata'
    method: 'GET'
    urlTemplate: '/.well-known/oauth-protected-resource/{*mcpPath}'
    description: 'Returns the OAuth 2.0 Protected Resource Metadata (RFC 9728) for any MCP server.'
    templateParameters: [
      {
        name: 'mcpPath'
        description: 'Path of the MCP server (e.g. obo-mcp-server/mcp).'
        type: 'string'
        required: true
      }
    ]
    responses: [
      {
        statusCode: 200
        description: 'Protected Resource Metadata JSON'
        representations: [
          {
            contentType: 'application/json'
          }
        ]
      }
    ]
  }
}

resource mcpAuthOperationPolicy 'Microsoft.ApiManagement/service/apis/operations/policies@2024-06-01-preview' = {
  parent: mcpAuthOperation
  name: 'policy'
  properties: {
    format: 'rawxml'
    value: loadTextContent('../policies/mcp-prm-policy.xml')
  }
  dependsOn: [
    apimGatewayUrlNamedValue
    mcpApiPathNamedValue
    namedValueTenantId
    namedValueClientId
  ]
}

// ──────────────────────────────────────────────────────
// MCP Server — type 'mcp', exposes Echo + GetMe as tools
// ──────────────────────────────────────────────────────
resource mcpServerApi 'Microsoft.ApiManagement/service/apis@2024-06-01-preview' = {
  parent: apim
  name: 'obo-mcp-server'
  properties: {
    displayName: 'OBO MCP Server'
    description: 'MCP server that exposes the MCP Function App REST API operations as tools.'
    type: 'mcp'
    subscriptionRequired: false
    path: mcpApiPath
    protocols: [
      'https'
    ]
    mcpTools: [
      {
        name: 'echo'
        description: 'Echo a parameter back.'
        operationId: echoOperation.id
      }
      {
        name: 'getMe'
        description: 'Get the current user profile from Microsoft Graph via OBO token exchange.'
        operationId: getMeOperation.id
      }
    ]
    authenticationSettings: {
      oAuth2AuthenticationSettings: []
      openidAuthenticationSettings: []
    }
    isCurrent: true
  }
  dependsOn: [
    functionAppApi
  ]
}

// MCP Server API-level policy: validate Entra token + return 401 with PRM link on failure.
resource mcpServerApiPolicy 'Microsoft.ApiManagement/service/apis/policies@2024-06-01-preview' = {
  parent: mcpServerApi
  name: 'policy'
  properties: {
    format: 'rawxml'
    value: loadTextContent('../policies/mcp-api-policy.xml')
  }
  dependsOn: [
    apimGatewayUrlNamedValue
    mcpApiPathNamedValue
    namedValueTenantId
    namedValueMcpClientAudience
  ]
}
