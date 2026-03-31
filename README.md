# azure-ddns

Azure Functions-based dynamic DNS updater for Azure DNS zones.

## Overview

This repository contains the initial scaffold for a Flex Consumption Azure Function App that will:

- accept authenticated HTTP GET requests from DDNS clients such as OpenWRT
- resolve an IPv4 or IPv6 address from the request or caller source IP
- authorize updates per client, zone, and record name
- create or update Azure DNS `A` and `AAAA` record sets without affecting the opposite type

## Planned endpoint

`GET /api/update?client=<name>&key=<raw-key>&zone=<zone>&name=<record>[&ip=<address>]`

## Repository layout

- `src/AzureDdns.FunctionApp` - Azure Functions app
- `docs/deployment-plan.md` - deployment plan and operational checklist
- `.github/copilot-instructions.md` - repository instructions for future changes

## Configuration

The app expects:

- `DNS_SUBSCRIPTION_ID`
- `DNS_RESOURCE_GROUP`
- `CONFIG_PATH`

A sample dynamic DNS configuration file is included at `src/AzureDdns.FunctionApp/config/dyndns.json`.

## Status

This commit provides the solution scaffold, baseline files, and deployment guidance. The production DDNS behavior is the next implementation step.
