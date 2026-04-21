#!/bin/bash
# install.sh - Helper script to deploy arkane-ddns-client on a Unifi gateway
#
# This script copies the necessary files to the correct locations and sets up
# the systemd service for automatic execution.
#
# Usage:
#   sudo bash install.sh /path/to/unifi-client/repo
#
# Example:
#   sudo bash install.sh /tmp/arkane-ddns-repo/unifi-client

set -e

if [ "$EUID" -ne 0 ]; then
   echo "Error: This script must be run as root (sudo)"
   exit 1
fi

SCRIPT_DIR="${1:-.}"

if [ ! -f "$SCRIPT_DIR/arkane-ddns-client.py" ]; then
    echo "Error: Could not find arkane-ddns-client.py in $SCRIPT_DIR"
    exit 1
fi

echo "Installing arkane-ddns-client..."

# Create cache directory
mkdir -p /var/cache/arkane-ddns-client
chmod 700 /var/cache/arkane-ddns-client
echo "✓ Created /var/cache/arkane-ddns-client"

# Copy Python script
cp "$SCRIPT_DIR/arkane-ddns-client.py" /usr/local/bin/arkane-ddns-client.py
chmod 755 /usr/local/bin/arkane-ddns-client.py
echo "✓ Installed /usr/local/bin/arkane-ddns-client.py"

# Copy config template if no config exists
if [ ! -f /usr/local/etc/arkane-ddns-client.conf ]; then
    cp "$SCRIPT_DIR/arkane-ddns-client.conf.example" /usr/local/etc/arkane-ddns-client.conf
    chmod 600 /usr/local/etc/arkane-ddns-client.conf
    echo "✓ Created /usr/local/etc/arkane-ddns-client.conf (REMEMBER TO EDIT THIS FILE)"
else
    echo "✓ Config file already exists at /usr/local/etc/arkane-ddns-client.conf"
fi

# Copy systemd units
mkdir -p /etc/systemd/system
cp "$SCRIPT_DIR/arkane-ddns-client.service" /etc/systemd/system/arkane-ddns-client.service
cp "$SCRIPT_DIR/arkane-ddns-client.timer" /etc/systemd/system/arkane-ddns-client.timer
chmod 644 /etc/systemd/system/arkane-ddns-client.{service,timer}
echo "✓ Installed systemd units"

# Copy README for reference
if [ -d /usr/local/etc/arkane-ddns-client ]; then
    cp "$SCRIPT_DIR/README.md" /usr/local/etc/arkane-ddns-client/README.md 2>/dev/null || true
    echo "✓ Documentation available at /usr/local/etc/arkane-ddns-client/README.md"
fi

# Reload systemd daemon
systemctl daemon-reload
echo "✓ Reloaded systemd daemon"

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
