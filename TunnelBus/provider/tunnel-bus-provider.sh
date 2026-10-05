#!/usr/bin/env bash
# Register a name on the tunnel bus and keep a stock chisel client connected.
# Usage: tunnel-bus-provider.sh --bus https://tunnel-bus.X.workers.dev --name myapp --port 3000 [--token T]
# The admin token comes from --token or $TUNNEL_BUS_ADMIN_TOKEN.
set -u
BUS=${TUNNEL_BUS_URL:-}; NAME=; PORT=; TOKEN=${TUNNEL_BUS_ADMIN_TOKEN:-}; HOST=localhost
CHISEL_VERSION=${CHISEL_VERSION:-1.10.1}
while [ $# -gt 0 ]; do
  case $1 in
    --bus) BUS=$2; shift 2;; --name) NAME=$2; shift 2;; --port) PORT=$2; shift 2;;
    --token) TOKEN=$2; shift 2;; --host) HOST=$2; shift 2;;
    *) echo "unknown argument $1" >&2; exit 2;;
  esac
done
[ -n "$BUS" ] && [ -n "$NAME" ] && [ -n "$PORT" ] && [ -n "$TOKEN" ] || { echo "need --bus, --name, --port and a token (--token or TUNNEL_BUS_ADMIN_TOKEN)" >&2; exit 2; }
BUS=${BUS%/}

find_chisel() {
  if command -v chisel >/dev/null 2>&1; then command -v chisel; return; fi
  local os arch dir bin
  case $(uname -s) in Linux) os=linux;; Darwin) os=darwin;; *) os=windows;; esac
  case $(uname -m) in x86_64|amd64) arch=amd64;; aarch64|arm64) arch=arm64;; *) arch=amd64;; esac
  dir=${XDG_CACHE_HOME:-$HOME/.cache}/tunnel-bus; bin=$dir/chisel-$CHISEL_VERSION
  if [ ! -x "$bin" ]; then
    mkdir -p "$dir"
    echo "downloading chisel $CHISEL_VERSION ($os/$arch)" >&2
    curl -fsSL "https://github.com/jpillora/chisel/releases/download/v$CHISEL_VERSION/chisel_${CHISEL_VERSION}_${os}_${arch}.gz" | gunzip > "$bin.tmp" || return 1
    chmod +x "$bin.tmp"; mv "$bin.tmp" "$bin"
  fi
  echo "$bin"
}
CHISEL=$(find_chisel) || { echo "could not get chisel" >&2; exit 1; }

json_field() { # json_field <field>  (reads stdin)
  if command -v jq >/dev/null 2>&1; then jq -r ".$1"; else sed -n "s/.*\"$1\":\"\\?\\([^\",}]*\\).*/\\1/p"; fi
}

trap 'exit 0' INT TERM
while true; do
  RESP=$(curl -fsS -X POST "$BUS/_api/register" -H "Authorization: Bearer $TOKEN" \
        -H 'Content-Type: application/json' -d "{\"name\":\"$NAME\"}")
  if [ -z "$RESP" ]; then echo "register failed, retrying in 5s" >&2; sleep 5; continue; fi
  USER=$(printf %s "$RESP" | json_field user | head -1)
  PASS=$(printf %s "$RESP" | json_field password | head -1)
  RPORT=$(printf %s "$RESP" | json_field port | head -1)
  echo "registered: $BUS/$NAME/  (server port $RPORT)" >&2
  sleep 1 # let chisel reload its authfile
  "$CHISEL" client --keepalive 25s --auth "$USER:$PASS" "$BUS/_chisel" "R:$RPORT:$HOST:$PORT"
  echo "chisel exited; re-registering in 3s" >&2
  sleep 3
done
