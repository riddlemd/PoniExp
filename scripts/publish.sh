#!/usr/bin/env sh
# Self-contained, single-file builds of poniexp-client (no .NET install needed to run them).
# Usage: scripts/publish.sh [rid ...]   (default: every supported runtime)
# Output: artifacts/<rid>/poniexp-client[.exe]
set -eu
cd "$(dirname "$0")/.."
rids="${*:-win-x64 win-arm64 osx-x64 osx-arm64 linux-x64 linux-arm64}"
for rid in $rids; do
  dotnet publish src/PoniExp.Cli -c Release -r "$rid" --self-contained \
    -p:PublishSingleFile=true -p:DebugType=none -o "artifacts/$rid" -v q
  echo "artifacts/$rid"
done
