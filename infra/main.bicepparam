using './main.bicep'

// Non-sensitive defaults. apimPublisherEmail is intentionally NOT set here —
// pass it on the command line so personal admin emails are never committed:
//   az deployment group create ... --parameters apimPublisherEmail=you@example.com
param apimPublisherName = 'AI Gateway Level Up'
param namePrefix = 'aigwlvlup'
param modelDeploymentName = 'gpt-4.1-mini'
param modelName = 'gpt-4.1-mini'
param modelVersion = '2025-04-14'
param modelCapacity = 10
