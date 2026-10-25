#!/bin/bash
# ═══════════════════════════════════════════════════════
#  Arpoon - macOS .dmg builder
#  Publishes self-contained x64 + arm64 builds, lipo's them into one
#  universal binary, wraps it in a Arpoon.app bundle (admin prompt
#  via a native macOS dialog, no Terminal), and packages a .dmg.
#
#  Run this ON macOS (hdiutil/lipo are macOS-only tools):
#      ./packaging/macos/build_dmg.sh
#  Requires the .NET 8 SDK (installed via Homebrew if missing).
# ═══════════════════════════════════════════════════════

set -euo pipefail

GREEN='\033[0;32m'; YELLOW='\033[1;33m'; RED='\033[0;31m'; CYAN='\033[0;36m'; NC='\033[0m'

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd "$SCRIPT_DIR/../.." && pwd)"
PROJECT="$REPO_ROOT/src/Arpoon/Arpoon.csproj"
WORK_DIR="$SCRIPT_DIR/.build"
APP_DIR="$WORK_DIR/Arpoon.app"
VERSION="${1:-1.0.0}"

echo -e "${CYAN}Arpoon .dmg builder${NC}"

if ! command -v dotnet &>/dev/null; then
    echo -e "${YELLOW}[*] .NET SDK not found. Installing via Homebrew...${NC}"
    if ! command -v brew &>/dev/null; then
        echo -e "${RED}[ERROR] Homebrew not found. Install it from https://brew.sh then re-run.${NC}"
        exit 1
    fi
    brew install --cask dotnet-sdk
fi

echo -e "${YELLOW}[1/5] Publishing self-contained osx-x64 build...${NC}"
dotnet publish "$PROJECT" -c Release -r osx-x64 --self-contained true \
    -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true

echo -e "${YELLOW}[2/5] Publishing self-contained osx-arm64 build...${NC}"
dotnet publish "$PROJECT" -c Release -r osx-arm64 --self-contained true \
    -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true

X64_BIN="$REPO_ROOT/src/Arpoon/bin/Release/net8.0/osx-x64/publish/Arpoon"
ARM64_BIN="$REPO_ROOT/src/Arpoon/bin/Release/net8.0/osx-arm64/publish/Arpoon"

rm -rf "$WORK_DIR"
mkdir -p "$APP_DIR/Contents/MacOS" "$APP_DIR/Contents/Resources"

echo -e "${YELLOW}[3/5] Creating universal (x64 + arm64) binary with lipo...${NC}"
lipo -create -output "$APP_DIR/Contents/MacOS/Arpoon" "$X64_BIN" "$ARM64_BIN"
chmod +x "$APP_DIR/Contents/MacOS/Arpoon"

echo -e "${YELLOW}[4/5] Assembling Arpoon.app...${NC}"
cat > "$APP_DIR/Contents/Info.plist" <<PLIST
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
<dict>
    <key>CFBundleName</key><string>Arpoon</string>
    <key>CFBundleDisplayName</key><string>Arpoon</string>
    <key>CFBundleExecutable</key><string>launch.sh</string>
    <key>CFBundleIconFile</key><string>app_icon</string>
    <key>CFBundleIdentifier</key><string>com.arpoon.app</string>
    <key>CFBundlePackageType</key><string>APPL</string>
    <key>CFBundleShortVersionString</key><string>$VERSION</string>
    <key>LSUIElement</key><false/>
</dict>
</plist>
PLIST

# The real binary needs a native admin prompt (not a Terminal sudo session) to
# get packet-capture privileges, so launch.sh is the bundle's actual entry point.
cat > "$APP_DIR/Contents/MacOS/launch.sh" <<'SCRIPT'
#!/bin/bash
DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
BIN="$DIR/Arpoon"
CURRENT_FWD=$(sysctl -n net.inet.ip.forwarding)
osascript -e "do shell script \"sysctl -w net.inet.ip.forwarding=1 >/dev/null 2>&1; '$BIN'; sysctl -w net.inet.ip.forwarding=$CURRENT_FWD >/dev/null 2>&1\" with administrator privileges"
SCRIPT
chmod +x "$APP_DIR/Contents/MacOS/launch.sh"

if [ -f "$REPO_ROOT/src/Arpoon/Assets/app_icon.icns" ]; then
    cp "$REPO_ROOT/src/Arpoon/Assets/app_icon.icns" "$APP_DIR/Contents/Resources/app_icon.icns"
fi

# Ad-hoc code sign (no Apple Developer certificate needed). Without any
# signature at all, Gatekeeper on newer macOS can refuse to open the app
# outright ("is damaged and can't be opened") instead of just warning about
# an unidentified developer -- the macOS analogue of the Windows manifest
# bug that blocked Arpoon.exe from launching at all.
if command -v codesign &>/dev/null; then
    codesign --force --deep --sign - "$APP_DIR"
fi

echo -e "${YELLOW}[5/5] Packaging Arpoon.dmg...${NC}"
DMG_STAGE="$WORK_DIR/dmg-stage"
mkdir -p "$DMG_STAGE"
cp -R "$APP_DIR" "$DMG_STAGE/"
ln -s /Applications "$DMG_STAGE/Applications"

OUT_DMG="$SCRIPT_DIR/Arpoon-$VERSION.dmg"
rm -f "$OUT_DMG"
hdiutil create -volname "Arpoon" -srcfolder "$DMG_STAGE" -ov -format UDZO "$OUT_DMG"

echo ""
echo -e "${GREEN}Done! Installer created at:${NC}"
echo -e "${GREEN}  $OUT_DMG${NC}"
echo -e "Open it and drag Arpoon.app to Applications."
