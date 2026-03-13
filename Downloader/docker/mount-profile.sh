#!/bin/bash
set -e

IMG_FILE="/mnt/host-data/brave-profile.img"
MOUNT_POINT="/home/kasm-user/.config/BraveSoftware/Brave-Browser"
IMG_SIZE_MB="${PROFILE_IMG_SIZE_MB:-256}"

# Install mkfs.ext4 if missing
if ! command -v mkfs.ext4 &>/dev/null; then
    echo "[profile-mount] Installing e2fsprogs..."
    apt-get update -qq && apt-get install -y -qq --no-install-recommends e2fsprogs
fi

# Create image if it doesn't exist
if [ ! -f "$IMG_FILE" ]; then
    echo "[profile-mount] Creating ${IMG_SIZE_MB}MB ext4 image..."
    dd if=/dev/zero of="$IMG_FILE" bs=1M count="$IMG_SIZE_MB" status=progress
    mkfs.ext4 -q "$IMG_FILE"
    echo "[profile-mount] Image created."
fi

# Ensure mount point exists
mkdir -p "$MOUNT_POINT"

# Loop-mount the image
echo "[profile-mount] Mounting $IMG_FILE -> $MOUNT_POINT"
mount -o loop "$IMG_FILE" "$MOUNT_POINT"

# Remove stale lock files from previous sessions
echo "[profile-mount] Cleaning stale lock files..."
rm -f "$MOUNT_POINT/SingletonLock" \
      "$MOUNT_POINT/SingletonCookie" \
      "$MOUNT_POINT/SingletonSocket"

# Fix ownership so Brave can write to it
chown -R kasm-user:kasm-user "$MOUNT_POINT"

echo "[profile-mount] Mounted successfully."
