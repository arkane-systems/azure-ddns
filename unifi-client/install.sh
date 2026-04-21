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

set -e

if [ "$EUID" -ne 0 ]; then
   echo "Error: This script must be run as root (sudo)"
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

# Create cache directory
mkdir -p /var/cache/arkane-ddns-client
chmod 700 /var/cache/arkane-ddns-client
echo "✓ Created /var/cache/arkane-ddns-client"

# Copy Python script
cp "$SOURCE_DIR/arkane-ddns-client.py" /usr/local/bin/arkane-ddns-client.py
chmod 755 /usr/local/bin/arkane-ddns-client.py
echo "✓ Installed /usr/local/bin/arkane-ddns-client.py"

# Copy config template if no config exists
if [ ! -f /usr/local/etc/arkane-ddns-client.conf ]; then
    cp "$SOURCE_DIR/arkane-ddns-client.conf.example" /usr/local/etc/arkane-ddns-client.conf
    chmod 600 /usr/local/etc/arkane-ddns-client.conf
    echo "✓ Created /usr/local/etc/arkane-ddns-client.conf (REMEMBER TO EDIT THIS FILE)"
else
    echo "✓ Config file already exists at /usr/local/etc/arkane-ddns-client.conf"
fi

# Copy systemd units
mkdir -p /etc/systemd/system
cp "$SOURCE_DIR/arkane-ddns-client.service" /etc/systemd/system/arkane-ddns-client.service
cp "$SOURCE_DIR/arkane-ddns-client.timer" /etc/systemd/system/arkane-ddns-client.timer
chmod 644 /etc/systemd/system/arkane-ddns-client.{service,timer}
echo "✓ Installed systemd units"

# Copy README for reference
mkdir -p /usr/local/etc/arkane-ddns-client
cp "$SOURCE_DIR/README.md" /usr/local/etc/arkane-ddns-client/README.md
echo "✓ Documentation available at /usr/local/etc/arkane-ddns-client/README.md"

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
echo "  1. Edit /usr/local/etc/arkane-ddns-client.conf with your API endpoint, credentials, and settings"
echo "  2. Test the script manually: /usr/local/bin/arkane-ddns-client.py /usr/local/etc/arkane-ddns-client.conf"
echo "  3. Enable and start the timer:"
echo "     sudo systemctl enable arkane-ddns-client.timer"
echo "     sudo systemctl start arkane-ddns-client.timer"
echo "  4. Check status: sudo systemctl status arkane-ddns-client.timer"
echo "  5. View logs: sudo journalctl -u arkane-ddns-client"
echo ""
