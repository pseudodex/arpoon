#!/bin/bash
# ═══════════════════════════════════════════════════════
#  Arpoon - macOS .dmg builder, buildable from Linux
#
#  build_dmg.sh (in this same folder) needs to run ON a Mac (hdiutil/lipo
#  are macOS-only). This script instead builds real, valid Apple Disk
#  Images from Linux using the same technique projects like Bitcoin Core
#  use to produce macOS installers from Linux CI: a hybrid ISO9660/HFS
#  image (genisoimage -apple) piped through libdmg-hfsplus's `dmg` tool,
#  which wraps it in a genuine UDIF container (verifiable by its "koly"
#  trailer). Produces separate Intel and Apple Silicon .dmg files since
#  `lipo` (for a universal binary) isn't available on Linux.
#
#  Run with: ./packaging/macos/build_dmg_linux.sh
#  Requires: .NET 8 SDK, a C build toolchain (gcc/cmake/make), git.
#  Also downloads a pinned, checksum-verified `rcodesign` (apple-codesign)
#  to ad-hoc sign the app binary, which Apple Silicon requires to execute at
#  all, so users don't have to run xattr/codesign themselves.
#  Installs genisoimage locally via `apt-get download` if missing (no
#  root needed -- it's only extracted, never installed system-wide).
# ═══════════════════════════════════════════════════════

set -euo pipefail

GREEN='\033[0;32m'; YELLOW='\033[1;33m'; RED='\033[0;31m'; CYAN='\033[0;36m'; NC='\033[0m'

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd "$SCRIPT_DIR/../.." && pwd)"
PROJECT="$REPO_ROOT/src/Arpoon/Arpoon.csproj"
VERSION="${1:-1.0.1}"
CACHE_DIR="$HOME/.cache/arpoon-macos-build"
WORK_DIR="$SCRIPT_DIR/.build-linux"

echo -e "${CYAN}Arpoon macOS .dmg builder (from Linux)${NC}"

if ! command -v dotnet &>/dev/null; then
    echo -e "${RED}[ERROR] .NET SDK not found. Install .NET 8 SDK first.${NC}"
    exit 1
fi

echo -e "${YELLOW}[1/5] Ensuring genisoimage is available...${NC}"
GENISO="$(command -v genisoimage || true)"
if [ -z "$GENISO" ]; then
    if [ ! -x "$CACHE_DIR/genisoimage/usr/bin/genisoimage" ]; then
        mkdir -p "$CACHE_DIR/genisoimage"
        TMPDL="$(mktemp -d)"
        (cd "$TMPDL" && apt-get download genisoimage)
        dpkg-deb -x "$TMPDL"/*.deb "$CACHE_DIR/genisoimage"
        rm -rf "$TMPDL"
    fi
    GENISO="$CACHE_DIR/genisoimage/usr/bin/genisoimage"
fi

echo -e "${YELLOW}[2/5] Ensuring the libdmg-hfsplus 'dmg' tool is built...${NC}"
DMG_TOOL="$CACHE_DIR/libdmg-hfsplus/build/dmg/dmg"
if [ ! -x "$DMG_TOOL" ]; then
    mkdir -p "$CACHE_DIR"
    rm -rf "$CACHE_DIR/libdmg-hfsplus"
    git clone --depth 1 https://github.com/fanquake/libdmg-hfsplus.git "$CACHE_DIR/libdmg-hfsplus"
    cmake "$CACHE_DIR/libdmg-hfsplus" -B "$CACHE_DIR/libdmg-hfsplus/build"
    make -C "$CACHE_DIR/libdmg-hfsplus/build/dmg" -j"$(nproc)"
fi

RCODESIGN_VERSION="0.29.0"
RCODESIGN="$CACHE_DIR/rcodesign-$RCODESIGN_VERSION/rcodesign"
ensure_rcodesign() {
    [ -x "$RCODESIGN" ] && return
    local name="apple-codesign-$RCODESIGN_VERSION-x86_64-unknown-linux-musl"
    local url="https://github.com/indygreg/apple-platform-rs/releases/download/apple-codesign/$RCODESIGN_VERSION/$name.tar.gz"
    local tmp; tmp="$(mktemp -d)"
    curl -sL -o "$tmp/rc.tar.gz" "$url"
    curl -sL -o "$tmp/rc.sha256" "$url.sha256"
    [ "$(sha256sum "$tmp/rc.tar.gz" | cut -d' ' -f1)" = "$(cut -d' ' -f1 "$tmp/rc.sha256")" ] \
        || { echo -e "${RED}[ERROR] rcodesign checksum mismatch.${NC}"; rm -rf "$tmp"; exit 1; }
    mkdir -p "$CACHE_DIR/rcodesign-$RCODESIGN_VERSION"
    tar -xzf "$tmp/rc.tar.gz" -C "$CACHE_DIR/rcodesign-$RCODESIGN_VERSION" --strip-components=1
    rm -rf "$tmp"
}

build_arch() {
    local rid="$1" label="$2"
    echo -e "${YELLOW}[3/5] Publishing self-contained $rid build...${NC}"
    dotnet publish "$PROJECT" -c Release -r "$rid" --self-contained true \
        -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true

    echo -e "${YELLOW}[4/5] Staging Arpoon.app ($label)...${NC}"
    local stage="$WORK_DIR/$rid"
    local app="$stage/Arpoon.app"
    rm -rf "$stage"
    mkdir -p "$app/Contents/MacOS" "$app/Contents/Resources"
    cp "$REPO_ROOT/src/Arpoon/bin/Release/net8.0/$rid/publish/Arpoon" "$app/Contents/MacOS/Arpoon"
    chmod +x "$app/Contents/MacOS/Arpoon"
    cp "$REPO_ROOT/src/Arpoon/Assets/app_icon.icns" "$app/Contents/Resources/app_icon.icns"

    cat > "$app/Contents/Info.plist" <<PLIST
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

    # See build_dmg.sh for why this launches via `launchctl asuser <uid>` rather
    # than invoking the binary directly: a GUI app started by `do shell script
    # ... with administrator privileges` runs root but off the window server and
    # never shows a window. asuser re-attaches the root process to the user's
    # Aqua GUI session.
    cat > "$app/Contents/MacOS/launch.sh" <<'SCRIPT'
#!/bin/bash
DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
BIN="$DIR/Arpoon"
UID_NUM="$(id -u)"
CURRENT_FWD=$(sysctl -n net.inet.ip.forwarding)
osascript -e "do shell script \"sysctl -w net.inet.ip.forwarding=1 >/dev/null 2>&1; launchctl asuser $UID_NUM '$BIN'; sysctl -w net.inet.ip.forwarding=$CURRENT_FWD >/dev/null 2>&1\" with administrator privileges"
SCRIPT
    chmod +x "$app/Contents/MacOS/launch.sh"

    # Sign just the Mach-O: the bundle's CFBundleExecutable is a shell script
    # (launch.sh), which rcodesign can't sign as a bundle's main executable.
    # The .NET single-file payload stays at the same offset (appended
    # signature only extends the file), so the app still finds it.
    echo -e "${YELLOW}      Ad-hoc signing the Arpoon binary...${NC}"
    ensure_rcodesign
    "$RCODESIGN" sign --binary-identifier com.arpoon.app \
        "$app/Contents/MacOS/Arpoon" "$app/Contents/MacOS/Arpoon.signed" >/dev/null 2>&1
    mv "$app/Contents/MacOS/Arpoon.signed" "$app/Contents/MacOS/Arpoon"
    chmod +x "$app/Contents/MacOS/Arpoon"
    ln -s /Applications "$stage/Applications"

    echo -e "${YELLOW}[5/5] Packaging Arpoon-$VERSION-$label.dmg...${NC}"
    local iso="$WORK_DIR/$rid.iso"
    local out="$SCRIPT_DIR/dist/Arpoon-$VERSION-$label.dmg"
    mkdir -p "$SCRIPT_DIR/dist"
    "$GENISO" -V "Arpoon" -apple -r -no-pad -o "$iso" "$stage" >/dev/null
    "$DMG_TOOL" "$iso" "$out"
    echo -e "${GREEN}  -> $out${NC}"
}

mkdir -p "$WORK_DIR"
build_arch osx-arm64 arm64
build_arch osx-x64 x64

echo ""
echo -e "${GREEN}Done! Two installers (no universal binary -- lipo isn't available on Linux):${NC}"
echo -e "${GREEN}  $SCRIPT_DIR/dist/Arpoon-$VERSION-arm64.dmg  (Apple Silicon)${NC}"
echo -e "${GREEN}  $SCRIPT_DIR/dist/Arpoon-$VERSION-x64.dmg    (Intel)${NC}"
echo ""
echo -e "${YELLOW}Note: the app binary is ad-hoc signed (no Apple Developer ID), so the first launch${NC}"
echo -e "${YELLOW}needs right-click > Open. Notarization requires a paid Apple Developer account.${NC}"
