using './main.bicep'

// Fill these in before deploying.
param apimPublisherEmail = 'you@example.com'
param apimPublisherName = 'AI Gateway Level Up'

// Optional overrides (defaults shown).
param namePrefix = 'aigwlvlup'
param modelDeploymentName = 'gpt-4.1-mini'
param modelName = 'gpt-4.1-mini'
param modelVersion = '2025-04-14'
param modelCapacity = 10
