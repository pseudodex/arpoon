#!/bin/bash
# ═══════════════════════════════════════════════════════
#  Arpoon - Windows installer builder (NSIS, Linux/macOS-buildable)
#
#  Unlike Inno Setup's ISCC.exe (Windows/Wine only), NSIS ships an
#  official Linux-native `makensis` compiler, so this builds the real
#  Arpoon-Setup-x64.exe without needing Windows or Wine.
#
#  Bundles the real Visual C++ Redistributable and Npcap installers so
#  the generated Setup.exe installs them itself (with approval) instead
#  of just linking to their download pages.
#
#  Run with: ./packaging/windows/build_installer.sh
#  Requires: .NET 8 SDK, and (on Debian/Ubuntu) the `nsis` package --
#  if it isn't installed system-wide, this extracts it locally itself
#  (no root needed) using an unprivileged mount namespace, since NSIS's
#  Debian build hardcodes /usr/share/nsis as its data directory.
# ═══════════════════════════════════════════════════════

set -euo pipefail

GREEN='\033[0;32m'; YELLOW='\033[1;33m'; RED='\033[0;31m'; CYAN='\033[0;36m'; NC='\033[0m'

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd "$SCRIPT_DIR/../.." && pwd)"
PROJECT="$REPO_ROOT/src/Arpoon/Arpoon.csproj"
VENDOR_DIR="$SCRIPT_DIR/vendor"
NSIS_CACHE="$HOME/.cache/arpoon-nsis"

echo -e "${CYAN}Arpoon Windows installer builder (NSIS)${NC}"

if ! command -v dotnet &>/dev/null; then
    echo -e "${RED}[ERROR] .NET SDK not found. Install .NET 8 SDK first.${NC}"
    exit 1
fi

echo -e "${YELLOW}[1/4] Publishing self-contained win-x64 build...${NC}"
dotnet publish "$PROJECT" -c Release -r win-x64 --self-contained true \
    -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true \
    -p:EnableCompressionInSingleFile=true

echo -e "${YELLOW}[2/4] Fetching bundled dependency installers (VC++ Redist, Npcap)...${NC}"
mkdir -p "$VENDOR_DIR"
if [ ! -f "$VENDOR_DIR/vc_redist.x64.exe" ]; then
    curl -sL -o "$VENDOR_DIR/vc_redist.x64.exe" "https://aka.ms/vs/17/release/vc_redist.x64.exe"
fi
if [ ! -f "$VENDOR_DIR/npcap-setup.exe" ]; then
    NPCAP_FILE=$(curl -sL "https://npcap.com/#download" | grep -oE 'dist/npcap-[0-9][0-9.]*\.exe' | head -1)
    curl -sL -o "$VENDOR_DIR/npcap-setup.exe" "https://npcap.com/${NPCAP_FILE}"
fi

echo -e "${YELLOW}[3/4] Ensuring makensis is available...${NC}"
MAKENSIS="$(command -v makensis || true)"
NEEDS_NAMESPACE=0
if [ -z "$MAKENSIS" ]; then
    if [ ! -x "$NSIS_CACHE/usr/bin/makensis" ]; then
        mkdir -p "$NSIS_CACHE"
        TMPDL="$(mktemp -d)"
        (cd "$TMPDL" && apt-get download nsis nsis-common nsis-pluginapi)
        for f in "$TMPDL"/*.deb; do dpkg-deb -x "$f" "$NSIS_CACHE"; done
        rm -rf "$TMPDL"
    fi
    MAKENSIS="$NSIS_CACHE/usr/bin/makensis"
    NEEDS_NAMESPACE=1
fi

echo -e "${YELLOW}[4/4] Compiling the installer...${NC}"
mkdir -p "$SCRIPT_DIR/dist"
if [ "$NEEDS_NAMESPACE" = "1" ]; then
    # This NSIS build hardcodes /usr/share/nsis as its data directory; an
    # unprivileged mount namespace lets us point that at our extracted copy
    # without needing real root.
    unshare --map-root-user --mount bash -c "
        mount -t tmpfs tmpfs /usr/share &&
        mkdir -p /usr/share/nsis &&
        mount --bind '$NSIS_CACHE/usr/share/nsis' /usr/share/nsis &&
        cd '$SCRIPT_DIR' &&
        '$MAKENSIS' Arpoon.nsi
    "
else
    (cd "$SCRIPT_DIR" && "$MAKENSIS" Arpoon.nsi)
fi

echo ""
echo -e "${GREEN}Done! Installer created at:${NC}"
echo -e "${GREEN}  $SCRIPT_DIR/dist/Arpoon-Setup-x64.exe${NC}"
