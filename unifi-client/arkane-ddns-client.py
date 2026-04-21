#!/usr/bin/env python
# -*- coding: utf-8 -*-
"""
arkane-ddns-client: DDNS client for Unifi gateways with IPv4/IPv6 support.

Discovers public IPv4 and IPv6 addresses from a WAN interface, detects changes,
and calls the Azure DDNS API endpoint to update records.
"""

import os
import sys
import subprocess
import json
import re
import socket
import syslog
from ConfigParser import SafeConfigParser

CACHE_DIR = '/var/cache/arkane-ddns-client'
CACHE_FILE = os.path.join(CACHE_DIR, 'cache.json')
DEFAULT_INTERFACE = 'eth1'
API_ENDPOINT_TEMPLATE = '{endpoint}/api/update'


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


def write_cache(cache):
    """Write cached IPs to file."""
    try:
        with open(CACHE_FILE, 'w') as f:
            json.dump(cache, f)
            debug('Wrote cache: {}'.format(cache))
    except IOError as e:
        log('error', 'Failed to write cache file {}: {}'.format(CACHE_FILE, e))


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
        
        # IPv6: look for "inet6 <address>/prefix scope global"
        ipv6_match = re.search(r'inet6\s+(\S+)/\d+\s+.*scope\s+global', line)
        if ipv6_match and ipv6 is None:
            # Skip link-local (fe80::) and documentation ranges (2001:db8::)
            addr = ipv6_match.group(1)
            if addr.startswith('fe80:') or addr.startswith('2001:db8:'):
                debug('Skipped non-public IPv6: {}'.format(addr))
                continue
            ipv6 = addr
            debug('Found global IPv6: {}'.format(ipv6))
            continue
    
    return ipv4, ipv6


def call_api(endpoint, client, key, zone, record, ip_address, ip_version):
    """Call the DDNS API with an IP update using curl."""
    url = '{endpoint}?client={client}&key={key}&zone={zone}&name={record}&ip={ip}'.format(
        endpoint=endpoint,
        client=client,
        key=key,
        zone=zone,
        record=record,
        ip=ip_address
    )
    
    debug('Calling API: {} (hiding key in logs)'.format(url.replace(key, '***')))
    
    try:
        response = subprocess.check_output(['curl', '-s', url], stderr=subprocess.STDOUT)
        debug('API response: {}'.format(response))
        
        # Check for "OK:" in response (success) or "ERROR:" (failure)
        if 'OK:' in response:
            log('info', 'Successfully updated {} record {} to {}'.format(
                ip_version, record, ip_address))
            return True
        else:
            log('error', 'API returned error for {} update of {}: {}'.format(
                ip_version, record, response))
            return False
    except subprocess.CalledProcessError as e:
        log('error', 'API call failed: {}'.format(e.output))
        return False
    except OSError as e:
        log('error', 'Command "curl" not found or failed: {}'.format(e))
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
    if not ensure_cache_dir():
        sys.exit(1)
    
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
    write_cache(new_cache)
    
    if updated:
        log('info', 'Update complete: changes detected and API calls made')
    else:
        debug('No changes detected')
    
    sys.exit(0)


if __name__ == '__main__':
    syslog.openlog('arkane-ddns-client', syslog.LOG_PID, syslog.LOG_USER)
    
    # Get config file path from argument or environment
    CONFIG_FILE = sys.argv[1] if len(sys.argv) > 1 else '/usr/local/etc/arkane-ddns-client.conf'
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
