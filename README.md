# azure-ddns

Azure Functions-based dynamic DNS updater for Azure DNS zones.

## What this project does

This project provides HTTP endpoints that dynamic DNS clients can call to keep Azure DNS records current.
Two endpoint contracts are supported:

### Custom endpoint (`/api/update`) — for OpenWRT and similar

- Endpoint contract: `GET /api/update?client=<name>&key=<raw-key>&zone=<zone>&name=<record>[&ip=<address>]`
- Authentication: client name + raw key (validated against SHA-256 hash in config)
- Authorization: per-client allowed zone/record list (`*` wildcard record name supported)
- Record behavior: updates only the matching address family
  - IPv4 -> `A` record only
  - IPv6 -> `AAAA` record only

### DynDNS v2 endpoint (`/api/nic/update`) — for Unifi and ddclient-compatible routers

- Endpoint contract: `GET /api/nic/update?hostname=<fqdn>[&myip=<address>]`
- Authentication: HTTP Basic Auth (`Authorization: Basic base64(clientname:rawkey)`)
- FQDN zone inference: the zone is determined automatically from the hostname and configured zones
- Record behavior: same as above — only the matching address family is updated

## Current architecture

- Runtime: .NET 8 isolated Azure Functions
- Hosting target: Azure Functions Flex Consumption (Linux)
- DNS backend: Azure DNS via Azure SDK and managed identity
- Config source: packaged file `config/dyndns.json` (by design, to keep complexity low)

### Request processing flow (`/api/update`)

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

### Request processing flow (`/api/nic/update`)

1. Function receives `GET /api/nic/update` request.
2. `Authorization: Basic` header is parsed for client name and raw key.
3. `config/dyndns.json` is loaded.
4. Client is authenticated by comparing SHA-256 hash of provided key.
5. `hostname` FQDN is resolved to a configured zone and relative record name (longest-suffix match).
6. Requested record is authorized for that client.
7. Effective IP is resolved:
   - `myip` query value if provided and valid
   - otherwise source IP from connection
8. DNS update is sent to Azure DNS using managed identity.
9. DynDNS v2 plain-text response is returned.

## Response contract (`/api/update`)

Responses are plain text and stable for client compatibility:

- Success: `OK: ...` with HTTP `200`
- Validation failures: `ERROR: ...` with HTTP `400`
- Invalid credentials: HTTP `401`
- Unauthorized record: HTTP `403`
- Azure DNS backend failure: HTTP `502`
- Server/configuration failure: HTTP `500`

## Response contract (`/api/nic/update`)

Responses are plain text per DynDNS v2 specification:

| Body | HTTP status | Meaning |
|---|---|---|
| `good <ip>` | 200 | Update succeeded |
| `badauth` | 401 | Credentials missing or invalid |
| `nohost` | 200 | FQDN not resolvable to a configured zone/record, or record not authorized |
| `911` | 200 | Server-side error (configuration or DNS update failure) |

> **Note**: `nohost` is returned for both missing and unauthorized records to avoid leaking information about configured zones.

> **Note**: This endpoint always returns `good <ip>` on success. It never returns `nochg` — a no-change check is not performed.

## Configuring Unifi Express 7 Cloud Gateway

The Unifi Express 7 custom DDNS dialog has four fields: **Hostname**, **Username**, **Password**, and **Server**.
Because the `{IP}` and `{IP6}` placeholders go in the Server URL, IPv4 and IPv6 require **two separate DDNS entries**.

### Step-by-step

1. Open the Unifi console and navigate to **Settings → Internet → WAN** (or **Dynamic DNS** in the sidebar, depending on firmware version).
2. Click **Add Dynamic DNS** (or the `+` button).
3. Set **Service** to **Custom** (or **dyndns** in older firmware — the field layout is the same).
4. Fill in the fields for the **IPv4 entry**:

   | Field | Value |
   |---|---|
   | **Hostname** | The FQDN to update, e.g. `home.example.com` |
   | **Username** | The client name from `dyndns.json`, e.g. `my-router` |
   | **Password** | The raw key for that client (not the hash) |
   | **Server** | `<func-app-url>/api/nic/update?hostname={HOSTNAME}&myip={IP}` |

   Replace `<func-app-url>` with your Function App base URL, e.g. `https://my-ddns.azurewebsites.net`.

5. Save the entry.
6. Repeat, adding a second entry for the **IPv6 entry** with the same Hostname, Username, and Password but with `{IP6}` instead of `{IP}` in the Server URL:

   | Field | Value |
   |---|---|
   | **Hostname** | `home.example.com` |
   | **Username** | `my-router` |
   | **Password** | *same raw key* |
   | **Server** | `<func-app-url>/api/nic/update?hostname={HOSTNAME}&myip={IP6}` |

7. Save. Unifi will now call the endpoint on WAN IP changes, passing the current IPv4 or IPv6 address respectively.

> **Credentials placement**: put credentials only in the **Username** and **Password** fields, not in the Server URL.
> Unifi encodes them into the HTTP `Authorization: Basic` header automatically. Embedding them in the URL is unnecessary and may expose them in logs.

### Template variable syntax by firmware version

If the `{HOSTNAME}`/`{IP}`/`{IP6}` placeholders are not substituted by your firmware, use the older `%h`/`%i`/`%i6` variants:

| Placeholder | Newer firmware | Older firmware |
|---|---|---|
| FQDN (hostname field value) | `{HOSTNAME}` | `%h` |
| IPv4 address | `{IP}` | `%i` |
| IPv6 address | `{IP6}` | `%i6` |

Example Server URL with older syntax: `<func-app-url>/api/nic/update?hostname=%h&myip=%i`

Check your firmware release notes or the Unifi community documentation to determine which syntax your version supports.

### Prerequisites in `dyndns.json`

No separate configuration section is needed for the DynDNS endpoint. The same `config/dyndns.json` client entries
work for both `/api/update` and `/api/nic/update`. Ensure the client entry includes an `allowedRecords` entry for
the zone and record name corresponding to the FQDN you are updating:

```json
{
  "zones": {
    "example.com": { "ttl": 60 }
  },
  "clients": [
    {
      "name": "my-router",
      "keyHash": "<sha256-hex-of-raw-key>",
      "allowedRecords": [
        { "zone": "example.com", "name": "home" }
      ]
    }
  ]
}
```

The zone is determined automatically from the FQDN: `home.example.com` maps to record `home` in zone `example.com`.
For zone-apex records (e.g. `example.com` itself), use `"name": "@"` in `allowedRecords`.

## Repository layout

- `src/AzureDdns.FunctionApp` - Function app code
  - `Functions/UpdateDnsFunction.cs` - HTTP entrypoint for `/api/update`
  - `Functions/DyndnsUpdateFunction.cs` - HTTP entrypoint for `/api/nic/update` (DynDNS v2)
  - `Services/AuthService.cs` - authentication + authorization checks
  - `Services/FqdnResolver.cs` - FQDN to zone/record resolution (used by DynDNS endpoint)
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

## Manual deployment from a repository clone

Use these steps when deploying without GitHub Actions.

### Prerequisites

1. Install required tooling:
   - Azure CLI (`az`)
   - .NET 8 SDK
   - Azure Functions Core Tools v4 (for local verification)
2. Sign in to Azure CLI:
   - `az login`
3. Select the subscription where infrastructure will be deployed:
   - `az account set --subscription <infra-subscription-id-or-name>`
4. Confirm the target resource group exists (or create it):
   - `az group create --name <resource-group> --location <azure-region>`

### 1) Clone and prepare the repository

1. Clone the repository and switch to the intended branch:
   - `git clone https://github.com/arkane-systems/azure-ddns.git`
   - `cd azure-ddns`
   - `git checkout <branch-or-tag>`
2. Restore dependencies and validate baseline build:
   - `dotnet restore`
   - `dotnet build`
3. Optionally run tests before deploying:
   - `dotnet test`

### 2) Update infrastructure parameters (`infra/main.parameters.json`)

1. Open `infra/main.parameters.json`.
2. Set required values for your environment:
   - `baseName`
   - `environmentName`
   - `location`
   - `dnsSubscriptionId`
   - `dnsResourceGroup`
3. Decide whether to use explicit resource names or derived names:
   - Leave optional name parameters empty to use deterministic generated names.
   - Or set explicit values for `functionAppName`, `storageAccountName`, `appInsightsName`, `logAnalyticsWorkspaceName`, and `functionPlanName`.
4. If you want zone-scoped DNS RBAC created by Bicep, populate `dnsZoneNames` with the zone names that this app will update.
5. Save the file.

### 3) Update DDNS runtime configuration (`src/AzureDdns.FunctionApp/config/dyndns.json`)

1. Open `src/AzureDdns.FunctionApp/config/dyndns.json`.
2. Update the `zones` section with your real zone names and desired TTL values.
3. Update the `clients` section for your local requirements:
   - Set each client `name`.
   - Replace each `keyHash` with the SHA-256 hex hash of that client’s raw key.
   - Set `allowedRecords` to the exact `{ zone, name }` pairs each client is allowed to update (use `*` for wildcard record-name authorization within a zone when needed).
4. Ensure no raw client keys are stored in source files.
5. Save the file.

### 4) Deploy infrastructure with Bicep

1. Deploy `infra/main.bicep` using the updated parameters file:
   - `az deployment group create --resource-group <resource-group> --template-file infra/main.bicep --parameters @infra/main.parameters.json`
2. (Optional) Run a what-if preview before deployment:
   - `az deployment group what-if --resource-group <resource-group> --template-file infra/main.bicep --parameters @infra/main.parameters.json`
3. Record outputs and final resource names (especially Function App and Storage resources).

### 5) Configure Function App settings

After infrastructure deployment, set required app settings on the Function App:

1. `DNS_SUBSCRIPTION_ID` = subscription that contains the DNS zones.
2. `DNS_RESOURCE_GROUP` = resource group that contains the DNS zones.
3. `CONFIG_PATH` = `config/dyndns.json` (unless you intentionally changed the location).

Example:

`az functionapp config appsettings set --name <function-app-name> --resource-group <resource-group> --settings DNS_SUBSCRIPTION_ID=<dns-subscription-id> DNS_RESOURCE_GROUP=<dns-resource-group> CONFIG_PATH=config/dyndns.json`

### 6) Publish and deploy the Function App package

1. Publish the Function App:
   - `dotnet publish src/AzureDdns.FunctionApp/AzureDdns.FunctionApp.csproj -c Release -o out/functionapp`
2. Create a deployment zip from the publish output.
3. Deploy the zip package to the target Function App:
   - `az functionapp deployment source config-zip --name <function-app-name> --resource-group <resource-group> --src <path-to-zip>`

### 7) Validate deployment

1. Confirm the Function App is running and responds on `/api/update`.
2. Verify managed identity role assignments are present as expected (including optional DNS zone-scoped RBAC when enabled).
3. Run the smoke test:
   - `./scripts/smoke-test.ps1 -FunctionBaseUrl 'https://<function-app-name>.azurewebsites.net' -ClientName '<client>' -Zone '<zone>' -Name '<record>'`
4. Confirm behavior:
   - IPv4 requests update only `A`.
   - IPv6 requests update only `AAAA`.
   - Unauthorized updates are rejected with expected status codes.

### 8) Re-deployment workflow for future changes

- Infrastructure changes: update `infra/main.bicep` and/or `infra/main.parameters.json`, then rerun the Bicep deployment command.
- Runtime policy/client changes: update `src/AzureDdns.FunctionApp/config/dyndns.json`, republish, and redeploy the app package.
- Always rerun smoke validation after either type of change.

### Optional: local-only run before Azure deployment

If desired, validate behavior locally first:

1. Copy `src/AzureDdns.FunctionApp/local.settings.json.example` to `src/AzureDdns.FunctionApp/local.settings.json`.
2. Set local values for `DNS_SUBSCRIPTION_ID`, `DNS_RESOURCE_GROUP`, and `CONFIG_PATH`.
3. Run the app locally and test the `/api/update` endpoint with your configured client credentials.

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
4. `src/AzureDdns.FunctionApp/Functions/UpdateDnsFunction.cs` (custom endpoint request flow)
5. `src/AzureDdns.FunctionApp/Functions/DyndnsUpdateFunction.cs` (DynDNS v2 endpoint request flow)
