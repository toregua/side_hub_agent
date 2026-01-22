#!/bin/bash
# Build and install agent locally (bypasses GitHub)
# Usage: ./install-local.sh

set -e

SCRIPT_DIR="$(cd "$(dirname "$0")" && pwd)"
AGENT_DIR="$SCRIPT_DIR/SideHub.Agent"
INSTALL_DIR="/usr/local/lib/sidehub-agent"
BIN_LINK="/usr/local/bin/sidehub-agent"

cd "$AGENT_DIR"

echo "🔨 Building agent..."
dotnet publish -c Release -o ./publish --self-contained -r osx-arm64 --verbosity quiet

echo "📦 Installing pty-helper dependencies..."
cd "$AGENT_DIR/pty-helper"
npm install --silent

echo "📁 Installing to $INSTALL_DIR..."

# Create install directory and copy files
if [ -w "$(dirname "$INSTALL_DIR")" ]; then
    rm -rf "$INSTALL_DIR"
    mkdir -p "$INSTALL_DIR"
    cp -r "$AGENT_DIR/publish/"* "$INSTALL_DIR/"
    cp -r "$AGENT_DIR/pty-helper" "$INSTALL_DIR/"

    # Create symlink
    rm -f "$BIN_LINK"
    ln -s "$INSTALL_DIR/sidehub-agent" "$BIN_LINK"
else
    sudo rm -rf "$INSTALL_DIR"
    sudo mkdir -p "$INSTALL_DIR"
    sudo cp -r "$AGENT_DIR/publish/"* "$INSTALL_DIR/"
    sudo cp -r "$AGENT_DIR/pty-helper" "$INSTALL_DIR/"

    # Create symlink
    sudo rm -f "$BIN_LINK"
    sudo ln -s "$INSTALL_DIR/sidehub-agent" "$BIN_LINK"
fi

echo ""
echo "✅ SideHub Agent installed!"
echo "   Binary: $INSTALL_DIR/sidehub-agent"
echo "   Symlink: $BIN_LINK"
echo ""
echo "Restart any running agent to apply changes."
