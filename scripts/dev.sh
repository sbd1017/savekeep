#!/usr/bin/env bash
# Runs SaveKeep locally with a simulated ftpd, data in ./data-dev.
#   scripts/dev.sh            web on http://localhost:8797, fake ftpd on 127.0.0.1:5021
# Ctrl-C stops both.
set -euo pipefail
cd "$(dirname "$0")/.."
DOTNET="${DOTNET:-dotnet}"
WEB_PORT="${WEB_PORT:-8797}"
FTP_PORT="${FTP_PORT:-5021}" # macOS uses 5000 for AirPlay

"$DOTNET" build SaveKeep.slnx -c Debug -v quiet -nologo
"$DOTNET" run --no-build --project src/SaveKeep.Sim -- --port "$FTP_PORT" < /dev/null &
SIM=$!
trap 'kill $SIM 2>/dev/null' EXIT
echo "Fake ftpd on 127.0.0.1:$FTP_PORT. In SaveKeep, search with console IP 127.0.0.1 and port $FTP_PORT."
SAVEKEEP_Urls="http://127.0.0.1:$WEB_PORT" SAVEKEEP_SaveKeep__DataDir="$PWD/data-dev" \
  "$DOTNET" run --no-build --project src/SaveKeep.Web
