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
- `AzureWebJobsStorage__accountName` and `AzureWebJobsStorage__credential` (`managedidentity`) — no `AzureWebJobsStorage` connection string
- `APPLICATIONINSIGHTS_CONNECTION_STRING`
- `LOG_ALL_REQUEST_HEADERS_FOR_IP_DIAGNOSTICS` (expected `false`)
- `ALLOW_DOCUMENTATION_ADDRESSES` (expected `true` unless you deliberately refuse documentation ranges)

## 5) Verify identity and RBAC

- Function App system-assigned identity must exist.
- Storage role assignments should exist for the function identity on the storage account: Storage Blob Data Owner (host + deployment) and Storage Table Data Contributor (host diagnostics). Role changes can take a few minutes to take effect; a host that cannot reach storage at first start usually recovers once they do.
- DNS role assignment behavior:
  - if `dnsZoneNames` is empty: no automatic zone-scoped DNS assignments are created
  - if `dnsZoneNames` is populated: `DNS Zone Contributor` is assigned per listed zone

## 6) Smoke test DDNS endpoint

Run `scripts/smoke-test-dyndns.ps1` (see README, "Smoke test script"). Contract:

```text
GET /api/nic/update?hostname=<fqdn>[&myip=<address>]      (Authorization: Basic client:key)
```

Expected checks:

1. valid IPv4 request updates only `A` (`good <ip>`)
2. valid IPv6 request updates only `AAAA` (`good <ip>`)
3. bad key returns `401` with body `badauth`
4. unauthorized or unknown hostname returns `200` with body `nohost`
5. plain `http://` requests are refused or redirected (the app is HTTPS-only)
6. repeating the same update returns `nochg <ip>` (no write is made)

## 7) Rotate a client key hash

1. Generate new SHA-256 hash for the new raw key.
2. Update `src/AzureDdns.FunctionApp/config/dyndns.json` (`keyHash`).
3. Redeploy app package.
4. Verify old key fails and new key succeeds.

## 8) Common failures

- `badauth` (401) -> client name/key mismatch, or no usable `Authorization: Basic` header.
- `nohost` -> hostname not under a configured zone, or the client is not allowed to update that record.
- `911` (200) -> server-side failure: DNS RBAC/scope issue, DNS write failure, managed identity unavailable, missing `DNS_SUBSCRIPTION_ID`/`DNS_RESOURCE_GROUP`, an unusable `myip`, or an address refused as not publicly routable (private, loopback, link-local, ULA, ...; the log names the reason); the app log has the detail.
- `911` with HTTP `503` -> `config/dyndns.json` missing from the package, unreadable, or invalid JSON; the app log names the path and parse error.
