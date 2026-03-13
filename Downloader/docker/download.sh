#!/bin/bash
set -e

PROFILE_DIR="/home/kasm-user/.config/BraveSoftware/Brave-Browser"
TARGET_URL="${TARGET_URL:-https://youtube.com}"
BROWSE_WAIT="${BROWSE_WAIT:-5}"
OUTPUT_FOLDER="${OUTPUT_FOLDER:-downloads}"

echo "[download] Starting Brave headless to refresh cookies..."

# Launch Brave headless as kasm-user in the background
su -c "brave-browser --headless --no-sandbox --disable-gpu --disable-software-rasterizer '$TARGET_URL'" kasm-user &
BRAVE_PID=$!

echo "[download] Waiting ${BROWSE_WAIT}s for page to load and cookies to settle..."
sleep "$BROWSE_WAIT"

echo "[download] Shutting down Brave..."
kill "$BRAVE_PID" 2>/dev/null || true
# Wait for graceful shutdown, then force if needed
timeout 10 tail --pid="$BRAVE_PID" -f /dev/null 2>/dev/null || kill -9 "$BRAVE_PID" 2>/dev/null || true
sleep 2

# Clean lock files so future runs work
rm -f "$PROFILE_DIR/SingletonLock" \
      "$PROFILE_DIR/SingletonCookie" \
      "$PROFILE_DIR/SingletonSocket"

echo "[download] Installing yt-dlp..."
if ! command -v yt-dlp &>/dev/null; then
    curl -sL https://github.com/yt-dlp/yt-dlp/releases/latest/download/yt-dlp_linux -o /usr/local/bin/yt-dlp
    chmod +x /usr/local/bin/yt-dlp
fi

OUT_DIR="/out/$OUTPUT_FOLDER"
mkdir -p "$OUT_DIR"
chown kasm-user:kasm-user "$OUT_DIR"

echo "[download] Running yt-dlp with cookies from Brave profile..."
echo "[download] Output directory: $OUT_DIR"
exec su -c "yt-dlp --js-runtimes node --remote-components ejs:github --cookies-from-browser brave --paths '$OUT_DIR' $*" kasm-user
