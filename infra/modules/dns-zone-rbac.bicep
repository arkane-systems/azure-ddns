targetScope = 'resourceGroup'

// DNS zones in this resource group to which the Function App identity should be granted write access.
@description('DNS zone names that should grant DNS Zone Contributor to the Function App identity.')
param dnsZoneNames array

// Principal ID of the Function App system-assigned identity.
@description('Managed identity principal ID of the Function App.')
param principalId string

// Built-in role definition ID for "DNS Zone Contributor".
var dnsZoneContributorRoleDefinitionId = 'befefa01-2a29-4197-83a8-272ff33ce314'

// Existing DNS zone references (zones are not created by this module).
resource dnsZones 'Microsoft.Network/dnsZones@2018-05-01' existing = [for zoneName in dnsZoneNames: {
  name: zoneName
}]

// One role assignment per DNS zone, scoped to the zone itself.
resource dnsZoneRoleAssignments 'Microsoft.Authorization/roleAssignments@2022-04-01' = [for (_, i) in dnsZoneNames: {
  name: guid(dnsZones[i].id, principalId, dnsZoneContributorRoleDefinitionId)
  scope: dnsZones[i]
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', dnsZoneContributorRoleDefinitionId)
    principalId: principalId
    principalType: 'ServicePrincipal'
  }
}]
