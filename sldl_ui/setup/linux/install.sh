#!/usr/bin/env bash
# sldl UI — Linux installer
# Usage: bash install.sh [--prefix /usr/local] [--with-sldl]
set -euo pipefail

PREFIX="${PREFIX:-$HOME/.local}"
INSTALL_WITH_SLDL=false
APP_NAME="sldl-ui"
APP_DISPLAY="sldl UI"
REPO="https://github.com/fiso64/slsk-batchdl"
FLUTTER_VERSION="3.24.0"

while [[ $# -gt 0 ]]; do
  case $1 in
    --prefix) PREFIX="$2"; shift 2 ;;
    --with-sldl) INSTALL_WITH_SLDL=true; shift ;;
    *) echo "Unknown option: $1"; exit 1 ;;
  esac
done

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd "$SCRIPT_DIR/../.." && pwd)"
UI_DIR="$REPO_ROOT/sldl_ui"
BUILD_DIR="$UI_DIR/build/linux/x64/release/bundle"

echo "═══════════════════════════════════════"
echo " sldl UI — Linux Installer"
echo "═══════════════════════════════════════"
echo "  Prefix: $PREFIX"
echo ""

# ── Step 1: Check Flutter ─────────────────────────────────────────────────────
if ! command -v flutter &>/dev/null; then
  echo "[ERROR] Flutter not found in PATH."
  echo ""
  echo "To install Flutter on Linux:"
  echo "  1. Download from https://docs.flutter.dev/get-started/install/linux"
  echo "  2. Extract and add flutter/bin to your PATH"
  echo "  3. Run: flutter doctor"
  echo ""
  echo "Alternatively, install via snap:"
  echo "  sudo snap install flutter --classic"
  echo ""
  exit 1
fi

echo "[✓] Flutter found: $(flutter --version | head -1)"

# ── Step 2: Install dependencies ──────────────────────────────────────────────
echo ""
echo "[→] Installing Flutter dependencies..."
cd "$UI_DIR"
flutter pub get

# ── Step 3: Build ─────────────────────────────────────────────────────────────
echo ""
echo "[→] Building sldl UI for Linux..."
flutter build linux --release

if [[ ! -d "$BUILD_DIR" ]]; then
  echo "[ERROR] Build failed — output directory not found: $BUILD_DIR"
  exit 1
fi

echo "[✓] Build complete."

# ── Step 4: Install sldl (optional) ──────────────────────────────────────────
if [[ "$INSTALL_WITH_SLDL" == "true" ]]; then
  echo ""
  echo "[→] Checking for sldl binary..."

  if command -v sldl &>/dev/null; then
    echo "[✓] sldl already in PATH: $(which sldl)"
  else
    # Try to download sldl from GitHub releases
    LATEST_SLDL=$(curl -s "https://api.github.com/repos/fiso64/slsk-batchdl/releases/latest" \
      | grep '"tag_name"' | sed 's/.*"tag_name": "\(.*\)".*/\1/')

    ARCH=$(uname -m)
    if [[ "$ARCH" == "x86_64" ]]; then
      SLDL_FILE="sldl-linux-x64"
    elif [[ "$ARCH" == "aarch64" ]]; then
      SLDL_FILE="sldl-linux-arm64"
    else
      echo "[WARN] Unsupported architecture for auto-download: $ARCH"
      SLDL_FILE=""
    fi

    if [[ -n "$SLDL_FILE" && -n "$LATEST_SLDL" ]]; then
      SLDL_URL="https://github.com/fiso64/slsk-batchdl/releases/download/$LATEST_SLDL/$SLDL_FILE"
      echo "[→] Downloading sldl $LATEST_SLDL..."
      mkdir -p "$BUILD_DIR"
      curl -L -o "$BUILD_DIR/sldl" "$SLDL_URL"
      chmod +x "$BUILD_DIR/sldl"
      echo "[✓] sldl downloaded."
    fi
  fi
fi

# ── Step 5: Install files ─────────────────────────────────────────────────────
echo ""
echo "[→] Installing to $PREFIX..."

BIN_DIR="$PREFIX/bin"
LIB_DIR="$PREFIX/lib/$APP_NAME"
SHARE_DIR="$PREFIX/share"

mkdir -p "$BIN_DIR" "$LIB_DIR" "$SHARE_DIR/applications" "$SHARE_DIR/pixmaps"

# Copy bundle
cp -r "$BUILD_DIR/." "$LIB_DIR/"

# Create launcher script
cat > "$BIN_DIR/$APP_NAME" <<EOF
#!/bin/sh
exec "$LIB_DIR/sldl_ui" "\$@"
EOF
chmod +x "$BIN_DIR/$APP_NAME"

# Desktop entry
cat > "$SHARE_DIR/applications/$APP_NAME.desktop" <<EOF
[Desktop Entry]
Version=1.0
Type=Application
Name=$APP_DISPLAY
GenericName=Soulseek Downloader UI
Comment=A graphical interface for slsk-batchdl (sldl)
Exec=$BIN_DIR/$APP_NAME
Icon=$APP_NAME
Categories=Audio;Network;
Keywords=soulseek;music;download;
EOF

# Copy icon if available
if [[ -f "$REPO_ROOT/sldl_ui/assets/icon.png" ]]; then
  cp "$REPO_ROOT/sldl_ui/assets/icon.png" "$SHARE_DIR/pixmaps/$APP_NAME.png"
fi

echo ""
echo "═══════════════════════════════════════"
echo " Installation complete!"
echo "═══════════════════════════════════════"
echo ""
echo " Run sldl UI:  $BIN_DIR/$APP_NAME"
echo " Or launch from your application menu."
echo ""
echo " NOTE: Make sure sldl is in your PATH or configure its"
echo " path via the Settings screen in sldl UI."
echo " Download sldl from: $REPO/releases"
echo ""
