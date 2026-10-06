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

Post-deploy validation: `scripts/smoke-test.ps1` (`/api/update`) and `scripts/smoke-test-dyndns.ps1` (`/api/nic/update`). Infra: `infra/main.bicep` (+ `modules/dns-zone-rbac.bicep`), deployed with `az deployment group create` or `azd` (`azure.yaml`). `infra/main.json` is the compiled ARM output of the Bicep.

## Architecture

Two HTTP functions share one service layer (`src/AzureDdns.FunctionApp/Services`):

- `Functions/UpdateDnsFunction.cs` — `GET /api/update?client&key&zone&name[&ip]` (OpenWRT-style). Plain-text `OK:`/`ERROR:` responses with 400/401/403/502/500.
- `Functions/DyndnsUpdateFunction.cs` — `GET /api/nic/update?hostname[&myip]` with HTTP Basic auth (DynDNS v2, for Unifi/ddclient). Responses are `good <ip>`/`badauth`/`nohost`/`911`; `nohost` is deliberately returned for both unknown and unauthorized records to avoid leaking zone config.
- Services: `AuthService` (SHA-256 key-hash check + per-client zone/record authorization, `*` wildcard record name, `@` for apex), `FqdnResolver` (longest-suffix match of FQDN to a configured zone; DynDNS endpoint only), `IpResolver` (explicit IP else connection source IP), `DnsUpdateService` (Azure DNS SDK), `ConfigProvider` (reads the config file).
- Both endpoints share the same `config/dyndns.json` (zones with TTL; clients with `keyHash` and `allowedRecords`). Config is a packaged file by design — changing clients/keys means republishing, not runtime refresh. Typed config models are in `config/DyndnsConfig.cs`.
- An update touches only the record type matching the IP family (IPv4 → `A`, IPv6 → `AAAA`); the two must stay independent.
- Tests are xUnit in `tests/AzureDdns.FunctionApp.Tests`, one file per service/function.
- `unifi-client/` is a separate Python client + systemd units for Unifi gateways (own README); it is not part of the .NET solution (`azure-ddns.slnx`).

## Conventions (from `.github/copilot-instructions.md`)

- Keep the `/api/update` contract unchanged unless requirements change; keep plain-text responses and source-IP fallback when `ip` is omitted.
- Small, testable service classes over large function methods; strongly typed config; managed identity, no embedded credentials.
- Never log raw client keys.
- Prefer simple deploy-time configuration over runtime-refreshable config.
- Comments/docs should be thorough enough for re-entry after 6–12 months; when editing the README, add material without removing existing content.
