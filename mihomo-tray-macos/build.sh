#!/bin/bash
set -e
cd "$(dirname "$0")"

echo "=== Mihomo Tray for macOS ==="

go mod tidy
go test ./...
CGO_ENABLED=1 go build -ldflags="-s -w" -o mihomo-tray .

APP="MihomoTray.app"
rm -rf "$APP"
mkdir -p "$APP/Contents/MacOS"
mkdir -p "$APP/Contents/Resources"

cp mihomo-tray "$APP/Contents/MacOS/"
cp on.png off.png warn.png "$APP/Contents/Resources/"

cat > "$APP/Contents/Info.plist" << 'PLIST'
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
<dict>
    <key>CFBundleName</key>
    <string>MihomoTray</string>
    <key>CFBundleIdentifier</key>
    <string>com.mihomo.tray</string>
    <key>CFBundleVersion</key>
    <string>1.0</string>
    <key>CFBundlePackageType</key>
    <string>APPL</string>
    <key>CFBundleExecutable</key>
    <string>mihomo-tray</string>
    <key>LSUIElement</key>
    <true/>
    <key>NSHighResolutionCapable</key>
    <true/>
</dict>
</plist>
PLIST

echo ""
echo "Done: $(pwd)/$APP"
echo "Usage: open $APP"
