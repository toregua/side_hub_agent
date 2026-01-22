#!/bin/bash
# Deploy script for SideHub.Agent
# Usage: ./deploy.sh "commit message"

set -e

cd "$(dirname "$0")"

MESSAGE="${1:-Update agent}"

echo "📦 Committing changes..."
git add -A
git commit -m "$MESSAGE" || echo "Nothing to commit"

echo "🚀 Pushing to remote..."
git push

echo "🏷️  Recreating tag 1.0.0..."
git tag -d 1.0.0 2>/dev/null || true
git push origin :refs/tags/1.0.0 2>/dev/null || true
git tag 1.0.0
git push origin 1.0.0

echo "✅ Deployed!"
