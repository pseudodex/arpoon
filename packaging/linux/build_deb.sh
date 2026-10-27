#!/bin/bash
# ═══════════════════════════════════════════════════════
#  Arpoon - Linux .deb builder
#  Publishes a self-contained linux-x64 build and packages it as a .deb:
#  installs to /usr/lib/arpoon, adds a menu entry + icon, and grants
#  the binary cap_net_raw/cap_net_admin on install so it runs without a
#  root/password prompt on every launch.
#
#  Run with: ./packaging/linux/build_deb.sh
#  Requires: .NET 8 SDK, dpkg-deb (both standard on Debian/Ubuntu).
# ═══════════════════════════════════════════════════════

set -euo pipefail

GREEN='\033[0;32m'; YELLOW='\033[1;33m'; RED='\033[0;31m'; CYAN='\033[0;36m'; NC='\033[0m'

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd "$SCRIPT_DIR/../.." && pwd)"
PROJECT="$REPO_ROOT/src/Arpoon/Arpoon.csproj"
VERSION="${1:-1.0.1}"
ARCH="amd64"
PKG_NAME="arpoon"
# Staged on a native filesystem (not /mnt/c under WSL, which can't hold real
# Unix permission bits) since dpkg-deb enforces exact mode bits on DEBIAN/.
BUILD_ROOT="$(mktemp -d)"
trap 'rm -rf "$BUILD_ROOT"' EXIT
STAGE_DIR="$BUILD_ROOT/${PKG_NAME}_${VERSION}_${ARCH}"

echo -e "${CYAN}Arpoon .deb builder${NC}"

if ! command -v dotnet &>/dev/null; then
    echo -e "${RED}[ERROR] .NET SDK not found. Install .NET 8 SDK first (see README).${NC}"
    exit 1
fi
if ! command -v dpkg-deb &>/dev/null; then
    echo -e "${RED}[ERROR] dpkg-deb not found. Install it with: sudo apt install dpkg-dev${NC}"
    exit 1
fi

echo -e "${YELLOW}[1/3] Publishing self-contained linux-x64 build...${NC}"
dotnet publish "$PROJECT" -c Release -r linux-x64 --self-contained true \
    -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true \
    -p:EnableCompressionInSingleFile=true

BIN_PATH="$REPO_ROOT/src/Arpoon/bin/Release/net8.0/linux-x64/publish/Arpoon"
if [ ! -f "$BIN_PATH" ]; then
    echo -e "${RED}[ERROR] Expected publish output not found at $BIN_PATH${NC}"
    exit 1
fi

echo -e "${YELLOW}[2/3] Staging package tree...${NC}"
mkdir -p "$STAGE_DIR/DEBIAN"
chmod 755 "$STAGE_DIR" "$STAGE_DIR/DEBIAN"
mkdir -p "$STAGE_DIR/usr/lib/arpoon"
mkdir -p "$STAGE_DIR/usr/bin"
mkdir -p "$STAGE_DIR/usr/share/applications"
mkdir -p "$STAGE_DIR/usr/share/icons/hicolor/512x512/apps"
mkdir -p "$STAGE_DIR/usr/share/doc/arpoon"

install -m 755 "$BIN_PATH" "$STAGE_DIR/usr/lib/arpoon/Arpoon"
ln -s "../lib/arpoon/Arpoon" "$STAGE_DIR/usr/bin/arpoon"
install -m 644 "$REPO_ROOT/arpoon.png" "$STAGE_DIR/usr/share/icons/hicolor/512x512/apps/arpoon.png"
install -m 644 "$REPO_ROOT/LICENSE" "$STAGE_DIR/usr/share/doc/arpoon/copyright"

# Exec runs the plain binary (no pkexec) since postinst grants it the raw-socket
# capabilities it needs, so there's no password prompt on every launch.
sed 's#^Exec=.*#Exec=/usr/bin/arpoon#' "$REPO_ROOT/arpoon.desktop" \
    > "$STAGE_DIR/usr/share/applications/arpoon.desktop"

cat > "$STAGE_DIR/DEBIAN/control" <<EOF
Package: ${PKG_NAME}
Version: ${VERSION}
Section: net
Priority: optional
Architecture: ${ARCH}
Depends: libpcap0.8
Maintainer: Arpoon Project <noreply@example.com>
Description: Cross-platform network bandwidth diagnostics and ARP traffic management
 Device discovery, bandwidth throttling, and ARP traffic management for local
 networks. Self-contained build; does not require a separate .NET runtime.
EOF

cat > "$STAGE_DIR/DEBIAN/postinst" <<'EOF'
#!/bin/bash
set -e
# Grant packet-capture capabilities so Arpoon runs without sudo/pkexec on
# every launch. Non-fatal if setcap/libcap2-bin isn't available (e.g. in a
# minimal container); the app itself still detects and reports this.
if command -v setcap >/dev/null 2>&1; then
    setcap cap_net_raw,cap_net_admin=eip /usr/lib/arpoon/Arpoon || true
else
    echo "Arpoon: 'setcap' not found (install libcap2-bin) -- run with sudo/pkexec instead." >&2
fi
EOF
chmod 755 "$STAGE_DIR/DEBIAN/postinst"

cat > "$STAGE_DIR/DEBIAN/postrm" <<'EOF'
#!/bin/bash
set -e
exit 0
EOF
chmod 755 "$STAGE_DIR/DEBIAN/postrm"

echo -e "${YELLOW}[3/3] Building .deb...${NC}"
OUT_DEB="$SCRIPT_DIR/${PKG_NAME}_${VERSION}_${ARCH}.deb"
dpkg-deb --root-owner-group --build "$STAGE_DIR" "$OUT_DEB"

echo ""
echo -e "${GREEN}Done! Installer created at:${NC}"
echo -e "${GREEN}  $OUT_DEB${NC}"
echo -e "Install with: sudo apt install $OUT_DEB    (or: sudo dpkg -i $OUT_DEB)"
echo -e "Then launch Arpoon from your app menu, or run: arpoon"
