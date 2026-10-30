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
VERSION="${1:-1.0.1}"

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
# NOTE: deliberately NOT a single-file publish. The previous version used
# -p:PublishSingleFile=true and then lipo'd the two resulting executables into a
# universal binary -- but a .NET single-file app appends its payload (managed
# assemblies AND native libraries like libSkiaSharp.dylib) *after* the Mach-O
# image, and `lipo` only understands Mach-O, so it silently drops that payload.
# The merged binary then crashed at startup with
# "DllNotFoundException: Unable to load shared library 'libSkiaSharp'" and never
# drew a window. A folder publish keeps every native .dylib as a real file, and
# we build the universal binary by lipo'ing the individual Mach-O files below.
X64_DIR="$WORK_DIR/publish-osx-x64"
ARM64_DIR="$WORK_DIR/publish-osx-arm64"
rm -rf "$WORK_DIR"
mkdir -p "$APP_DIR/Contents/MacOS" "$APP_DIR/Contents/Resources"

dotnet publish "$PROJECT" -c Release -r osx-x64 --self-contained true \
    -p:PublishSingleFile=false -o "$X64_DIR"

echo -e "${YELLOW}[2/5] Publishing self-contained osx-arm64 build...${NC}"
dotnet publish "$PROJECT" -c Release -r osx-arm64 --self-contained true \
    -p:PublishSingleFile=false -o "$ARM64_DIR"

echo -e "${YELLOW}[3/5] Assembling universal payload (lipo per Mach-O file)...${NC}"
# Use the arm64 publish as the template for the file set. Managed assemblies are
# architecture-neutral MSIL (identical in both publishes), so they are copied as
# is; every Mach-O file (the apphost and the native .dylibs) is merged with its
# x64 counterpart into a universal file via lipo.
cp -R "$ARM64_DIR"/. "$APP_DIR/Contents/MacOS/"
while IFS= read -r arm_file; do
    rel="${arm_file#"$ARM64_DIR"/}"
    x64_file="$X64_DIR/$rel"
    dest="$APP_DIR/Contents/MacOS/$rel"
    # Only Mach-O files need merging, and only when an x64 counterpart exists.
    file "$arm_file" | grep -q "Mach-O" || continue
    [ -f "$x64_file" ] || continue
    # SkiaSharp / HarfBuzzSharp / Avalonia native assets already ship as
    # universal (x86_64+arm64) dylibs, identical in both publishes -- leave those
    # as copied. Trying to lipo two already-universal files is a fatal error
    # ("have the same architectures ... can't be in the same fat output file").
    # Only the single-arch .NET runtime dylibs and the apphost need a real merge.
    if lipo -archs "$arm_file" | grep -qw x86_64; then
        continue
    fi
    lipo -create -output "$dest" "$arm_file" "$x64_file"
done < <(find "$ARM64_DIR" -type f)
chmod +x "$APP_DIR/Contents/MacOS/Arpoon"
echo -e "${GREEN}[OK] Universal binary: $(lipo -info "$APP_DIR/Contents/MacOS/Arpoon" | sed 's/.*: //')${NC}"

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
#
# NOTE: the privileged command launches Arpoon via `launchctl asuser <uid>`,
# NOT by invoking the binary directly. `do shell script ... with administrator
# privileges` runs as root but in a session detached from the window server, so
# a GUI app started directly there never shows a window (the admin dialog
# appears, you authorize, then nothing happens). `launchctl asuser <console-uid>
# <binary>` keeps the process running as root while attaching it to that user's
# Aqua GUI session, so the Avalonia window actually appears.
cat > "$APP_DIR/Contents/MacOS/launch.sh" <<'SCRIPT'
#!/bin/bash
DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
BIN="$DIR/Arpoon"
UID_NUM="$(id -u)"
CURRENT_FWD=$(sysctl -n net.inet.ip.forwarding)
osascript -e "do shell script \"sysctl -w net.inet.ip.forwarding=1 >/dev/null 2>&1; launchctl asuser $UID_NUM '$BIN'; sysctl -w net.inet.ip.forwarding=$CURRENT_FWD >/dev/null 2>&1\" with administrator privileges"
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
