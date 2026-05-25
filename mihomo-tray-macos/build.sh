#!/bin/bash
set -e
cd "$(dirname "$0")"

echo "=== Mihomo Tray for macOS ==="

if ! command -v go &> /dev/null; then
    echo "[ERROR] Go is not installed. Install from https://go.dev/dl/"
    exit 1
fi

echo "Go: $(go version)"

echo "Downloading dependencies..."
go mod tidy
echo ""

echo "Building..."
CGO_ENABLED=1 go build -ldflags="-s -w" -o mihomo-tray .
echo ""

echo "=== Build successful ==="
echo "Binary: $(pwd)/mihomo-tray"
echo ""
echo "Usage:"
echo "  cp mihomo-tray /path/to/mihomo/dir/"
echo "  cd /path/to/mihomo/dir && ./mihomo-tray &"
echo ""
echo "The menu bar icon will appear immediately."
echo "Use 'Auto Start' in the menu to register with launchd."
