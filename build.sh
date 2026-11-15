#!/usr/bin/env bash
# Builds the plugin with the .NET SDK in Docker (no local SDK needed).
set -euo pipefail
cd "$(dirname "$0")"
rm -rf out dist
docker run --rm -v "$PWD":/src -w /src mcr.microsoft.com/dotnet/sdk:9.0 \
  dotnet publish Jellyfin.Plugin.TranscodeManager -c Release -o out
mkdir -p dist/TranscodeManager_0.1.0.0
cp out/Jellyfin.Plugin.TranscodeManager.dll dist/TranscodeManager_0.1.0.0/
cp meta.json dist/TranscodeManager_0.1.0.0/
echo "Built: dist/TranscodeManager_0.1.0.0  -> copy this folder into <jellyfin-config>/plugins/ and restart Jellyfin"
