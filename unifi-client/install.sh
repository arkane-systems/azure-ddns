#!/bin/bash
# install.sh - Install arkane-ddns-client on a Unifi gateway
#
# This script is designed to run ON the Unifi gateway after files are transferred.
# It can be called in two ways:
#   1. Via scp/ssh from copy-to-gateway.sh (files in staging directory)
#   2. Directly on gateway for local testing (files passed as argument)
#
# Usage:
#   sudo bash install.sh [/path/to/files]
#
# When called from copy-to-gateway.sh, the script finds files in:
#   /root/arkane-ddns-client-staging/
#
# Example (local/testing):
#   sudo bash install.sh /root/arkane-ddns-client-staging
#
# WHERE THINGS GO, AND WHY
#   Unifi software updates rebuild the root filesystem: /usr/local (and anything else outside the
#   persistent areas) is wiped, while /data and /etc survive. So everything that must outlive an update
#   lives under /data/arkane-ddns-client/ (program, config containing the API key, documentation), and
#   the systemd units go in /etc/systemd/system/ as usual. The cache is disposable and lives in
#   /var/cache/arkane-ddns-client/; it is recreated automatically whenever it is missing.
#
#   Earlier versions installed under /usr/local. If those files are still present, this script moves the
#   configuration to the new location and removes the old copies, so re-running it upgrades an install.

set -e

INSTALL_DIR=/data/arkane-ddns-client
CACHE_DIR=/var/cache/arkane-ddns-client
CONFIG_FILE="$INSTALL_DIR/arkane-ddns-client.conf"

# Locations used by earlier versions (wiped by Unifi software updates).
LEGACY_BIN=/usr/local/bin/arkane-ddns-client.py
LEGACY_CONFIG=/usr/local/etc/arkane-ddns-client.conf
LEGACY_DOC_DIR=/usr/local/etc/arkane-ddns-client

if [ "$EUID" -ne 0 ]; then
   echo "Error: This script must be run as root (sudo)"
   exit 1
fi

if [ ! -d /data ]; then
    echo "Error: /data does not exist. This does not look like a Unifi OS device,"
    echo "and /data is the location that survives Unifi software updates."
    exit 1
fi

# Source directory: from argument, or assume staging directory, or current dir
SOURCE_DIR="${1:-/root/arkane-ddns-client-staging}"

if [ ! -f "$SOURCE_DIR/arkane-ddns-client.py" ]; then
    echo "Error: Could not find arkane-ddns-client.py in $SOURCE_DIR"
    echo "Usage: sudo bash install.sh [/path/to/files]"
    exit 1
fi

echo "Installing arkane-ddns-client from: $SOURCE_DIR"

# Install directory (persistent). 700: the config inside holds the raw API key.
mkdir -p "$INSTALL_DIR"
chmod 700 "$INSTALL_DIR"
echo "✓ Created $INSTALL_DIR"

# Copy Python script. The service runs it through the interpreter, so the exec bit is a convenience for manual runs.
cp "$SOURCE_DIR/arkane-ddns-client.py" "$INSTALL_DIR/arkane-ddns-client.py"
chmod 755 "$INSTALL_DIR/arkane-ddns-client.py"
echo "✓ Installed $INSTALL_DIR/arkane-ddns-client.py"

# Configuration: keep an existing one, else migrate one from the old /usr/local location, else install the template.
if [ -f "$CONFIG_FILE" ]; then
    echo "✓ Config file already exists at $CONFIG_FILE"
elif [ -f "$LEGACY_CONFIG" ]; then
    cp -p "$LEGACY_CONFIG" "$CONFIG_FILE"
    chmod 600 "$CONFIG_FILE"
    echo "✓ Migrated your existing configuration from $LEGACY_CONFIG to $CONFIG_FILE"
else
    cp "$SOURCE_DIR/arkane-ddns-client.conf.example" "$CONFIG_FILE"
    chmod 600 "$CONFIG_FILE"
    echo "✓ Created $CONFIG_FILE (REMEMBER TO EDIT THIS FILE)"
fi

# Copy README for reference
cp "$SOURCE_DIR/README.md" "$INSTALL_DIR/README.md"
echo "✓ Documentation available at $INSTALL_DIR/README.md"

# Remove files left by earlier versions. (They are gone already if a Unifi update has run since.)
rm -f "$LEGACY_BIN"
rm -rf "$LEGACY_DOC_DIR"
if [ -f "$LEGACY_CONFIG" ] && [ -f "$CONFIG_FILE" ]; then
    rm -f "$LEGACY_CONFIG"
fi
echo "✓ Removed any files from the old /usr/local location"

# Cache directory (disposable). The systemd unit and the script itself also recreate it when it is missing.
mkdir -p "$CACHE_DIR"
chmod 700 "$CACHE_DIR"
echo "✓ Created $CACHE_DIR"

# Copy systemd units (/etc/systemd/system survives Unifi software updates)
mkdir -p /etc/systemd/system
cp "$SOURCE_DIR/arkane-ddns-client.service" /etc/systemd/system/arkane-ddns-client.service
cp "$SOURCE_DIR/arkane-ddns-client.timer" /etc/systemd/system/arkane-ddns-client.timer
chmod 644 /etc/systemd/system/arkane-ddns-client.{service,timer}
echo "✓ Installed systemd units"

# Reload systemd daemon
systemctl daemon-reload
echo "✓ Reloaded systemd daemon"

# Cleanup staging directory
if [ "$SOURCE_DIR" = "/root/arkane-ddns-client-staging" ]; then
    rm -rf "$SOURCE_DIR"
    echo "✓ Cleaned up staging directory"
fi

echo ""
echo "Installation complete!"
echo ""
echo "Next steps:"
echo "  1. Edit $CONFIG_FILE with your API endpoint, credentials, and settings"
echo "  2. Test the script manually: python $INSTALL_DIR/arkane-ddns-client.py $CONFIG_FILE"
echo "  3. Enable and start the timer (skip if you are upgrading an install that already runs):"
echo "     sudo systemctl enable arkane-ddns-client.timer"
echo "     sudo systemctl start arkane-ddns-client.timer"
echo "  4. Check status: sudo systemctl status arkane-ddns-client.timer"
echo "  5. View logs: sudo journalctl -u arkane-ddns-client"
echo ""
