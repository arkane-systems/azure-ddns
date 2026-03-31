targetScope = 'resourceGroup'

@description('DNS zone names that should grant DNS Zone Contributor to the Function App identity.')
param dnsZoneNames array

@description('Managed identity principal ID of the Function App.')
param principalId string

var dnsZoneContributorRoleDefinitionId = 'befefa01-2a29-4197-83a8-272ff33ce314'

resource dnsZones 'Microsoft.Network/dnsZones@2018-05-01' existing = [for zoneName in dnsZoneNames: {
  name: zoneName
}]

resource dnsZoneRoleAssignments 'Microsoft.Authorization/roleAssignments@2022-04-01' = [for (_, i) in dnsZoneNames: {
  name: guid(dnsZones[i].id, principalId, dnsZoneContributorRoleDefinitionId)
  scope: dnsZones[i]
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', dnsZoneContributorRoleDefinitionId)
    principalId: principalId
    principalType: 'ServicePrincipal'
  }
}]
