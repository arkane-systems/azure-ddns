# Azure DDNS deployment runbook

This document is the operational playbook for deploying and validating this project.

## 1) Deployment target and assumptions

- Azure Functions Flex Consumption
- .NET 8 isolated worker
- Function app configuration file packaged with app (`config/dyndns.json`)
- Azure DNS zones already exist (often in a shared DNS resource group)
- Function app uses system-assigned managed identity for Azure DNS updates

## 2) What infrastructure is provisioned

From `infra/main.bicep`:

1. Storage account (Functions host + deployment container)
2. Blob container `function-releases` for one-deploy package source
3. Log Analytics workspace
4. Application Insights (workspace-based)
5. Flex Consumption plan (`FC1`)
6. Function App (Linux, `dotnet-isolated` runtime)
7. Storage Blob Data Owner role assignment for function managed identity on storage account
8. Optional DNS Zone Contributor role assignments per DNS zone (when `dnsZoneNames` is populated)

## 3) Parameter reference (`infra/main.parameters.json`)

### Required

| Parameter | Description | Example |
|---|---|---|
| `baseName` | Base token used to derive default resource names | `azddns` |
| `environmentName` | Environment label used for naming and app environment setting | `dev`, `test`, `prod` |
| `location` | Azure region for app resources | `westeurope` |
| `dnsSubscriptionId` | Subscription containing target DNS zones | `00000000-0000-0000-0000-000000000000` |
| `dnsResourceGroup` | Resource group containing target DNS zones | `rg-dns-shared` |

### Optional

| Parameter | Description | Behavior when empty |
|---|---|---|
| `functionAppName` | Explicit Function App name override | Derived from `baseName`/`environmentName` |
| `storageAccountName` | Explicit Storage Account name override | Derived from `baseName`/`environmentName` |
| `appInsightsName` | Explicit App Insights name override | Derived from `baseName`/`environmentName` |
| `logAnalyticsWorkspaceName` | Explicit Log Analytics name override | Derived from `baseName`/`environmentName` |
| `functionPlanName` | Explicit Flex plan name override | Derived from `baseName`/`environmentName` |
| `dnsZoneNames` | DNS zones for zone-scoped DNS RBAC assignment | No DNS zone role assignments created |

## 4) Application settings configured by IaC

These are set in `siteConfig.appSettings` during deployment:

- `APPLICATIONINSIGHTS_CONNECTION_STRING`
- `FUNCTIONS_EXTENSION_VERSION` (`~4`)
- `FUNCTIONS_WORKER_RUNTIME` (`dotnet-isolated`)
- `AZURE_FUNCTIONS_ENVIRONMENT` (from `environmentName`)
- `DNS_SUBSCRIPTION_ID`
- `DNS_RESOURCE_GROUP`
- `CONFIG_PATH` (`config/dyndns.json`)
- `AzureWebJobsStorage`

No manual portal configuration is required for these values in normal deployments.

## 5) Pre-deployment checklist

1. Confirm you are in the correct Azure tenant/subscription context.
2. Ensure DNS zones already exist in `dnsSubscriptionId` / `dnsResourceGroup`.
3. Decide whether to assign zone-scoped RBAC now:
   - keep `dnsZoneNames` empty to skip
   - populate with zone names to assign automatically
4. Confirm `dyndns.json` contains only hashed keys.
5. Validate Bicep template:
   - `az bicep build --file infra/main.bicep`

## 6) Deploy infrastructure (step-by-step)

### Option A: Azure CLI deployment

1. Set/confirm parameter values in `infra/main.parameters.json`.
2. Deploy to your app resource group:

```pwsh
az deployment group create --resource-group <app-resource-group> --template-file infra/main.bicep --parameters @infra/main.parameters.json
```

### Option B: Azure Developer CLI flow

1. Ensure `azure.yaml` is present and points to `infra/`.
2. Configure environment values if needed.
3. Run:

```pwsh
azd up
```

## 7) Post-deployment steps

1. Deploy function app code package (if infra-only deployment was used).
2. Confirm Function App system-assigned identity exists.
3. If `dnsZoneNames` was left empty, assign DNS permissions manually when ready.
4. If `dnsZoneNames` was set, verify role assignments exist at each zone scope.

## 8) Post-deployment validation procedures

### A. Infrastructure validation

1. Function app is Running.
2. App settings are present and match expected values.
3. Application Insights receives traces.
4. Storage account and `function-releases` container exist.

### B. Functional validation

Perform from a client/network path representing your DDNS caller:

1. Valid IPv4 update request.
2. Valid IPv6 update request.
3. Invalid key request returns `401`.
4. Unauthorized record request returns `403`.
5. Unknown zone request returns `400`.

Expected behavior:

- `A` and `AAAA` updates remain independent.
- Responses are plain-text `OK:`/`ERROR:`.

### C. Logging and security validation

1. Logs include client/zone/record/IP context.
2. Logs do not contain raw client keys.
3. Identity permissions are least-privilege:
   - storage access needed for deployment/runtime
   - DNS zone scope only where required

## 9) Troubleshooting quick notes

- `ERROR: zone not configured` -> zone key missing in `dyndns.json`.
- `ERROR: invalid credentials` -> client name/hash mismatch.
- `ERROR: dns update failed` -> missing/incorrect RBAC or DNS resource reference issues.
- `ERROR: server configuration invalid` -> missing required settings (`DNS_SUBSCRIPTION_ID`, `DNS_RESOURCE_GROUP`, etc.).

## 10) Suggested ongoing operations

- Keep `infra/main.parameters.json` values per environment under controlled change process.
- Rotate client keys periodically by updating `keyHash` values and redeploying code package.
- Re-run the functional validation suite after any config or RBAC changes.
