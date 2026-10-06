#!/usr/bin/env python
# -*- coding: utf-8 -*-
"""
arkane-ddns-client: DDNS client for Unifi gateways with IPv4/IPv6 support.

Discovers public IPv4 and IPv6 addresses from a WAN interface, detects changes,
and calls the Azure DDNS API (DynDNS v2 endpoint, HTTP Basic auth) to update records.
"""

import os
import sys
import subprocess
import json
import re
import socket
import syslog
import urllib
from ConfigParser import SafeConfigParser

CACHE_DIR = '/var/cache/arkane-ddns-client'
CACHE_FILE = os.path.join(CACHE_DIR, 'cache.json')
DEFAULT_INTERFACE = 'eth1'
API_PATH = '/api/nic/update'
CURL_TIMEOUT_SECONDS = '30'


def log(level, message):
    """Log to syslog with optional debug support."""
    priority_map = {
        'debug': syslog.LOG_DEBUG,
        'info': syslog.LOG_INFO,
        'error': syslog.LOG_ERR,
    }
    syslog.syslog(priority_map.get(level, syslog.LOG_INFO), message)


def debug(message):
    """Log debug message if DEBUG is enabled."""
    if ENABLE_DEBUG:
        log('debug', message)


def ensure_cache_dir():
    """Create cache directory if it doesn't exist."""
    if not os.path.exists(CACHE_DIR):
        try:
            os.makedirs(CACHE_DIR, 0o700)
            debug('Created cache directory: {}'.format(CACHE_DIR))
        except OSError as e:
            log('error', 'Failed to create cache directory {}: {}'.format(CACHE_DIR, e))
            return False
    return True


def read_cache():
    """Read cached IPs from file. Returns dict with 'ipv4', 'ipv6', 'timestamp'."""
    if not os.path.exists(CACHE_FILE):
        debug('Cache file does not exist: {}'.format(CACHE_FILE))
        return {}
    
    try:
        with open(CACHE_FILE, 'r') as f:
            cache = json.load(f)
            debug('Loaded cache: {}'.format(cache))
            return cache
    except (IOError, ValueError) as e:
        log('error', 'Failed to read cache file {}: {}'.format(CACHE_FILE, e))
        return {}


def write_cache(cache, previous_cache=None):
    """Write cached IPs to file.

    The cache directory is recreated if it has gone missing (it is disposable: losing it only costs one
    redundant update). The write is skipped when nothing changed since `previous_cache` was read, which
    avoids rewriting flash storage every few minutes, and is done via a temporary file and rename so a
    crash can never leave a truncated cache behind.
    """
    if previous_cache is not None and cache == previous_cache and os.path.exists(CACHE_FILE):
        debug('Cache unchanged; not rewriting')
        return

    if not ensure_cache_dir():
        return

    tmp_file = CACHE_FILE + '.tmp'
    try:
        with open(tmp_file, 'w') as f:
            json.dump(cache, f)
        os.rename(tmp_file, CACHE_FILE)
        debug('Wrote cache: {}'.format(cache))
    except (IOError, OSError) as e:
        log('error', 'Failed to write cache file {}: {}'.format(CACHE_FILE, e))


# Address flags (as printed by `ip addr`) that mean an address must not be published: privacy-extension
# temporary addresses change constantly and are not the host's stable address, and deprecated/tentative/
# dadfailed addresses are not (or are no longer) valid for new connections.
UNPUBLISHABLE_IPV6_FLAGS = ('temporary', 'deprecated', 'tentative', 'dadfailed')


def ipv6_skip_reason(addr, flags):
    """Return why an IPv6 address must not be published, or None if it should be.

    Only a stable, ISP-assigned global unicast address (GUA, 2000::/3) is published. That excludes
    unique-local (fc00::/7), link-local (fe80::/10), loopback and multicast addresses, address-flag cases
    such as temporary privacy addresses, and the documentation prefixes. (Python 2.7 has no ipaddress module,
    so the first two groups are examined directly.)
    """
    for flag in UNPUBLISHABLE_IPV6_FLAGS:
        if flag in flags:
            return 'flagged {}'.format(flag)

    groups = addr.lower().split(':')
    try:
        first = int(groups[0], 16) if groups[0] else 0
        second = int(groups[1], 16) if len(groups) > 1 and groups[1] else 0
    except ValueError:
        return 'not a parseable address'

    if (first & 0xe000) != 0x2000:
        return 'not global unicast (2000::/3), e.g. unique-local or link-local'
    if first == 0x2001 and second == 0x0db8:
        return 'documentation range 2001:db8::/32'
    if first == 0x3fff and second < 0x1000:
        return 'documentation range 3fff::/20'
    return None


def get_interface_addresses(interface):
    """Extract public IPv4 and IPv6 addresses from interface using 'ip addr' output."""
    ipv4 = None
    ipv6 = None
    
    try:
        output = subprocess.check_output(['ip', 'addr', 'show', interface],
                                        stderr=subprocess.STDOUT)
        debug('ip addr output for {}: {}'.format(interface, output))
    except subprocess.CalledProcessError as e:
        log('error', 'Failed to query interface {}: {}'.format(interface, e))
        return None, None
    except OSError as e:
        log('error', 'Command "ip" not found or failed: {}'.format(e))
        return None, None
    
    # Parse lines looking for global scope addresses
    for line in output.split('\n'):
        line = line.strip()
        
        # IPv4: look for "inet <address>/prefix scope global"
        ipv4_match = re.search(r'inet\s+(\S+)/\d+\s+.*scope\s+global', line)
        if ipv4_match and ipv4 is None:
            ipv4 = ipv4_match.group(1)
            debug('Found global IPv4: {}'.format(ipv4))
            continue
        
        # IPv6: look for "inet6 <address>/prefix scope global [flags...]"
        ipv6_match = re.search(r'inet6\s+(\S+)/\d+\s+.*scope\s+global(.*)$', line)
        if ipv6_match and ipv6 is None:
            addr = ipv6_match.group(1)
            reason = ipv6_skip_reason(addr, ipv6_match.group(2).split())
            if reason:
                debug('Skipped IPv6 {}: {}'.format(addr, reason))
                continue
            ipv6 = addr
            debug('Found global IPv6: {}'.format(ipv6))
            continue
    
    return ipv4, ipv6


def build_hostname(zone, record):
    """Return the fully-qualified hostname for a zone and record ('@' means the zone apex)."""
    if record in ('', '@'):
        return zone
    return '{}.{}'.format(record, zone)


def curl_quote(value):
    """Quote a value for a curl config file (backslash and double quote are escaped)."""
    return '"{}"'.format(value.replace('\\', '\\\\').replace('"', '\\"'))


def call_api(api_base_url, client, key, zone, record, ip_address, ip_version):
    """Call the DDNS API (DynDNS v2 protocol) with an IP update using curl.

    The key is sent as the HTTP Basic auth password, and curl reads its settings from stdin
    (-K -), so the key never appears in the process list or in any URL.
    """
    hostname = build_hostname(zone, record)
    url = '{endpoint}{path}?hostname={hostname}&myip={ip}'.format(
        endpoint=api_base_url.rstrip('/'),
        path=API_PATH,
        hostname=urllib.quote(hostname, safe=''),
        ip=urllib.quote(ip_address, safe=''))

    debug('Calling API: {} (as client {})'.format(url, client))

    curl_config = 'url = {}\nuser = {}\n'.format(
        curl_quote(url), curl_quote('{}:{}'.format(client, key)))

    try:
        process = subprocess.Popen(
            ['curl', '-s', '--proto', '=https', '--connect-timeout', '10',
             '--max-time', CURL_TIMEOUT_SECONDS, '-K', '-'],
            stdin=subprocess.PIPE, stdout=subprocess.PIPE, stderr=subprocess.STDOUT)
        response, _ = process.communicate(curl_config)
    except OSError as e:
        log('error', 'Command "curl" not found or failed: {}'.format(e))
        return False

    response = response.strip()
    debug('API response: {}'.format(response))

    # DynDNS v2: "good <ip>" (changed) or "nochg <ip>" (unchanged) mean success; anything else is a failure
    # (badauth, nohost, 911, or a curl error message).
    if process.returncode == 0 and (response.startswith('good') or response.startswith('nochg')):
        log('info', 'Successfully updated {} record {} to {}'.format(
            ip_version, hostname, ip_address))
        return True

    log('error', 'API returned error for {} update of {} (curl exit {}): {}'.format(
        ip_version, hostname, process.returncode, response))
    return False


def main():
    """Main entry point."""
    global ENABLE_DEBUG
    
    # Parse configuration
    config = SafeConfigParser()
    if not config.read(CONFIG_FILE):
        log('error', 'Failed to read configuration file: {}'.format(CONFIG_FILE))
        sys.exit(1)
    
    # Get config values
    try:
        api_endpoint = config.get('api', 'endpoint')
        client_name = config.get('api', 'client')
        client_key = config.get('api', 'key')
        zone = config.get('api', 'zone')
        record = config.get('api', 'record')
        wan_interface = config.get('interface', 'wan') if config.has_option('interface', 'wan') else DEFAULT_INTERFACE
        enable_ipv4 = config.getboolean('options', 'enable_ipv4') if config.has_option('options', 'enable_ipv4') else True
        enable_ipv6 = config.getboolean('options', 'enable_ipv6') if config.has_option('options', 'enable_ipv6') else True
        ENABLE_DEBUG = config.getboolean('options', 'debug') if config.has_option('options', 'debug') else False
    except Exception as e:
        log('error', 'Configuration error: {}'.format(e))
        sys.exit(1)
    
    debug('Loaded config: endpoint={}, client={}, zone={}, record={}, interface={}'.format(
        api_endpoint, client_name, zone, record, wan_interface))
    
    # Ensure cache directory exists
    # The cache is disposable: if its directory cannot be created, carry on without one (every run then
    # simply reports the current address; the server answers "nochg" when nothing has changed).
    if not ensure_cache_dir():
        log('error', 'Continuing without a cache; the cache directory could not be created')
    
    # Get current IPs
    current_ipv4, current_ipv6 = get_interface_addresses(wan_interface)
    debug('Current addresses: IPv4={}, IPv6={}'.format(current_ipv4, current_ipv6))
    
    # Load cached IPs
    cache = read_cache()
    cached_ipv4 = cache.get('ipv4')
    cached_ipv6 = cache.get('ipv6')
    
    debug('Cached addresses: IPv4={}, IPv6={}'.format(cached_ipv4, cached_ipv6))
    
    # Determine what needs updating
    new_cache = {}
    updated = False
    
    # Handle IPv4
    if enable_ipv4:
        if current_ipv4 and current_ipv4 != cached_ipv4:
            debug('IPv4 changed: {} -> {}'.format(cached_ipv4, current_ipv4))
            if call_api(api_endpoint, client_name, client_key, zone, record, current_ipv4, 'IPv4'):
                new_cache['ipv4'] = current_ipv4
                updated = True
            else:
                # Keep cached value on API failure so we retry next time
                new_cache['ipv4'] = cached_ipv4
        elif current_ipv4:
            new_cache['ipv4'] = current_ipv4
        else:
            debug('No valid IPv4 address found')
    
    # Handle IPv6
    if enable_ipv6:
        if current_ipv6 and current_ipv6 != cached_ipv6:
            debug('IPv6 changed: {} -> {}'.format(cached_ipv6, current_ipv6))
            if call_api(api_endpoint, client_name, client_key, zone, record, current_ipv6, 'IPv6'):
                new_cache['ipv6'] = current_ipv6
                updated = True
            else:
                # Keep cached value on API failure so we retry next time
                new_cache['ipv6'] = cached_ipv6
        elif current_ipv6:
            new_cache['ipv6'] = current_ipv6
        else:
            debug('No valid IPv6 address found')
    
    # Write updated cache
    write_cache(new_cache, cache)
    
    if updated:
        log('info', 'Update complete: changes detected and API calls made')
    else:
        debug('No changes detected')
    
    sys.exit(0)


if __name__ == '__main__':
    syslog.openlog('arkane-ddns-client', syslog.LOG_PID, syslog.LOG_USER)
    
    # Get config file path from argument or environment
    CONFIG_FILE = sys.argv[1] if len(sys.argv) > 1 else '/data/arkane-ddns-client/arkane-ddns-client.conf'
    ENABLE_DEBUG = False
    
    try:
        main()
    except KeyboardInterrupt:
        log('info', 'Interrupted')
        sys.exit(0)
    except Exception as e:
        log('error', 'Unhandled exception: {}'.format(e))
        import traceback
        debug('Traceback: {}'.format(traceback.format_exc()))
        sys.exit(1)
