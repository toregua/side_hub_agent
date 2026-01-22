#!/bin/bash
# Run agent locally for testing
# Usage: ./run-local.sh [config-directory]
#
# The config-directory should contain a .sidehub/ folder with agent configs.
# If not specified, uses current directory.

set -e

SCRIPT_DIR="$(dirname "$0")"
CONFIG_DIR="${1:-$(pwd)}"

cd "$SCRIPT_DIR/SideHub.Agent"

echo "🔨 Building agent..."
dotnet build -c Debug -v q

echo "🚀 Starting agent locally..."
echo "   Config directory: $CONFIG_DIR"
echo "   Press Ctrl+C to stop"
echo ""

cd "$CONFIG_DIR"
dotnet run --no-build --project "$SCRIPT_DIR/SideHub.Agent"
