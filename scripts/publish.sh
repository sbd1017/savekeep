#!/usr/bin/env bash
# Builds a self-contained Linux package. Needs the .NET 10 SDK; runs on macOS or Linux.
#   scripts/publish.sh [linux-arm64|linux-x64]     (default linux-arm64, for a Raspberry Pi)
# Output: dist/savekeep-<rid>.tar.gz and its .sha256
set -euo pipefail
cd "$(dirname "$0")/.."
RID="${1:-linux-arm64}"
DOTNET="${DOTNET:-dotnet}"
export DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1
NAME="savekeep-$RID"
OUT="dist/$NAME"
rm -rf "$OUT" "$OUT.tar.gz" "$OUT.tar.gz.sha256"

"$DOTNET" restore SaveKeep.slnx --locked-mode
"$DOTNET" test tests/SaveKeep.Tests -c Release
"$DOTNET" publish src/SaveKeep.Web -c Release -r "$RID" --self-contained true -p:RestoreLockedMode=true \
  -p:PublishSingleFile=false -p:DebugType=none -p:ContinuousIntegrationBuild=true -o "$OUT/app"

mkdir -p "$OUT/deploy"
cp deploy/savekeep.service deploy/savekeep.json "$OUT/deploy/"
cp scripts/install.sh "$OUT/install.sh"
cp README.md LICENSE THIRD-PARTY.md "$OUT/"
{
  echo "SaveKeep $(sed -n 's:.*<Version>\(.*\)</Version>.*:\1:p' Directory.Build.props)"
  echo "Built:    $(date -u +%Y-%m-%dT%H:%M:%SZ)"
  echo "Commit:   $(git rev-parse --short HEAD 2>/dev/null || echo none)$(git diff --quiet 2>/dev/null || echo ' (with uncommitted changes)')"
  echo "Runtime:  $RID, self-contained, .NET SDK $("$DOTNET" --version)"
} > "$OUT/BUILD-INFO.txt"

tar --version 2>/dev/null | grep -q GNU && TAROPTS="--sort=name --owner=0 --group=0 --numeric-owner" || TAROPTS="--uid 0 --gid 0 --no-xattrs --no-mac-metadata"
(cd dist && COPYFILE_DISABLE=1 tar $TAROPTS -czf "$NAME.tar.gz" "$NAME")
(cd dist && shasum -a 256 "$NAME.tar.gz" > "$NAME.tar.gz.sha256")
cat "dist/$NAME.tar.gz.sha256"
