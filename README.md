# azure-ddns

Azure Functions-based dynamic DNS updater for Azure DNS zones.

## What this project does

This project provides a small HTTP endpoint that dynamic DNS clients (for example OpenWRT routers) can call to keep Azure DNS records current.

- Endpoint contract: `GET /api/update?client=<name>&key=<raw-key>&zone=<zone>&name=<record>[&ip=<address>]`
- Authentication: client name + raw key (validated against SHA-256 hash in config)
- Authorization: per-client allowed zone/record list (`*` wildcard record name supported)
- Record behavior: updates only the matching address family
  - IPv4 -> `A` record only
  - IPv6 -> `AAAA` record only

## Current architecture

- Runtime: .NET 8 isolated Azure Functions
- Hosting target: Azure Functions Flex Consumption (Linux)
- DNS backend: Azure DNS via Azure SDK and managed identity
- Config source: packaged file `config/dyndns.json` (by design, to keep complexity low)

### Request processing flow

1. Function receives `GET /api/update` request.
2. Query parameters are validated (`client`, `key`, `zone`, `name`; optional `ip`).
3. `config/dyndns.json` is loaded.
4. Client is authenticated by comparing SHA-256 hash of provided key.
5. Requested record is authorized for that client.
6. Effective IP is resolved:
   - `ip` query value if provided and valid
   - otherwise source IP from connection
7. DNS update is sent to Azure DNS using managed identity.
8. Plain-text response is returned for DDNS client compatibility.

## Response contract

Responses are plain text and stable for client compatibility:

- Success: `OK: ...` with HTTP `200`
- Validation failures: `ERROR: ...` with HTTP `400`
- Invalid credentials: HTTP `401`
- Unauthorized record: HTTP `403`
- Azure DNS backend failure: HTTP `502`
- Server/configuration failure: HTTP `500`

## Repository layout

- `src/AzureDdns.FunctionApp` - Function app code
  - `Functions/UpdateDnsFunction.cs` - HTTP entrypoint and orchestration
  - `Services/AuthService.cs` - authentication + authorization checks
  - `Services/IpResolver.cs` - source/explicit IP handling
  - `Services/DnsUpdateService.cs` - Azure DNS SDK update logic
  - `Services/ConfigProvider.cs` - reads DDNS config JSON
  - `config/dyndns.json` - sample DDNS configuration
  - `local.settings.json.example` - local app settings template
- `tests/AzureDdns.FunctionApp.Tests` - xUnit tests
- `scripts/smoke-test.ps1` - post-deployment smoke test for end-to-end DDNS validation
- `infra/main.bicep` - infrastructure definition
- `infra/modules/dns-zone-rbac.bicep` - optional zone-scoped RBAC assignment module
- `infra/main.parameters.json` - deploy-time parameter values
- `docs/deployment-plan.md` - detailed deployment + validation runbook
- `.azure/plan.md` - preparation plan artifact for Azure workflow

## Configuration reference

### Function app settings

| Setting | Required | Purpose |
|---|---|---|
| `DNS_SUBSCRIPTION_ID` | Yes | Subscription containing target Azure DNS zones |
| `DNS_RESOURCE_GROUP` | Yes | Resource group containing target Azure DNS zones |
| `CONFIG_PATH` | Yes | Relative/absolute path to DDNS config file; default is `config/dyndns.json` |
| `AZURE_FUNCTIONS_ENVIRONMENT` | Recommended | Environment label (`Development`, `Production`, etc.) |
| `APPLICATIONINSIGHTS_CONNECTION_STRING` | Recommended | Application Insights connection |
| `AzureWebJobsStorage` | Required in Azure | Functions host storage connection |

### DDNS config file (`config/dyndns.json`)

Schema summary:

- `zones` -> dictionary keyed by zone name
  - each zone supports `ttl`
- `clients` -> list of authenticated callers
  - `name` -> client identifier
  - `keyHash` -> SHA-256 hex hash of raw key
  - `allowedRecords` -> list of `{ zone, name }` permissions

Security notes:

- Store only hashed keys, never raw keys.
- Keep raw keys out of source control and logs.
- Rotate keys by updating `keyHash` and redeploying app package.

## Local development (step-by-step)

1. Copy `src/AzureDdns.FunctionApp/local.settings.json.example` to `src/AzureDdns.FunctionApp/local.settings.json`.
2. Set local values for:
   - `DNS_SUBSCRIPTION_ID`
   - `DNS_RESOURCE_GROUP`
   - `CONFIG_PATH` (normally `config/dyndns.json`)
3. Update `src/AzureDdns.FunctionApp/config/dyndns.json` with your test zone/client entries and hashed keys.
4. Build:
   - `dotnet build`
5. Run tests:
   - `dotnet test`
6. Run function locally (from app project folder) and send a test request to `/api/update`.

## Infrastructure (Bicep)

Infrastructure is defined in `infra/main.bicep` and is parameterized with:

- Required: `baseName`, `environmentName`, `location`, `dnsSubscriptionId`, `dnsResourceGroup`
- Optional overrides: `functionAppName`, `storageAccountName`, `appInsightsName`, `logAnalyticsWorkspaceName`, `functionPlanName`
- Optional DNS zone-scope RBAC input: `dnsZoneNames`

Behavior summary:

- If optional names are empty, deterministic names are derived from `baseName` + `environmentName` + unique suffix.
- If `dnsZoneNames` is empty, zone-scoped DNS RBAC assignments are skipped.
- If `dnsZoneNames` is populated, DNS Zone Contributor is assigned per zone to the Function App managed identity.

For full deployment and validation procedures, see `docs/deployment-plan.md`.

## GitHub deployment workflows

Two manually triggered GitHub Actions workflows are provided to keep infrastructure and app redeployments independent.

- `.github/workflows/deploy-infrastructure.yml`
  - Trigger: manual (`workflow_dispatch`)
  - Inputs: `ref`, `resourceGroup`, `runWhatIf`
  - Actions: checkout selected ref -> Azure OIDC login -> Bicep compile -> optional `what-if` -> deploy

- `.github/workflows/deploy-application.yml`
  - Trigger: manual (`workflow_dispatch`)
  - Inputs: `ref`, `functionAppName`, `runTests`
  - Actions: checkout selected ref -> restore/build/test -> publish -> Azure OIDC login -> deploy Function package

### Required GitHub repository secrets

Set these repository secrets before running either workflow:

- `AZURE_CLIENT_ID`
- `AZURE_TENANT_ID`
- `AZURE_SUBSCRIPTION_ID`

These values are used by `azure/login@v2` with OpenID Connect (OIDC).

### Required Azure federation/RBAC setup

1. In Microsoft Entra ID, configure a federated credential on the deployment app registration/service principal for this GitHub repository.
2. Grant the deployment principal least-privilege Azure roles needed for:
   - resource group deployments (infrastructure workflow)
   - Function App code deployment (application workflow)
3. Keep DNS zone runtime permissions on the Function App managed identity (not on the GitHub deployment principal) unless operationally required.

### Branch/ref selection behavior

Both workflows accept a `ref` input so you can manually choose which branch/tag/SHA to deploy at run time.
This supports controlled redeployments from release branches without changing workflow YAML.

## Operational checklist

After deployment, verify:

1. Managed identity exists on the Function App.
2. Identity has expected role assignments:
   - storage access for deployment container
   - optional DNS Zone Contributor on each configured DNS zone
3. IPv4 update requests modify only `A` records.
4. IPv6 update requests modify only `AAAA` records.
5. Unauthorized client/record requests are rejected.
6. Logs contain useful context without exposing raw keys.

## Smoke test script

Use `scripts/smoke-test.ps1` after deployment to perform an end-to-end functional validation of the Function App and Azure DNS integration.

What the script does:

1. Accepts the target Function App URL plus the client, zone, and record name to test.
2. Generates one random IPv4 address from the RFC5737 documentation ranges.
3. Generates one random IPv6 address from the RFC3849 documentation range.
4. Sends an IPv4 update request and verifies the plain-text success response.
5. Queries an authoritative name server for the target zone and waits for the `A` record to match the generated IPv4 address.
6. Sends an IPv6 update request and verifies the plain-text success response.
7. Queries an authoritative name server again and waits for the `AAAA` record to match the generated IPv6 address.
8. Confirms that the earlier `A` record value remains unchanged to validate `A`/`AAAA` independence.

Parameters:

- `-FunctionBaseUrl` - Base URL of the deployed Function App, with or without `/api/update`
- `-ClientName` - DDNS client name configured in `config/dyndns.json`
- `-ClientKey` - raw DDNS client key; if omitted, the script uses `AZURE_DDNS_CLIENT_KEY`
- `-Zone` - DNS zone to test
- `-Name` - relative record name to test (`@` for zone apex)
- `-DnsTimeoutSeconds` - optional DNS propagation wait timeout; default `120`
- `-DnsPollIntervalSeconds` - optional poll interval between authoritative DNS checks; default `5`

Example:

```powershell
$env:AZURE_DDNS_CLIENT_KEY = 'your-raw-client-key'
.\scripts\smoke-test.ps1 `
  -FunctionBaseUrl 'https://<your-function-app>.azurewebsites.net' `
  -ClientName 'stargate' `
  -Zone 'arkane-systems.net' `
  -Name 'smoke'
```

Prerequisites and notes:

- The tested record must already be authorized for the selected client in `config/dyndns.json`.
- The script depends on `Resolve-DnsName` being available in PowerShell.
- The script verifies authoritative DNS results, so completion time depends on Azure DNS write latency and name server visibility.
- The generated addresses are intentionally from documentation-only ranges so the smoke test never points records at real client endpoints.

## Notes for future you

If you revisit this repo after a long gap, start in this order:

1. `README.md` (high-level model + references)
2. `docs/deployment-plan.md` (exact deploy/validate procedure)
3. `infra/main.bicep` (what is provisioned and why)
4. `src/AzureDdns.FunctionApp/Functions/UpdateDnsFunction.cs` (runtime request flow)
