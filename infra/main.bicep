targetScope = 'resourceGroup'

// Required naming/input parameters.
// baseName + environmentName are used to derive stable, readable defaults for resource names.
@description('Base name used to derive resource names when explicit overrides are not supplied.')
param baseName string

@description('Deployment environment label used for naming/tagging (for example: dev, test, prod).')
param environmentName string

@description('Azure location for all resources.')
param location string = resourceGroup().location

// DNS target settings used by the application at runtime.
@description('Subscription ID that contains the Azure DNS zones updated by the function app.')
param dnsSubscriptionId string

@description('Resource group name that contains the Azure DNS zones updated by the function app.')
param dnsResourceGroup string

// Optional explicit name overrides.
// Leave empty to use derived defaults.
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

// Optional DNS zones for automatic zone-scoped RBAC assignment.
// Keep empty when you do not want template-managed DNS role assignment.
@description('Optional DNS zone names to grant DNS Zone Contributor at zone scope in the shared DNS resource group.')
param dnsZoneNames array = []

// Whether the documentation address ranges (192.0.2.0/24, 198.51.100.0/24, 203.0.113.0/24, 2001:db8::/32, 3fff::/20)
// may be published. They are harmless (never routable) and the smoke test uses them, so they are allowed by default.
@description('Allow publishing documentation-range addresses (RFC 5737/3849/9637). Set false to refuse them; the smoke test then needs -TestIpv4/-TestIpv6.')
param allowDocumentationAddresses bool = true

// Normalize tokens for Azure resource naming constraints.
var baseToken = toLower(replace(replace(baseName, '-', ''), '_', ''))
var envToken = toLower(replace(replace(environmentName, '-', ''), '_', ''))
var uniqueSuffix = take(uniqueString(subscription().subscriptionId, resourceGroup().id, baseName, environmentName), 6)

// Derived default resource names.
var functionAppNameDerived = take('${baseToken}-func-${environmentName}-${uniqueSuffix}', 60)
var storageAccountNameDerived = take('${baseToken}${envToken}${uniqueSuffix}', 24)
var appInsightsNameDerived = take('${baseToken}-appi-${environmentName}', 260)
var logAnalyticsWorkspaceNameDerived = take('${baseToken}-law-${environmentName}', 63)
var functionPlanNameDerived = take('${baseToken}-plan-${environmentName}', 40)

// Resolved names use override values when provided.
var functionAppNameResolved = empty(functionAppName) ? functionAppNameDerived : functionAppName
var storageAccountNameResolved = empty(storageAccountName) ? storageAccountNameDerived : toLower(storageAccountName)
var appInsightsNameResolved = empty(appInsightsName) ? appInsightsNameDerived : appInsightsName
var logAnalyticsWorkspaceNameResolved = empty(logAnalyticsWorkspaceName) ? logAnalyticsWorkspaceNameDerived : logAnalyticsWorkspaceName
var functionPlanNameResolved = empty(functionPlanName) ? functionPlanNameDerived : functionPlanName

// Static deployment values.
var deploymentStorageContainerName = 'function-releases'
var storageBlobDataOwnerRoleDefinitionId = 'b7e6dc6d-f1e8-4753-8033-0f276bb0955b'
var storageTableDataContributorRoleDefinitionId = '0a9a7e1f-b9d0-4cc4-a60d-0319b160aaa3'

// Storage account used by the Functions host/runtime and deployment package source.
// Access is identity-only: the Function App's managed identity is granted data-plane roles below (AzureWebJobsStorage
// uses the AzureWebJobsStorage__accountName / __credential settings, and the deployment container uses
// SystemAssignedIdentity), and shared-key (account key / connection string / SAS signed with the key) access is disabled
// so there is no long-lived secret to leak. If a tool you use needs a key, set allowSharedKeyAccess back to true.
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
    allowSharedKeyAccess: false
  }
}

// Blob container used by Flex Consumption deployment configuration.
resource deploymentContainer 'Microsoft.Storage/storageAccounts/blobServices/containers@2023-05-01' = {
  name: '${storageAccount.name}/default/${deploymentStorageContainerName}'
  properties: {
    publicAccess: 'None'
  }
}

// Central workspace for logs/telemetry storage.
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

// Application Insights instance linked to the workspace above.
resource applicationInsights 'Microsoft.Insights/components@2020-02-02' = {
  name: appInsightsNameResolved
  location: location
  kind: 'web'
  properties: {
    Application_Type: 'web'
    WorkspaceResourceId: logAnalyticsWorkspace.id
  }
}

// Flex Consumption hosting plan for the Function App.
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

// Function App runtime resource.
// Uses system-assigned managed identity and sets required app settings for this solution.
resource functionApp 'Microsoft.Web/sites@2024-04-01' = {
  name: functionAppNameResolved
  location: location
  kind: 'functionapp,linux'
  // The Azure portal links a function app to its Application Insights resource through this hidden tag. The portal adds it
  // when you connect them; declaring it here stops a redeploy from silently removing the link.
  tags: {
    'hidden-link: /app-insights-resource-id': applicationInsights.id
  }
  identity: {
    type: 'SystemAssigned'
  }
  properties: {
    serverFarmId: functionPlan.id
    // Credentials travel in the Authorization header; never accept them over plain HTTP.
    httpsOnly: true
    functionAppConfig: {
      runtime: {
        name: 'dotnet-isolated'
        version: '10.0'
      }
      scaleAndConcurrency: {
        // Do not go below 40 for an HTTP app. The Flex Consumption default is 100 (range 1-1000), and Microsoft's docs warn that
        // setting it below 40 for HTTP apps can cause frequent request failures and prolonged throttling windows. A cap of 5 was
        // tried here and caused intermittent stalls: roughly 3-10% of requests hung or waited 5-20 s (in 5 s steps) for several
        // hours after a deploy, and 200/200 requests were clean once the cap was raised to 40. (The docs do not describe the 5 s
        // pattern; the likely cause is the platform's own instance bursts and replacements filling a tiny cap and then waiting
        // on throttled retries.) 40 is the lowest recommended value; the default of 100 would also be fine. Cost exposure is not
        // a reason to lower it: Flex bills for execution time and a rejected request takes a few milliseconds.
        maximumInstanceCount: 40
        instanceMemoryMB: 512
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
          name: 'LOG_ALL_REQUEST_HEADERS_FOR_IP_DIAGNOSTICS'
          value: 'false'
        }
        {
          name: 'ALLOW_DOCUMENTATION_ADDRESSES'
          value: string(allowDocumentationAddresses)
        }
        // Identity-based host storage: no account key in app settings. The host reaches blob/queue/table endpoints
        // of this account as the app's system-assigned managed identity (roles are assigned below).
        {
          name: 'AzureWebJobsStorage__accountName'
          value: storageAccount.name
        }
        {
          name: 'AzureWebJobsStorage__credential'
          value: 'managedidentity'
        }
      ]
    }
  }
  dependsOn: [
    deploymentContainer
  ]
}

// Grants Function App identity access to the storage account's blob data plane. Storage Blob Data Owner is the minimum
// role the Functions host needs for AzureWebJobsStorage (singleton locks, keys) and also covers the deployment container.
resource deploymentStorageRoleAssignment 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(storageAccount.id, functionApp.id, storageBlobDataOwnerRoleDefinitionId)
  scope: storageAccount
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', storageBlobDataOwnerRoleDefinitionId)
    principalId: functionApp.identity.principalId
    principalType: 'ServicePrincipal'
  }
}

// Lets the host persist diagnostic events in Table storage. Without it the host logs warnings that it cannot write them.
resource hostTableRoleAssignment 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(storageAccount.id, functionApp.id, storageTableDataContributorRoleDefinitionId)
  scope: storageAccount
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', storageTableDataContributorRoleDefinitionId)
    principalId: functionApp.identity.principalId
    principalType: 'ServicePrincipal'
  }
}

// Optional module to grant DNS Zone Contributor at individual DNS zone scope.
// This runs only when dnsZoneNames contains one or more values.
module dnsZoneRbac './modules/dns-zone-rbac.bicep' = if (!empty(dnsZoneNames)) {
  name: 'dns-zone-rbac'
  scope: resourceGroup(dnsSubscriptionId, dnsResourceGroup)
  params: {
    dnsZoneNames: dnsZoneNames
    principalId: functionApp.identity.principalId
  }
}

// Deployment outputs for downstream tooling/inspection.
output functionAppName string = functionApp.name
output functionAppPrincipalId string = functionApp.identity.principalId
output storageAccountName string = storageAccount.name
output applicationInsightsConnectionString string = applicationInsights.properties.ConnectionString
output dnsZoneScopedRoleAssignments array = [for zoneName in dnsZoneNames: '/subscriptions/${dnsSubscriptionId}/resourceGroups/${dnsResourceGroup}/providers/Microsoft.Network/dnsZones/${zoneName}']
