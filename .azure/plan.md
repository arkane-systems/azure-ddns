# Azure DDNS Infrastructure Plan

## Status
Ready for Validation

## Mode
MODIFY (existing .NET 8 Azure Functions app)

## Objective
Provision Azure infrastructure for the Azure DDNS Function App using Bicep, aligned with Flex Consumption and managed identity.

## Scope
- Create Bicep templates under `infra/`.
- Add deployment metadata for Azure Developer CLI.
- Keep existing application contract and runtime behavior unchanged.

## Architecture Decisions
- Hosting: Azure Functions Flex Consumption (Linux, .NET 8 isolated worker).
- Observability: Application Insights + Log Analytics workspace.
- Storage: General-purpose StorageV2 account for Functions runtime.
- Security: System-assigned managed identity for Function App.
- Naming: Use a required `baseName` and `environmentName` to derive default resource names, with optional explicit name overrides.
- DNS updates: App setting-driven target subscription/resource group; RBAC assigned outside template scope unless user requests in-scope role assignment.

## Resources to Deploy
1. Function App hosting resource (Flex Consumption).
2. Storage account for Functions content/state.
3. Application Insights component.
4. Log Analytics workspace.
5. App Service plan / hosting configuration required by Flex Consumption model.

## Application Settings
- `DNS_SUBSCRIPTION_ID`
- `DNS_RESOURCE_GROUP`
- `CONFIG_PATH` (fixed to `config/dyndns.json`)
- `AZURE_FUNCTIONS_ENVIRONMENT`
- `APPLICATIONINSIGHTS_CONNECTION_STRING`
- `AzureWebJobsStorage`

## Parameters
- Required:
  - `baseName`
  - `location`
  - `environmentName`
  - `dnsSubscriptionId`
  - `dnsResourceGroup`
- Optional overrides:
  - `functionAppName`
  - `storageAccountName`
  - `appInsightsName`
  - `logAnalyticsWorkspaceName`
  - `functionPlanName`

## Validation Plan
- Validate Bicep template compilation.
- Ensure generated artifacts match existing app requirements.

## Execution Steps
1. Generate `infra/main.bicep` with derived names and optional overrides.
2. Generate `infra/main.parameters.json`.
3. Generate `azure.yaml` for azd.
4. Validate IaC syntax and references.
5. Add split manual GitHub workflows for infrastructure and application deployments with branch/ref selection.
6. Document workflow secrets, inputs, and run procedures in repository documentation.

## Out of Scope
- Application code refactors.
- Runtime-refreshable config redesign.
- Deployment execution (`azd up`).
