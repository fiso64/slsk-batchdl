#!/usr/bin/env bash
# sldl UI — macOS installer
# Usage: bash install.sh [--with-sldl]
set -euo pipefail

INSTALL_WITH_SLDL=false
APP_NAME="sldl UI"
REPO="https://github.com/fiso64/slsk-batchdl"

while [[ $# -gt 0 ]]; do
  case $1 in
    --with-sldl) INSTALL_WITH_SLDL=true; shift ;;
    *) echo "Unknown option: $1"; exit 1 ;;
  esac
done

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd "$SCRIPT_DIR/../.." && pwd)"
UI_DIR="$REPO_ROOT/sldl_ui"
BUILD_DIR="$UI_DIR/build/macos/Build/Products/Release"
APPS_DIR="/Applications"

echo "═══════════════════════════════════════"
echo " sldl UI — macOS Installer"
echo "═══════════════════════════════════════"
echo ""

# ── Step 1: Check Flutter ─────────────────────────────────────────────────────
if ! command -v flutter &>/dev/null; then
  echo "[ERROR] Flutter not found in PATH."
  echo ""
  echo "To install Flutter on macOS:"
  echo "  1. Install Homebrew: https://brew.sh"
  echo "  2. brew install flutter"
  echo "  3. flutter doctor"
  echo ""
  echo "Or download from: https://docs.flutter.dev/get-started/install/macos"
  exit 1
fi

echo "[✓] Flutter found: $(flutter --version | head -1)"

# Check Xcode
if ! xcode-select -p &>/dev/null; then
  echo "[ERROR] Xcode command line tools not found."
  echo "Run: xcode-select --install"
  exit 1
fi

echo "[✓] Xcode command line tools found."

# ── Step 2: Install dependencies ──────────────────────────────────────────────
echo ""
echo "[→] Installing Flutter dependencies..."
cd "$UI_DIR"
flutter pub get

# ── Step 3: Build ─────────────────────────────────────────────────────────────
echo ""
echo "[→] Building sldl UI for macOS..."
flutter build macos --release

APP_BUNDLE="$BUILD_DIR/sldl UI.app"
if [[ ! -d "$APP_BUNDLE" ]]; then
  # Try alternate casing
  APP_BUNDLE="$BUILD_DIR/sldl_ui.app"
fi

if [[ ! -d "$APP_BUNDLE" ]]; then
  echo "[ERROR] Build failed — .app bundle not found in $BUILD_DIR"
  ls "$BUILD_DIR" 2>/dev/null || true
  exit 1
fi

echo "[✓] Build complete."

# ── Step 4: Install sldl (optional via Homebrew) ─────────────────────────────
if [[ "$INSTALL_WITH_SLDL" == "true" ]]; then
  echo ""
  echo "[→] Checking for sldl binary..."
  if command -v sldl &>/dev/null; then
    echo "[✓] sldl already in PATH: $(which sldl)"
  else
    echo "[INFO] sldl not found. To install sldl, download it from:"
    echo "       $REPO/releases"
    echo "       Then move it to /usr/local/bin/sldl and make it executable."
  fi
fi

# ── Step 5: Install .app bundle ───────────────────────────────────────────────
echo ""
echo "[→] Installing to $APPS_DIR..."

DEST="$APPS_DIR/$(basename "$APP_BUNDLE")"
if [[ -d "$DEST" ]]; then
  echo "[→] Removing existing installation..."
  rm -rf "$DEST"
fi

cp -r "$APP_BUNDLE" "$APPS_DIR/"

echo ""
echo "═══════════════════════════════════════"
echo " Installation complete!"
echo "═══════════════════════════════════════"
echo ""
echo " Launch from Applications or run:"
echo "   open '$DEST'"
echo ""
echo " NOTE: Make sure sldl is in your PATH or configure its"
echo " path via the Settings screen in sldl UI."
echo " Download sldl from: $REPO/releases"
echo ""

# Open the app
read -p "Open sldl UI now? [y/N] " yn
if [[ "$yn" =~ ^[Yy]$ ]]; then
  open "$DEST"
fi
