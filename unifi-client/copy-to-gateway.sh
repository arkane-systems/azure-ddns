#!/bin/bash
# copy-to-gateway.sh - Transfer arkane-ddns-client files to a Unifi gateway via scp
#
# This script copies all necessary files from the repo to a Unifi gateway and
# optionally runs the install script to deploy them into place.
#
# Usage:
#   bash copy-to-gateway.sh <gateway-host> [--install] [--user <username>]
#
# Arguments:
#   <gateway-host>  - Hostname or IP address of the Unifi gateway
#   --install       - Automatically run install.sh on the gateway (requires sudo access)
#   --user <name>   - SSH user (default: root)
#
# Examples:
#   # Copy files to gateway (manual install)
#   bash copy-to-gateway.sh 192.168.1.1
#
#   # Copy files and automatically install (requires sudo, no password)
#   bash copy-to-gateway.sh my-gateway.local --install
#
#   # Copy files as non-root user (requires sudo on gateway)
#   bash copy-to-gateway.sh 192.168.1.1 --user ubnt --install

set -e

# Parse arguments
GATEWAY_HOST=""
AUTO_INSTALL=false
SSH_USER="root"

while [ $# -gt 0 ]; do
    case "$1" in
        --install)
            AUTO_INSTALL=true
            shift
            ;;
        --user)
            SSH_USER="$2"
            shift 2
            ;;
        -*)
            echo "Error: Unknown option: $1"
            echo "Usage: bash copy-to-gateway.sh <gateway-host> [--install] [--user <username>]"
            exit 1
            ;;
        *)
            if [ -z "$GATEWAY_HOST" ]; then
                GATEWAY_HOST="$1"
            else
                echo "Error: Multiple hosts specified"
                exit 1
            fi
            shift
            ;;
    esac
done

if [ -z "$GATEWAY_HOST" ]; then
    echo "Error: Gateway hostname or IP address required"
    echo "Usage: bash copy-to-gateway.sh <gateway-host> [--install] [--user <username>]"
    exit 1
fi

# Get the directory where this script lives (repo's unifi-client folder)
SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"

# Verify required files exist
REQUIRED_FILES=(
    "arkane-ddns-client.py"
    "arkane-ddns-client.conf.example"
    "arkane-ddns-client.service"
    "arkane-ddns-client.timer"
    "install.sh"
    "README.md"
)

for file in "${REQUIRED_FILES[@]}"; do
    if [ ! -f "$SCRIPT_DIR/$file" ]; then
        echo "Error: Required file not found: $SCRIPT_DIR/$file"
        exit 1
    fi
done

# Staging directory on the gateway (in /root)
STAGING_DIR="/root/arkane-ddns-client-staging"

echo "Copying arkane-ddns-client files to $GATEWAY_HOST:$STAGING_DIR ..."
echo ""

# Create staging directory on gateway
ssh -l "$SSH_USER" "$GATEWAY_HOST" "mkdir -p $STAGING_DIR" || {
    echo "Error: Failed to create staging directory on gateway"
    echo "Check that SSH is working and you have write access to /root"
    exit 1
}

# Copy all required files
for file in "${REQUIRED_FILES[@]}"; do
    echo "  → Copying $file..."
    scp -q "$SCRIPT_DIR/$file" "${SSH_USER}@${GATEWAY_HOST}:${STAGING_DIR}/" || {
        echo "Error: Failed to copy $file to gateway"
        exit 1
    }
done

echo ""
echo "✓ Files transferred successfully"
echo "  Location: $STAGING_DIR"
echo ""

# Optionally run install script
if [ "$AUTO_INSTALL" = true ]; then
    echo "Running install.sh on gateway..."
    ssh -l "$SSH_USER" "$GATEWAY_HOST" "sudo bash $STAGING_DIR/install.sh $STAGING_DIR" || {
        echo "Error: Installation failed on gateway"
        echo "Run manually: ssh $SSH_USER@$GATEWAY_HOST"
        echo "            sudo bash $STAGING_DIR/install.sh"
        exit 1
    }
    echo ""
    echo "✓ Installation complete on gateway"
    echo ""
else
    echo "To install on the gateway, run:"
    echo ""
    echo "  ssh $SSH_USER@$GATEWAY_HOST"
    echo "  sudo bash $STAGING_DIR/install.sh"
    echo ""
fi

echo "Next steps on gateway:"
echo "  1. Edit /usr/local/etc/arkane-ddns-client.conf with your settings"
echo "  2. Test: sudo /usr/local/bin/arkane-ddns-client.py /usr/local/etc/arkane-ddns-client.conf"
echo "  3. Enable: sudo systemctl enable arkane-ddns-client.timer"
echo "  4. Start: sudo systemctl start arkane-ddns-client.timer"
echo ""
