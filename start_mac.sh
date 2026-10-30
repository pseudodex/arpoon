#!/bin/bash
# ═══════════════════════════════════════════════════════
#  Arpoon - macOS Launcher
#  Enables IP forwarding and runs with sudo
# ═══════════════════════════════════════════════════════

set -e

GREEN='\033[0;32m'
YELLOW='\033[1;33m'
RED='\033[0;31m'
CYAN='\033[0;36m'
NC='\033[0m'

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
PROJECT_DIR="$SCRIPT_DIR/src/Arpoon"
BINARY="$PROJECT_DIR/bin/Release/net8.0/Arpoon"

echo -e "${CYAN}"
echo "╔══════════════════════════════════════╗"
echo "║        Arpoon - macOS Launcher         ║"
echo "╚══════════════════════════════════════╝"
echo -e "${NC}"

# ── Check if built ──
if [ ! -f "$BINARY" ] && [ ! -f "$BINARY.dll" ]; then
    echo -e "${RED}[ERROR] Arpoon not built. Run ./install_mac.sh first.${NC}"
    exit 1
fi

# Resolve a symlink chain to its real target without relying on GNU
# `readlink -f` (BSD/macOS readlink has no -f).
resolve_path() {
    local p="$1" dir base
    while [ -L "$p" ]; do
        dir="$(cd "$(dirname "$p")" && pwd -P)"
        base="$(readlink "$p")"
        case "$base" in
            /*) p="$base" ;;
            *)  p="$dir/$base" ;;
        esac
    done
    dir="$(cd "$(dirname "$p")" && pwd -P)"
    echo "$dir/$(basename "$p")"
}

# ── Check for root ──
if [ "$EUID" -ne 0 ]; then
    # Resolve the .NET install root *now*, while the user's PATH is intact.
    # sudo resets PATH to its secure_path, which does NOT include Homebrew's
    # dotnet (/opt/homebrew/bin, /usr/local/share/dotnet), so without this the
    # elevated shell can't find dotnet and the launch silently dies right after
    # the password prompt.
    DOTNET_BIN="$(command -v dotnet || true)"
    if [ -z "$DOTNET_BIN" ] && [ ! -f "$BINARY" ]; then
        echo -e "${RED}[ERROR] 'dotnet' not found on PATH. Run ./install_mac.sh first.${NC}"
        exit 1
    fi
    DOTNET_ROOT_DIR=""
    if [ -n "$DOTNET_BIN" ]; then
        DOTNET_ROOT_DIR="$(dirname "$(resolve_path "$DOTNET_BIN")")"
    fi

    echo -e "${YELLOW}[!] Arpoon requires root privileges for network access.${NC}"
    echo -e "${YELLOW}    Relaunching with sudo...${NC}"
    echo ""
    # Pass DOTNET_ROOT and a PATH that includes it through to the root shell so
    # both the apphost binary and `dotnet` can locate the runtime.
    exec sudo DOTNET_ROOT="$DOTNET_ROOT_DIR" PATH="${DOTNET_ROOT_DIR}:$PATH" bash "$0" "$@"
fi

# ── Enable IP forwarding (required for MITM on macOS) ──
echo -e "${YELLOW}[1/2] Enabling IP forwarding...${NC}"
CURRENT_FWD=$(sysctl -n net.inet.ip.forwarding)
sysctl -w net.inet.ip.forwarding=1 > /dev/null 2>&1
echo -e "${GREEN}[OK] IP forwarding enabled.${NC}"

# ── Launch ──
echo -e "${YELLOW}[2/2] Launching Arpoon...${NC}"
echo ""

cd "$PROJECT_DIR"
# Launch the already-built apphost directly rather than `dotnet run`. Under root
# `dotnet run` re-evaluates the project, triggers the .NET first-run experience,
# and resolves the runtime via root's HOME/DOTNET_ROOT — fragile and slow. The
# apphost needs none of that; DOTNET_ROOT (exported above) points it at the
# runtime. Fall back to `dotnet <dll>` only if the apphost is missing.
set +e
if [ -f "$BINARY" ]; then
    "$BINARY" "$@"
else
    "$(command -v dotnet || echo "${DOTNET_ROOT}/dotnet")" "$BINARY.dll" "$@"
fi
EXIT_CODE=$?
set -e

# ── Restore IP forwarding to previous state ──
sysctl -w net.inet.ip.forwarding=$CURRENT_FWD > /dev/null 2>&1
echo -e "${GREEN}[OK] IP forwarding restored to previous state ($CURRENT_FWD).${NC}"

exit $EXIT_CODE
