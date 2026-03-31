# Copilot Instructions

## Project goal
- Build and maintain an Azure Functions Flex Consumption application that provides authenticated dynamic DNS updates for Azure DNS zones.

## Solution context
- Use C# and .NET 8 isolated worker for the Function App.
- Treat Azure DNS zones as existing resources in one subscription and one resource group.
- Preserve the contract `GET /api/update?client=<name>&key=<raw-key>&zone=<zone>&name=<record>[&ip=<address>]` unless requirements change.
- Keep `A` and `AAAA` updates independent.

## Coding guidance
- Prefer small, testable service classes over large function methods.
- Do not log raw client keys.
- Keep configuration strongly typed.
- Prefer simpler deployment/configuration approaches over runtime-refreshable configuration when update frequency is low and complexity cost is high.
- Use managed identity and Azure SDK clients rather than embedded credentials.
- Make minimal, focused changes and preserve existing conventions.
- Ensure documentation and code comments are thorough and written for long-gap maintainability (future re-entry after 6-12 months).

## Operational guidance
- Plain-text responses are preferred for DDNS client compatibility.
- Source IP fallback should remain available when `ip` is omitted.
- Per-client authorization must support exact zone and record pairs and wildcard record names within a zone.
