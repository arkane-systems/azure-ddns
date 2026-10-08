# azure-ddns

Azure Functions-based dynamic DNS updater for Azure DNS zones.

## What this project does

This project provides an HTTP endpoint that dynamic DNS clients can call to keep Azure DNS records current. It
speaks the DynDNS v2 protocol, so it works with Unifi gateways, ddclient, OpenWRT (`dyndns2` provider) and similar clients.

### DynDNS v2 endpoint (`/api/nic/update`)

- Endpoint contract: `GET /api/nic/update?hostname=<fqdn>[&myip=<address>]`
- Authentication: HTTP Basic Auth (`Authorization: Basic base64(clientname:rawkey)`); the key never appears in the URL
- Transport: HTTPS only (`httpsOnly` is set on the Function App in `infra/main.bicep`)
- FQDN zone inference: the zone is determined automatically from the hostname and configured zones
- Authorization: per-client allowed zone/record list (`*` wildcard record name supported)
- Record behavior: updates only the matching address family
  - IPv4 -> `A` record only
  - IPv6 -> `AAAA` record only

The earlier custom `/api/update` endpoint (client name and key in the query string) has been removed. Clients that
used it must switch to `/api/nic/update` with Basic auth; see the Unifi client in `unifi-client/` for an example.

## Current architecture

- Runtime: .NET 10 isolated Azure Functions
- Hosting target: Azure Functions Flex Consumption (Linux)
- DNS backend: Azure DNS via Azure SDK and managed identity
- Config source: packaged file `config/dyndns.json` (by design, to keep complexity low)

### Request processing flow (`/api/nic/update`)

The work is split between a thin HTTP front end and a protocol-neutral coordinator, so that another API can be added
later without duplicating policy:

- `Functions/DyndnsUpdateFunction.cs` parses the DynDNS v2 request and renders the DynDNS v2 response.
- `Services/DdnsUpdateCoordinator.cs` performs the update and reports a `DdnsUpdateStatus`; it knows nothing about
  DynDNS response codes.

Steps:

1. Function receives `GET /api/nic/update` request.
2. `Authorization: Basic` header is parsed for client name and raw key (missing/invalid header -> `badauth`).
3. The coordinator loads `config/dyndns.json`.
4. Client is authenticated by comparing SHA-256 hash of provided key.
5. `hostname` FQDN is resolved to a configured zone and relative record name (longest-suffix match).
6. Requested record is authorized for that client.
7. Effective IP is resolved:
   - `myip` query value if provided and valid
   - otherwise the resolved source IP (see [Source IP resolution](#source-ip-resolution))
8. The existing record set is read; if it already holds the address and TTL nothing is written, otherwise the update is
   sent to Azure DNS using managed identity.
9. The function maps the coordinator's status to a DynDNS v2 plain-text response.

## Source IP resolution

`Services/IpResolver.cs` decides which address is written. When the client supplies an explicit address (`myip`)
that address is used. Otherwise the *source IP* is derived as follows.

The app normally sits behind Azure's front end, so the TCP peer seen by the function is an infrastructure address,
not the caller. The resolver therefore treats the peer as a **known proxy hop** when its address is loopback,
RFC 1918 private (`10/8`, `172.16/12`, `192.168/16`), IPv4 link-local (`169.254/16`), or IPv6 link-local, site-local
or unique-local (`fc00::/7`); IPv4-mapped IPv6 peers are unwrapped first.

| Peer address | Source IP used |
|---|---|
| Not a known hop (public address) | The peer address itself; all forwarding headers are ignored, so a direct caller cannot spoof its address |
| Known hop | The rightmost `X-Forwarded-For` entry that is not itself an internal address, else the first parseable `CLIENT-IP` value, else the peer address |

Notes:

- `X-Forwarded-For` is read from the **right**. Each proxy appends the address of the peer it saw, so everything left
  of the entries added by Azure's own front end was chosen by the caller and can be forged; taking the leftmost entry
  (as earlier versions did) let any caller pick its own "source IP" just by sending the header. Internal addresses on
  the right (the platform's own hops) are skipped, and the first address that is not internal is used. An entry that is
  not a valid address stops the walk instead of trusting anything left of it, falling back to `CLIENT-IP` and then the
  peer. Several `X-Forwarded-For` header lines are treated as one chain, in order. If `X-Forwarded-For` and `CLIENT-IP`
  name different clients, a warning is logged (that would mean one of them is not being set by the platform).
- What the platform actually sends (observed on Azure Functions Flex Consumption): the peer is `127.0.0.1`,
  `X-Forwarded-For` and `Forwarded` are absent, and `CLIENT-IP` holds the caller's address as `ip:port`. Caller-supplied
  `CLIENT-IP`, `X-Forwarded-For` and `X-Original-For` headers are overwritten or stripped before they reach the app (a
  forged-header test returned the real address), so the `CLIENT-IP` fallback is trustworthy there. The `X-Forwarded-For`
  handling above is defence in depth for other hosting plans.
- Forwarding-header entries may be `ip`, `ip:port`, or `[ipv6]:port`; the port is stripped. Unparseable entries are skipped.
- `Forwarded`, `X-Original-For` and `X-Real-IP` are captured for diagnostics only and never used to choose the IP.
- IPv4-mapped IPv6 addresses (`::ffff:a.b.c.d`) are converted to the IPv4 address they represent, both for `myip`
  and for the source IP, so they update the `A` record rather than writing a bogus `AAAA` record.
- Addresses that cannot work in public DNS are refused with `911` and a warning in the log (naming the reason, the
  explicit address and the source IP), whether they arrive as `myip` or are taken from the source IP. Refused: IPv4
  unspecified/"this network" (`0/8`), private (`10/8`, `172.16/12`, `192.168/16`), carrier-grade NAT (`100.64/10`),
  loopback, link-local (`169.254/16`), IETF protocol assignments (`192.0.0/24`), benchmarking (`198.18/15`), multicast
  and reserved/broadcast (`224/4`, `240/4`); and any IPv6 address outside global unicast (`2000::/3`), which excludes
  unique-local, link-local, loopback and multicast. This stops, for example, a router reporting its LAN address, or the
  app seeing only an internal proxy hop because no forwarding header arrived, from silently publishing a broken
  record. The documentation ranges (`192.0.2/24`, `198.51.100/24`, `203.0.113/24`, `2001:db8::/32`, `3fff::/20`) are
  allowed by default because they can never misdirect traffic and the smoke test publishes addresses from them; set
  `ALLOW_DOCUMENTATION_ADDRESSES=false` (Bicep parameter `allowDocumentationAddresses`) to refuse them as well, in which
  case run the smoke test with `-TestIpv4` / `-TestIpv6` (real, publicly routable addresses). The policy lives in
  `Services/AddressPolicy.cs`.
- An explicit address that is not a valid IP address is rejected (`911`). If the source IP cannot be determined and
  no explicit address was given, the request fails the same way.
- If an explicit address differs from the resolved source IP, the update still proceeds (the client is authenticated
  and authorized) but a warning is logged. Only addresses of the same family are compared: a dual-stack client that
  reaches the app over IPv4 and reports its IPv6 address is normal and is not flagged.
- The resolution diagnostics are logged (peer, parsed address, raw forwarding headers) on every request,
  and warns if the source IP resolves to loopback, which usually means header forwarding is misconfigured.
  Setting `LOG_ALL_REQUEST_HEADERS_FOR_IP_DIAGNOSTICS=true` additionally logs every request header
  (any header whose name contains `auth`, `cookie`, `key`, `token`, `secret`, `password` or `credential` is redacted,
  e.g. `Authorization`, `Proxy-Authorization`, `Cookie`, `X-Functions-Key`, `X-Api-Key`). Request-derived values in these log
  lines have control characters (including line breaks) replaced with `_`, so callers cannot forge log entries.
  The query string is never logged. This logging is implemented in `Services/IpDiagnosticsLog.cs`; request-derived
  values are sanitized by `Services/LogSanitizer.cs`.

## Response contract (`/api/nic/update`)

Responses are plain text per DynDNS v2 specification:

| Body | HTTP status | Meaning |
|---|---|---|
| `good <ip>` | 200 | Record written |
| `nochg <ip>` | 200 | Record already held this address and TTL; nothing was written |
| `badauth` | 401 | Credentials missing or invalid |
| `nohost` | 200 | FQDN not resolvable to a configured zone/record, or record not authorized |
| `911` | 200 | Server-side error (app-setting misconfiguration, unresolvable IP, or DNS update failure) |
| `911` | 503 | Configuration file (`config/dyndns.json`) missing, unreadable or malformed |

> **Note**: `nohost` is returned for both missing and unauthorized records to avoid leaking information about configured zones.

> **Note**: Before writing, the app reads the existing record set. If it already contains exactly the requested address
> and the zone's configured TTL, no write is made and the response is `nochg <ip>`; otherwise (different address, different
> TTL, extra addresses, or no record yet) the record set is written and the response is `good <ip>`. Clients that report
> their address every few minutes therefore cause almost no Azure DNS writes.

### Prerequisites in `dyndns.json`

Ensure the client entry in `config/dyndns.json` includes an `allowedRecords` entry for the zone and record name
corresponding to the FQDN you are updating:

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
  - `Functions/DyndnsUpdateFunction.cs` - HTTP entrypoint for `/api/nic/update` (DynDNS v2 wire format only)
  - `Services/DdnsUpdateCoordinator.cs` - protocol-neutral update pipeline (config, auth, zone/IP resolution, DNS write)
  - `Services/AuthService.cs` - authentication + authorization checks
  - `Services/FqdnResolver.cs` - FQDN to zone/record resolution
  - `Services/IpResolver.cs` - source/explicit IP handling
  - `Services/IpDiagnosticsLog.cs`, `Services/LogSanitizer.cs` - IP diagnostics logging and log-forging protection
  - `Services/DnsUpdateService.cs` - Azure DNS SDK update logic
  - `Services/ConfigProvider.cs` - reads DDNS config JSON
  - `config/dyndns.json` - sample DDNS configuration
  - `local.settings.json.example` - local app settings template
- `tests/AzureDdns.FunctionApp.Tests` - xUnit tests
- `scripts/smoke-test-dyndns.ps1` - post-deployment smoke test for end-to-end DDNS validation
- `unifi-client/` - Python DDNS client for Unifi gateways (see [`unifi-client/README.md`](unifi-client/README.md))
  - `arkane-ddns-client.py` - main update script supporting IPv4 and IPv6
  - `arkane-ddns-client.conf.example` - configuration template
  - `arkane-ddns-client.service` - systemd service unit
  - `arkane-ddns-client.timer` - systemd timer unit (runs every 5 minutes)
  - `install.sh` - deployment helper script for Unifi gateways
  - `README.md` - setup, configuration, and troubleshooting guide
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
| `AzureWebJobsStorage__accountName`, `AzureWebJobsStorage__credential` | Required in Azure | Identity-based Functions host storage: the storage account name, and `managedidentity`. Set by `infra/main.bicep`; there is no connection string or account key. Locally, `AzureWebJobsStorage` (e.g. `UseDevelopmentStorage=true`) is still used. |
| `LOG_ALL_REQUEST_HEADERS_FOR_IP_DIAGNOSTICS` | Optional | Logs all request headers (sensitive ones redacted) on both endpoints for IP diagnostics; default is `false`. See [Source IP resolution](#source-ip-resolution). |
| `AZURE_CLIENT_ID` | Optional | Client ID of a *user-assigned* managed identity to use for Azure DNS. Leave unset to use the system-assigned identity (what `infra/main.bicep` creates). Ignored off Azure. |
| `ALLOW_DOCUMENTATION_ADDRESSES` | Optional | `true` (default) allows publishing documentation-range addresses (RFC 5737/3849/9637), which the smoke test uses; `false` refuses them like other unroutable addresses. See [Source IP resolution](#source-ip-resolution). |

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
6. Run function locally (from app project folder) and send a test request to `/api/nic/update` (for example `curl -u <client>:<key> "http://localhost:7071/api/nic/update?hostname=<fqdn>&myip=<ip>"`).

## Manual deployment from a repository clone

Use these steps to deploy. Deployment is intentionally manual: the repository's only GitHub Actions workflow
(`.github/workflows/ci.yml`) builds and tests pull requests, checks that the Bicep templates compile, and does not deploy.

### Prerequisites

1. Install required tooling:
   - Azure CLI (`az`)
   - .NET 10 SDK
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

1. Confirm the Function App is running and responds on `/api/nic/update` (an unauthenticated request should return `badauth`).
2. Verify managed identity role assignments are present as expected (including optional DNS zone-scoped RBAC when enabled).
3. Run the smoke test:
   - `./scripts/smoke-test-dyndns.ps1 -FunctionBaseUrl 'https://<function-app-name>.azurewebsites.net' -ClientName '<client>' -Zone '<zone>' -Name '<record>'`
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
3. Run the app locally and test the `/api/nic/update` endpoint with your configured client credentials.

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

Use `scripts/smoke-test-dyndns.ps1` after deployment to perform an end-to-end functional validation of the Function App and Azure DNS integration.

What the script does:

1. Accepts the target Function App URL plus the client, zone, and record name to test.
2. Generates one random IPv4 address from the RFC5737 documentation ranges.
3. Generates one random IPv6 address from the RFC3849 documentation range.
4. Sends an IPv4 update request and verifies the DynDNS `good <ip>` response.
5. Queries an authoritative name server for the target zone and waits for the `A` record to match the generated IPv4 address.
6. Sends an IPv6 update request and verifies the DynDNS `good <ip>` response.
7. Queries an authoritative name server again and waits for the `AAAA` record to match the generated IPv6 address.
8. Confirms that the earlier `A` record value remains unchanged to validate `A`/`AAAA` independence.
9. Repeats the IPv4 update and verifies the response is `nochg <ip>` (the record already held that address).

Parameters:

- `-FunctionBaseUrl` - Base URL of the deployed Function App, with or without `/api/nic/update`
- `-ClientName` - DDNS client name configured in `config/dyndns.json`
- `-ClientKey` - raw DDNS client key; if omitted, the script uses `AZURE_DDNS_CLIENT_KEY`
- `-Zone` - DNS zone to test
- `-Name` - relative record name to test (`@` for zone apex)
- `-DnsTimeoutSeconds` - optional DNS propagation wait timeout; default `120`
- `-DnsPollIntervalSeconds` - optional poll interval between authoritative DNS checks; default `5`
- `-TestIpv4` / `-TestIpv6` - optional addresses to publish instead of random documentation-range ones; required when the deployment sets `ALLOW_DOCUMENTATION_ADDRESSES=false`

Example:

```powershell
$env:AZURE_DDNS_CLIENT_KEY = 'your-raw-client-key'
.\scripts\smoke-test-dyndns.ps1 `
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
4. `src/AzureDdns.FunctionApp/Functions/DyndnsUpdateFunction.cs` (DynDNS v2 wire format)
5. `src/AzureDdns.FunctionApp/Services/DdnsUpdateCoordinator.cs` (the actual update pipeline)
