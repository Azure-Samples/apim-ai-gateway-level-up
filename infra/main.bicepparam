using './main.bicep'

// Non-sensitive defaults. apimPublisherEmail is intentionally NOT set here —
// pass it on the command line so personal admin emails are never committed:
//   az deployment group create ... --parameters apimPublisherEmail=you@example.com
param location = 'eastus2'
param apimPublisherName = 'AI Gateway Level Up'
param namePrefix = 'aigwlvlup'
param modelDeploymentName = 'gpt-4.1-mini'
param modelName = 'gpt-4.1-mini'
param modelVersion = '2025-04-14'
param modelCapacity = 10

param embeddingDeploymentName = 'text-embedding-ada-002'
param embeddingModelName = 'text-embedding-ada-002'
param embeddingModelVersion = '2'
param embeddingModelCapacity = 10

// Additional resources (Azure Managed Redis, Content Safety, Application Insights).
// APIM, Foundry and the model deployments stay in `location` (eastus2 by
// default). Redis is pinned to canadacentral; override on the CLI if needed.
param redisSkuName = 'Balanced_B0'
param redisLocation = 'canadacentral'
param contentSafetySkuName = 'S0'

// Session 4 (MCP + OBO). Leave these empty to deploy the base gateway only.
// To deploy the MCP "expose-as-MCP" surface, first create the two Entra app
// registrations (see hol/walkthrough.md), then set these — ideally on the CLI so
// the secret is never committed:
//   az deployment group create ... \
//     --parameters entraIdTenantId=<tenant-id> oboClientId=<app2-client-id> \
//                  oboClientSecret=<app2-secret> mcpClientAudience=api://<app2-client-id>
param entraIdTenantId = ''
param oboClientId = ''
param oboClientSecret = ''
param mcpClientAudience = ''
