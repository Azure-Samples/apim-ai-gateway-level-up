using './main.bicep'

// Non-sensitive defaults. apimPublisherEmail is intentionally NOT set here —
// pass it on the command line so personal admin emails are never committed:
//   az deployment group create ... --parameters apimPublisherEmail=you@example.com
param location = 'canadacentral'
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
// redisLocation defaults to `location` in main.bicep, so all resources land in
// canadacentral. Override on the command line if regional capacity changes.
param redisSkuName = 'Balanced_B0'
param contentSafetySkuName = 'S0'
