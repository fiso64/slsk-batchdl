#!/usr/bin/env bash
# sldl UI — Bootstrap script
# Run this ONCE after cloning to create the Flutter platform runner files.
# After this, you can build normally with `flutter build <platform> --release`.
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
UI_DIR="$(cd "$SCRIPT_DIR/.." && pwd)"

echo "═══════════════════════════════════════"
echo " sldl UI — Bootstrap"
echo "═══════════════════════════════════════"
echo ""

# Check Flutter
if ! command -v flutter &>/dev/null; then
  echo "[ERROR] Flutter SDK not found in PATH."
  echo ""
  echo "Install from: https://docs.flutter.dev/get-started/install"
  echo ""
  exit 1
fi

echo "[✓] Flutter: $(flutter --version | head -1)"
echo ""

# Detect platform
OS=$(uname -s)
case "$OS" in
  Linux)
    PLATFORM="linux"
    ;;
  Darwin)
    PLATFORM="macos"
    ;;
  CYGWIN*|MINGW*|MSYS*)
    PLATFORM="windows"
    ;;
  *)
    echo "[WARN] Unknown OS '$OS'. Will attempt all platforms."
    PLATFORM="windows,linux,macos"
    ;;
esac

echo "[→] Creating Flutter platform runner files for: $PLATFORM"
echo "    (This adds the native platform scaffold without overwriting our Dart source files)"
echo ""

# We create a temp project, then copy only the platform-specific directories
TEMP_DIR=$(mktemp -d)
TEMP_PROJ="$TEMP_DIR/sldl_ui_scaffold"

flutter create \
  --project-name sldl_ui \
  --org com.github.fiso64.sldlui \
  --platforms "$PLATFORM" \
  --template app \
  "$TEMP_PROJ"

# Copy platform directories (do NOT overwrite lib/ pubspec.yaml etc.)
for dir in windows linux macos; do
  if [[ -d "$TEMP_PROJ/$dir" ]]; then
    if [[ -d "$UI_DIR/$dir" ]]; then
      echo "[→] Platform dir '$dir' already exists. Skipping."
    else
      echo "[→] Copying platform dir: $dir"
      cp -r "$TEMP_PROJ/$dir" "$UI_DIR/"
    fi
  fi
done

# Cleanup temp
rm -rf "$TEMP_DIR"

# Install Flutter deps
echo ""
echo "[→] Running flutter pub get..."
cd "$UI_DIR"
flutter pub get

echo ""
echo "═══════════════════════════════════════"
echo " Bootstrap complete!"
echo "═══════════════════════════════════════"
echo ""
echo " You can now build and run:"
echo "   flutter run            (debug)"
echo "   flutter build $PLATFORM --release"
echo ""
echo " Or use the platform-specific install scripts:"
echo "   bash setup/linux/install.sh"
echo "   bash setup/macos/install.sh"
echo "   setup\\windows\\build_and_install.bat (Windows)"
echo ""
