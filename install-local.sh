#!/bin/bash
# Build and install agent locally (bypasses GitHub)
# Usage: ./install-local.sh

set -e

cd "$(dirname "$0")/SideHub.Agent"

echo "🔨 Building agent..."
dotnet publish -c Release -o ./publish --self-contained -r osx-arm64 -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true --verbosity quiet

PUBLISH_PATH="$(pwd)/publish/sidehub-agent"

echo "📦 Installing to /usr/local/bin/sidehub-agent..."
if sudo cp "$PUBLISH_PATH" /usr/local/bin/sidehub-agent 2>/dev/null; then
    echo "✅ Installed! Restart any running agent to apply changes."
else
    echo ""
    echo "⚠️  Sudo required. Run this command manually:"
    echo "   sudo cp '$PUBLISH_PATH' /usr/local/bin/sidehub-agent"
fi
