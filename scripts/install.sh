#!/usr/bin/env bash
# Installs or upgrades SaveKeep as a systemd service. Run it from the extracted package:
#   sudo ./install.sh
# The data directory (/var/lib/savekeep) and the config (/etc/savekeep/savekeep.json) are never overwritten.
set -euo pipefail
[ "$(id -u)" = 0 ] || { echo "Run with sudo." >&2; exit 1; }
HERE="$(cd "$(dirname "$0")" && pwd)"

if ! ldconfig -p | grep -q 'libicuuc\.so'; then
  echo "== Installing libicu (needed by .NET)"
  PKG="$(apt-cache search --names-only '^libicu[0-9]+$' | awk '{print $1}' | sort -V | tail -1)"
  [ -n "$PKG" ] || { echo "No libicu package found; run: sudo apt update" >&2; exit 1; }
  apt-get install -y --no-install-recommends "$PKG"
fi

echo "== User and directories"
id savekeep >/dev/null 2>&1 || useradd --system --home-dir /var/lib/savekeep --shell /usr/sbin/nologin savekeep
install -d -o savekeep -g savekeep -m 750 /var/lib/savekeep
install -d -m 755 /opt/savekeep /etc/savekeep

echo "== Application"
systemctl stop savekeep 2>/dev/null || true
rm -rf /opt/savekeep/app.new /opt/savekeep/app.old
cp -a "$HERE/app" /opt/savekeep/app.new
chown -R root:root /opt/savekeep/app.new
[ -d /opt/savekeep/app ] && mv /opt/savekeep/app /opt/savekeep/app.old
mv /opt/savekeep/app.new /opt/savekeep/app
cp "$HERE"/README.md "$HERE"/LICENSE "$HERE"/THIRD-PARTY.md "$HERE"/BUILD-INFO.txt /opt/savekeep/ 2>/dev/null || true
[ -f /etc/savekeep/savekeep.json ] || install -m 644 "$HERE/deploy/savekeep.json" /etc/savekeep/savekeep.json

echo "== Service"
install -m 644 "$HERE/deploy/savekeep.service" /etc/systemd/system/savekeep.service
systemctl daemon-reload
systemctl enable --now savekeep

PORT="$(sed -n 's/.*"Urls"[^:]*:[^:]*:\/\/[^:]*:\([0-9]*\).*/\1/p' /etc/savekeep/savekeep.json | head -1)"
PORT="${PORT:-8797}"
for _ in $(seq 1 30); do
  if curl -fsS "http://127.0.0.1:$PORT/api/status" 2>/dev/null | grep -q '"app":"SaveKeep"'; then
    echo "SaveKeep is running on port $PORT."
    exit 0
  fi
  sleep 1
done
echo "SaveKeep did not answer on port $PORT. Check: journalctl -u savekeep -n 50" >&2
exit 1
