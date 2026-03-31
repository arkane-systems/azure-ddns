targetScope = 'resourceGroup'

@description('Base name used to derive resource names when explicit overrides are not supplied.')
param baseName string

@description('Deployment environment label used for naming/tagging (for example: dev, test, prod).')
param environmentName string

@description('Azure location for all resources.')
param location string = resourceGroup().location

@description('Subscription ID that contains the Azure DNS zones updated by the function app.')
param dnsSubscriptionId string

@description('Resource group name that contains the Azure DNS zones updated by the function app.')
param dnsResourceGroup string

@description('Optional override for Function App name.')
param functionAppName string = ''

@description('Optional override for Storage Account name.')
param storageAccountName string = ''

@description('Optional override for Application Insights name.')
param appInsightsName string = ''

@description('Optional override for Log Analytics workspace name.')
param logAnalyticsWorkspaceName string = ''

@description('Optional override for Flex Consumption plan name.')
param functionPlanName string = ''

@description('Optional DNS zone names to grant DNS Zone Contributor at zone scope in the shared DNS resource group.')
param dnsZoneNames array = []

var baseToken = toLower(replace(replace(baseName, '-', ''), '_', ''))
var envToken = toLower(replace(replace(environmentName, '-', ''), '_', ''))
var uniqueSuffix = take(uniqueString(subscription().subscriptionId, resourceGroup().id, baseName, environmentName), 6)

var functionAppNameDerived = take('${baseToken}-func-${environmentName}-${uniqueSuffix}', 60)
var storageAccountNameDerived = take('${baseToken}${envToken}${uniqueSuffix}', 24)
var appInsightsNameDerived = take('${baseToken}-appi-${environmentName}', 260)
var logAnalyticsWorkspaceNameDerived = take('${baseToken}-law-${environmentName}', 63)
var functionPlanNameDerived = take('${baseToken}-plan-${environmentName}', 40)

var functionAppNameResolved = empty(functionAppName) ? functionAppNameDerived : functionAppName
var storageAccountNameResolved = empty(storageAccountName) ? storageAccountNameDerived : toLower(storageAccountName)
var appInsightsNameResolved = empty(appInsightsName) ? appInsightsNameDerived : appInsightsName
var logAnalyticsWorkspaceNameResolved = empty(logAnalyticsWorkspaceName) ? logAnalyticsWorkspaceNameDerived : logAnalyticsWorkspaceName
var functionPlanNameResolved = empty(functionPlanName) ? functionPlanNameDerived : functionPlanName

var deploymentStorageContainerName = 'function-releases'
var storageBlobDataOwnerRoleDefinitionId = 'b7e6dc6d-f1e8-4753-8033-0f276bb0955b'
var azureWebJobsStorageConnectionString = 'DefaultEndpointsProtocol=https;AccountName=${storageAccount.name};EndpointSuffix=${environment().suffixes.storage};AccountKey=${storageAccount.listKeys().keys[0].value}'

resource storageAccount 'Microsoft.Storage/storageAccounts@2023-05-01' = {
  name: storageAccountNameResolved
  location: location
  kind: 'StorageV2'
  sku: {
    name: 'Standard_LRS'
  }
  properties: {
    supportsHttpsTrafficOnly: true
    allowBlobPublicAccess: false
    minimumTlsVersion: 'TLS1_2'
    defaultToOAuthAuthentication: true
  }
}

resource deploymentContainer 'Microsoft.Storage/storageAccounts/blobServices/containers@2023-05-01' = {
  name: '${storageAccount.name}/default/${deploymentStorageContainerName}'
  properties: {
    publicAccess: 'None'
  }
}

resource logAnalyticsWorkspace 'Microsoft.OperationalInsights/workspaces@2023-09-01' = {
  name: logAnalyticsWorkspaceNameResolved
  location: location
  properties: {
    sku: {
      name: 'PerGB2018'
    }
    retentionInDays: 30
  }
}

resource applicationInsights 'Microsoft.Insights/components@2020-02-02' = {
  name: appInsightsNameResolved
  location: location
  kind: 'web'
  properties: {
    Application_Type: 'web'
    WorkspaceResourceId: logAnalyticsWorkspace.id
  }
}

resource functionPlan 'Microsoft.Web/serverfarms@2024-04-01' = {
  name: functionPlanNameResolved
  location: location
  kind: 'functionapp'
  sku: {
    tier: 'FlexConsumption'
    name: 'FC1'
  }
  properties: {
    reserved: true
  }
}

resource functionApp 'Microsoft.Web/sites@2024-04-01' = {
  name: functionAppNameResolved
  location: location
  kind: 'functionapp,linux'
  identity: {
    type: 'SystemAssigned'
  }
  properties: {
    serverFarmId: functionPlan.id
    functionAppConfig: {
      runtime: {
        name: 'dotnet-isolated'
        version: '8.0'
      }
      scaleAndConcurrency: {
        maximumInstanceCount: 40
        instanceMemoryMB: 2048
      }
      deployment: {
        storage: {
          type: 'blobContainer'
          value: '${storageAccount.properties.primaryEndpoints.blob}${deploymentStorageContainerName}'
          authentication: {
            type: 'SystemAssignedIdentity'
          }
        }
      }
    }
    siteConfig: {
      appSettings: [
        {
          name: 'APPLICATIONINSIGHTS_CONNECTION_STRING'
          value: applicationInsights.properties.ConnectionString
        }
        {
          name: 'FUNCTIONS_EXTENSION_VERSION'
          value: '~4'
        }
        {
          name: 'FUNCTIONS_WORKER_RUNTIME'
          value: 'dotnet-isolated'
        }
        {
          name: 'AZURE_FUNCTIONS_ENVIRONMENT'
          value: environmentName
        }
        {
          name: 'DNS_SUBSCRIPTION_ID'
          value: dnsSubscriptionId
        }
        {
          name: 'DNS_RESOURCE_GROUP'
          value: dnsResourceGroup
        }
        {
          name: 'CONFIG_PATH'
          value: 'config/dyndns.json'
        }
        {
          name: 'AzureWebJobsStorage'
          value: azureWebJobsStorageConnectionString
        }
      ]
    }
  }
  dependsOn: [
    deploymentContainer
  ]
}

resource deploymentStorageRoleAssignment 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(storageAccount.id, functionApp.id, storageBlobDataOwnerRoleDefinitionId)
  scope: storageAccount
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', storageBlobDataOwnerRoleDefinitionId)
    principalId: functionApp.identity.principalId
    principalType: 'ServicePrincipal'
  }
}

module dnsZoneRbac './modules/dns-zone-rbac.bicep' = if (!empty(dnsZoneNames)) {
  name: 'dns-zone-rbac'
  scope: resourceGroup(dnsSubscriptionId, dnsResourceGroup)
  params: {
    dnsZoneNames: dnsZoneNames
    principalId: functionApp.identity.principalId
  }
}

output functionAppName string = functionApp.name
output functionAppPrincipalId string = functionApp.identity.principalId
output storageAccountName string = storageAccount.name
output applicationInsightsConnectionString string = applicationInsights.properties.ConnectionString
output dnsZoneScopedRoleAssignments array = [for zoneName in dnsZoneNames: '/subscriptions/${dnsSubscriptionId}/resourceGroups/${dnsResourceGroup}/providers/Microsoft.Network/dnsZones/${zoneName}']
