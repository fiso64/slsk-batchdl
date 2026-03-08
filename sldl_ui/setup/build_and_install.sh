#!/usr/bin/env bash
# sldl UI — Cross-platform build & install helper
# Detects the OS and runs the appropriate installer.
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"

OS=$(uname -s)

case "$OS" in
  Linux)
    echo "[→] Detected Linux. Running Linux installer..."
    bash "$SCRIPT_DIR/linux/install.sh" "$@"
    ;;
  Darwin)
    echo "[→] Detected macOS. Running macOS installer..."
    bash "$SCRIPT_DIR/macos/install.sh" "$@"
    ;;
  CYGWIN*|MINGW*|MSYS*)
    echo "[→] Detected Windows (Git Bash/MSYS). Please run the installer.iss"
    echo "    with Inno Setup, or build and run manually:"
    echo "    cd sldl_ui && flutter build windows --release"
    ;;
  *)
    echo "[ERROR] Unsupported OS: $OS"
    exit 1
    ;;
esac
