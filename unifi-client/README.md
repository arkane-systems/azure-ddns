# Arkane DDNS Client for Unifi Gateways

This directory contains a Python-based DDNS client for Unifi Cloud Gateways (and similar Linux-based edge routers) that supports both IPv4 and IPv6 address updates. It works around the limitation that built-in Unifi DynDNS support does not handle IPv6.

## Overview

The client:
1. **Discovers public IPs** by parsing the WAN interface (e.g., `eth1`) for global unicast IPv4 and IPv6 addresses
2. **Caches the last known IPs** in `/var/cache/arkane-ddns-client/cache.json`
3. **Detects changes** by comparing current addresses to cached values
4. **Updates DNS** by calling the Azure DDNS API (`/api/update` endpoint) when addresses change
5. **Runs on schedule** via a systemd timer (every 5 minutes by default)
6. **Logs minimally** to the system journal for troubleshooting

The client is configured via a single INI file and requires only Python 2.7 and `curl` (both available on Unifi gateways).

## Requirements

- **Python 2.7.18** or later (already available on Unifi gateways)
- **curl** (for making HTTP API calls; already available on Unifi gateways)
- **ip** command (for interface/address inspection; standard on Linux)
- **systemd** (for scheduling; standard on Unifi gateways)
- A working Azure DDNS function app endpoint with configured client credentials

## Quick Start

### 1. Prepare the Configuration File

Copy `arkane-ddns-client.conf.example` to `/usr/local/etc/arkane-ddns-client.conf` on your gateway and edit it:

```ini
[api]
endpoint = https://your-function-app.azurewebsites.net
client = my-gateway-client
key = your-raw-client-key
zone = example.com
record = home

[interface]
wan = eth1

[options]
enable_ipv4 = true
enable_ipv6 = true
debug = false
```

**Important**: Keep the config file permissions restricted:
```bash
chmod 600 /usr/local/etc/arkane-ddns-client.conf
```

### 2. Install Using the Helper Script

On your Unifi gateway:

```bash
sudo bash install.sh /path/to/unifi-client
```

This will:
- Copy the Python script to `/usr/local/bin/`
- Copy the config template to `/usr/local/etc/` (if not already present)
- Copy systemd units to `/etc/systemd/system/`
- Create the cache directory at `/var/cache/arkane-ddns-client/`

### 3. Configure Your API Endpoint and Credentials

Edit `/usr/local/etc/arkane-ddns-client.conf` with your:
- Azure DDNS function app endpoint
- Client name and raw key (from `config/dyndns.json` in the function app)
- Zone and record name(s) to update
- WAN interface name (usually `eth1`, but may vary)

### 4. Test the Script

Run the script manually to verify configuration:

```bash
sudo /usr/local/bin/arkane-ddns-client.py /usr/local/etc/arkane-ddns-client.conf
```

Check for errors or success in the output. Enable debug mode in the config to see more detail:

```bash
sudo journalctl -u arkane-ddns-client -f  # Follow live logs
```

### 5. Enable Automatic Execution

Once testing passes, enable the systemd timer:

```bash
sudo systemctl enable arkane-ddns-client.timer
sudo systemctl start arkane-ddns-client.timer
```

Verify it's running:

```bash
sudo systemctl status arkane-ddns-client.timer
sudo systemctl list-timers arkane-ddns-client.timer
```

## Configuration Reference

### `[api]` Section

| Option | Required | Description |
|--------|----------|-------------|
| `endpoint` | Yes | Base URL of Azure DDNS function app (e.g., `https://my-func.azurewebsites.net`). Path `/api/update` is appended by the client. |
| `client` | Yes | Client name configured in the function app's `config/dyndns.json`. |
| `key` | Yes | Raw (unhashed) client key. Must match the configured client's key. **Keep this secure!** |
| `zone` | Yes | DNS zone name (e.g., `example.com`). |
| `record` | Yes | Record name relative to the zone (e.g., `home` for `home.example.com`, or `@` for zone apex). |

### `[interface]` Section

| Option | Default | Description |
|--------|---------|-------------|
| `wan` | `eth1` | Name of the WAN interface to monitor for public IP addresses. |

### `[options]` Section

| Option | Default | Description |
|--------|---------|-------------|
| `enable_ipv4` | `true` | If `true`, update IPv4 (`A` record) when addresses change. |
| `enable_ipv6` | `true` | If `true`, update IPv6 (`AAAA` record) when addresses change. |
| `debug` | `false` | If `true`, log verbose debug information to the journal. Useful for troubleshooting. |

## How It Works

### IP Discovery

The script uses `ip addr show <interface>` to enumerate addresses on the WAN interface. It extracts:
- **IPv4**: First global-scope address (not link-local, not loopback)
- **IPv6**: First global-scope address that is not link-local (`fe80::`) and not from the documentation range (`2001:db8::`)

If multiple global addresses exist, only the first of each family is used. This is suitable for most Unifi gateway deployments.

### Change Detection

The script caches the last-known IPv4 and IPv6 addresses in `/var/cache/arkane-ddns-client/cache.json`. On each run:
1. Read current addresses from the interface
2. Load the cache
3. Compare: if different and enabled, call the API
4. Update the cache with current values

### API Calls

When an address changes, the script calls the Azure DDNS `/api/update` endpoint with query parameters:

```
GET https://<endpoint>/api/update?client=<name>&key=<key>&zone=<zone>&name=<record>&ip=<ip>
```

The API response is checked for `OK:` (success) or `ERROR:` (failure). Only successful updates modify the cache.

### Logging

By default, the script logs minimal information to the system journal:
- **Info**: "Successfully updated <family> record <record> to <ip>"
- **Error**: Configuration errors, API failures, interface errors

With `debug = true`, additional debug entries show:
- Configuration loaded
- IP discovery results
- Cache read/write operations
- API calls (with key redacted)
- Why no update occurred

View logs:

```bash
# Show recent logs
sudo journalctl -u arkane-ddns-client -n 20

# Follow live logs
sudo journalctl -u arkane-ddns-client -f

# Show debug logs (if enabled in config)
sudo journalctl -u arkane-ddns-client --priority=debug
```

## Scheduling

The systemd timer (`arkane-ddns-client.timer`) is configured to run the script:
- Immediately on boot (after 30 seconds)
- Every 5 minutes thereafter

The timer includes a randomized delay (±60 seconds) to avoid thundering herd issues if multiple gateways run simultaneously.

View schedule information:

```bash
sudo systemctl list-timers arkane-ddns-client.timer
sudo systemctl show arkane-ddns-client.timer -p AllowPersistentTimer,Persistent
```

## Troubleshooting

### Script doesn't run automatically

Check if the timer is enabled and active:

```bash
sudo systemctl is-enabled arkane-ddns-client.timer  # Should print "enabled"
sudo systemctl is-active arkane-ddns-client.timer   # Should print "active"
```

If not active, enable it:

```bash
sudo systemctl enable --now arkane-ddns-client.timer
```

### Manual test fails

Run with debug enabled:

```bash
# Edit config temporarily
sudo vi /usr/local/etc/arkane-ddns-client.conf
# Set: debug = true

# Run manually
sudo /usr/local/bin/arkane-ddns-client.py

# Check output
sudo journalctl -u arkane-ddns-client -n 50
```

Common issues:
- **API endpoint unreachable**: Check function app URL and network connectivity
- **"No valid IPv4 address found"**: Check interface name and ensure it has a global-scope address (`ip addr show eth1`)
- **Authentication failed (API returns `ERROR`)**: Verify client name, key, zone, and record are correct in both config file and function app config
- **curl not found**: Install curl (`apt-get install curl`) or check `$PATH`

### No IPv6 address detected

Ensure your ISP and gateway provide IPv6 connectivity:

```bash
# Check for global IPv6 addresses
ip addr show eth1 | grep "scope global"

# If none, you may not have IPv6 connectivity
# Disable IPv6 in config: enable_ipv6 = false
```

### Cache file issues

The cache is stored in `/var/cache/arkane-ddns-client/cache.json`. If you suspect stale cache:

```bash
# View cache contents
sudo cat /var/cache/arkane-ddns-client/cache.json

# Clear cache (addresses will be treated as "changed" on next run)
sudo rm /var/cache/arkane-ddns-client/cache.json
```

## Manual API Testing

If you want to test the API endpoint directly without the client:

```bash
# IPv4 update
curl "https://your-func.azurewebsites.net/api/update?client=my-client&key=my-key&zone=example.com&name=home&ip=203.0.113.42"

# IPv6 update
curl "https://your-func.azurewebsites.net/api/update?client=my-client&key=my-key&zone=example.com&name=home&ip=2001:db8::1"

# Expected success response: "OK: ..."
# Expected auth failure: "ERROR: ..."
```

## Performance & Resource Usage

The script is lightweight:
- Runs in ~1 second on typical hardware
- Uses minimal memory (~5 MB)
- Cache file is <1 KB
- One HTTP request per changed address (typically one request every few days or less)

## Security Notes

1. **Config file permissions**: Always keep `/usr/local/etc/arkane-ddns-client.conf` readable only by root (`chmod 600`). It contains your raw API key.
2. **Key in logs**: The script never logs the raw key, but take care not to enable system-wide debugging or share journal output carelessly.
3. **HTTPS only**: The script always uses HTTPS for API calls. Do not use unencrypted HTTP endpoints in production.

## Architecture & Future Enhancements

The current implementation is intentionally minimal to suit Unifi gateways' limited resources and to avoid dependencies beyond Python 2.7 and standard utilities. Possible future enhancements (not currently planned):
- Support for multiple records in one config file
- IPv6 prefix delegation (updating entire subnets)
- Fallback to HTTPS POST if GET causes issues
- Systemd watchdog support for health monitoring

## Reporting Issues

If you encounter problems:
1. Collect logs: `sudo journalctl -u arkane-ddns-client > /tmp/ddns-logs.txt`
2. Enable debug: Set `debug = true` in config and run once
3. Share the logs and your config (with sensitive values redacted) in an issue
