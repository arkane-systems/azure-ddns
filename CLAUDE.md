# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## What this is

Azure Functions (.NET 8 isolated worker, Flex Consumption/Linux) app that exposes authenticated dynamic-DNS HTTP endpoints and writes `A`/`AAAA` records to existing Azure DNS zones via the Azure SDK and managed identity. `README.md` is detailed (request flows, response contracts, config reference, manual deployment); `docs/deployment-plan.md` and `docs/quick-ops.md` cover deploy/ops.

## Commands

```bash
dotnet build
dotnet test
dotnet test --filter "FullyQualifiedName~AuthServiceTests"        # one test class
dotnet test --filter "FullyQualifiedName~AuthServiceTests.SomeTest" # one test
dotnet publish src/AzureDdns.FunctionApp/AzureDdns.FunctionApp.csproj -c Release -o out/functionapp
```

Local run: copy `src/AzureDdns.FunctionApp/local.settings.json.example` to `local.settings.json`, set `DNS_SUBSCRIPTION_ID`, `DNS_RESOURCE_GROUP`, `CONFIG_PATH`, then `func start` from the app project folder.

Post-deploy validation: `scripts/smoke-test.ps1` (`/api/update`) and `scripts/smoke-test-dyndns.ps1` (`/api/nic/update`). Infra: `infra/main.bicep` (+ `modules/dns-zone-rbac.bicep`), deployed with `az deployment group create` or `azd` (`azure.yaml`). `infra/main.json` is an old compiled ARM copy of the Bicep; nothing references it and it has drifted from `main.bicep`, so treat the Bicep as the source of truth.

## Architecture

Two HTTP functions share one service layer (`src/AzureDdns.FunctionApp/Services`):

- `Functions/UpdateDnsFunction.cs` — `GET /api/update?client&key&zone&name[&ip]` (OpenWRT-style). Plain-text `OK:`/`ERROR:` responses with 400/401/403/502/500.
- `Functions/DyndnsUpdateFunction.cs` — `GET /api/nic/update?hostname[&myip]` with HTTP Basic auth (DynDNS v2, for Unifi/ddclient). Responses are `good <ip>`/`badauth`/`nohost`/`911`; `nohost` is deliberately returned for both unknown and unauthorized records to avoid leaking zone config.
- Services: `AuthService` (SHA-256 key-hash check + per-client zone/record authorization, `*` wildcard record name, `@` for apex), `FqdnResolver` (longest-suffix match of FQDN to a configured zone; DynDNS endpoint only), `IpResolver` (explicit IP else connection source IP), `DnsUpdateService` (Azure DNS SDK), `ConfigProvider` (reads the config file).
- Both endpoints share the same `config/dyndns.json` (zones with TTL; clients with `keyHash` and `allowedRecords`). Config is a packaged file by design — changing clients/keys means republishing, not runtime refresh. Typed config models are in `config/DyndnsConfig.cs`.
- IP resolution (`IpResolver`) is not just the raw connection address: when the remote address is loopback/private/link-local (i.e. a trusted proxy hop, as behind Azure's front end), the source IP is taken from `X-Forwarded-For`, then `CLIENT-IP`; otherwise from the connection. An explicit `ip`/`myip` wins but a mismatch with the source IP is logged. Setting `LOG_ALL_REQUEST_HEADERS_FOR_IP_DIAGNOSTICS=true` logs all headers (sensitive ones redacted) from both endpoints. That logging lives in `Services/IpDiagnosticsLog.cs` and must not log the query string (it holds the raw key on `/api/update`).
- A missing, unreadable or malformed config file makes `FileConfigProvider` throw `ConfigurationUnavailableException`; both functions log it and return HTTP 503 (`ERROR: configuration unavailable` on `/api/update`, body `911` on `/api/nic/update`). A valid-but-empty file (`{}`) is not an error and just fails authentication. `ttl` defaults to 300.
- Both endpoints authenticate and authorize *before* checking the zone is configured (`/api/update`: 400 `zone not configured` only for an authorized caller), so configured zone names are not exposed to unauthenticated callers. Zone lookup in config goes through `DyndnsConfig.TryGetZone` for both endpoints, which ignores case, surrounding whitespace and a trailing dot on either side. `AuthService.IsRecordAuthorized` compares `allowedRecords[].zone` the same way (via `DyndnsConfig.NormalizeZoneName`).
- CI is a single workflow, `.github/workflows/ci.yml`, that has two jobs on every pull request (and manual dispatch): `test` builds and runs the tests, and `bicep` compiles `infra/main.bicep` with `az bicep build`. It targets the test project, not the `.slnx`, because `.slnx` needs a newer SDK than the .NET 8 one the workflow installs. Deployment is deliberately manual CLI, not CI (see README).
- An update touches only the record type matching the IP family (IPv4 → `A`, IPv6 → `AAAA`); the two must stay independent.
- Tests are xUnit (67 passing at last run; the function tests use stub services, so no Azure access is needed) in `tests/AzureDdns.FunctionApp.Tests`, one file per service/function.
- `unifi-client/` is a separate Python client + systemd units for Unifi gateways (own README); it is not part of the .NET solution (`azure-ddns.slnx`).

## Conventions

- Keep the `/api/update` contract unchanged unless requirements change; keep plain-text responses and source-IP fallback when `ip` is omitted.
- Small, testable service classes over large function methods; strongly typed config; managed identity, no embedded credentials.
- Never log raw client keys.
- Prefer simple deploy-time configuration over runtime-refreshable config.
- Comments/docs should be thorough enough for re-entry after 6–12 months; when editing the README, add material without removing existing content.
