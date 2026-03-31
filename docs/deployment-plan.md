# Azure DDNS deployment plan

## Target platform

- Azure Functions Flex Consumption
- .NET 8 isolated worker
- Azure DNS zones hosted in one subscription and one resource group
- System-assigned managed identity for DNS updates

## Required Azure resources

1. Resource group for the Function App hosting resources if one does not already exist.
2. Function App on Flex Consumption.
3. Storage account created as part of the Functions deployment.
4. Application Insights instance for telemetry.
5. Existing Azure DNS zones in the target subscription and resource group.

## Required application settings

- `DNS_SUBSCRIPTION_ID`
- `DNS_RESOURCE_GROUP`
- `CONFIG_PATH`

Recommended additions:

- `APPLICATIONINSIGHTS_CONNECTION_STRING`
- `AZURE_FUNCTIONS_ENVIRONMENT`

## Identity and access

1. Enable the Function App system-assigned managed identity.
2. Grant `DNS Zone Contributor` to that identity on the resource group containing the DNS zones.
3. Review scope to ensure the identity cannot modify unrelated Azure resources.

## Configuration deployment

1. Package `config/dyndns.json` with the application content or deploy an environment-specific version during release.
2. Store only hashed client keys in the configuration file.
3. Keep raw keys outside source control and distribute them directly to DDNS clients.

## Validation checklist

1. Deploy the function app.
2. Confirm the managed identity has propagated.
3. Issue a test IPv4 update request.
4. Issue a test IPv6 update request.
5. Verify that `A` and `AAAA` record updates remain independent.
6. Verify unauthorized client, zone, and record requests are rejected.
7. Verify logs capture client name, zone, record, resolved IP, and mismatch events without logging raw keys.

## Future automation

When implementation is complete, add:

- infrastructure-as-code for Flex Consumption deployment
- CI workflow for build and test
- deployment workflow for packaging and release
- configuration promotion strategy per environment
