# Azure DDNS quick operations

This is the short operational cheat sheet for routine work.

For full background and detailed explanations, see:

- `README.md`
- `docs/deployment-plan.md`

## 0) Manual deployment model

Deployment uses two independent CLI-driven phases:

**1. Infrastructure**

```pwsh
az login
az account set --subscription <subscription-id>
az deployment group create --resource-group <app-resource-group> --template-file infra/main.bicep --parameters @infra/main.parameters.json
```

**2. Application**

```pwsh
dotnet publish src/AzureDdns.FunctionApp/AzureDdns.FunctionApp.csproj -c Release -o out/functionapp
# create a zip from out/functionapp/
az functionapp deployment source config-zip --name <function-app-name> --resource-group <resource-group> --src <path-to-zip>
```

## 1) Validate before deploy

```pwsh
az bicep build --file infra/main.bicep
dotnet build
dotnet test
```

## 2) Deploy infrastructure

```pwsh
az deployment group create --resource-group <app-resource-group> --template-file infra/main.bicep --parameters @infra/main.parameters.json
```

## 3) Deploy app code

```pwsh
dotnet publish src/AzureDdns.FunctionApp/AzureDdns.FunctionApp.csproj -c Release -o out/functionapp
# create a zip from out/functionapp/
az functionapp deployment source config-zip --name <function-app-name> --resource-group <resource-group> --src <path-to-zip>
```

## 4) Verify app settings

Confirm these are present on the Function App:

- `DNS_SUBSCRIPTION_ID`
- `DNS_RESOURCE_GROUP`
- `CONFIG_PATH` (expected `config/dyndns.json`)
- `AzureWebJobsStorage`
- `APPLICATIONINSIGHTS_CONNECTION_STRING`
- `LOG_ALL_REQUEST_HEADERS_FOR_IP_DIAGNOSTICS` (expected `false`)

## 5) Verify identity and RBAC

- Function App system-assigned identity must exist.
- Storage role assignment should exist for deployment/runtime storage access.
- DNS role assignment behavior:
  - if `dnsZoneNames` is empty: no automatic zone-scoped DNS assignments are created
  - if `dnsZoneNames` is populated: `DNS Zone Contributor` is assigned per listed zone

## 6) Smoke test DDNS endpoint

Contract:

```text
GET /api/update?client=<name>&key=<raw-key>&zone=<zone>&name=<record>[&ip=<address>]
```

Expected checks:

1. valid IPv4 request updates only `A`
2. valid IPv6 request updates only `AAAA`
3. bad key returns `401`
4. unauthorized record returns `403`
5. unknown zone returns `400`

## 7) Rotate a client key hash

1. Generate new SHA-256 hash for the new raw key.
2. Update `src/AzureDdns.FunctionApp/config/dyndns.json` (`keyHash`).
3. Redeploy app package.
4. Verify old key fails and new key succeeds.

## 8) Common failures

- `ERROR: invalid credentials` -> client key hash mismatch.
- `ERROR: zone not configured` -> missing zone entry in `dyndns.json`.
- `ERROR: dns update failed` -> DNS RBAC/scope issue or DNS resource lookup issue.
- `ERROR: server configuration invalid` -> missing `DNS_SUBSCRIPTION_ID` or `DNS_RESOURCE_GROUP`.
