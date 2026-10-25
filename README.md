# Arpoon

Cross-platform network bandwidth diagnostics, device discovery, and ARP traffic management for local networks, built with .NET 8 and Avalonia UI.

> [!IMPORTANT]
> **Ethical and Authorized Usage:** Arpoon is designed strictly for authorized network diagnostics, administrative troubleshooting, and educational research in controlled laboratory environments. Executing ARP cache redirection or packet interception on networks without explicit, documented permission from the network owner is illegal and unethical.

---

## Architecture & Cross-Platform Support

| Component | Technology |
| :--- | :--- |
| **Runtime Framework** | **.NET 8.0 (LTS)** |
| **User Interface** | **Avalonia UI** (cross-platform XAML) |
| **Packet Capture Engine** | **SharpPcap** (over native `libpcap` / `Npcap`) |
| **Operating Systems** | **Linux (x64), Windows (x64), macOS (arm64 & x64)** |
| **Packaging** | **Single-file self-contained binaries** |

---

## Standalone Release Packages

Standalone release archives do **not** require the .NET SDK or runtime to be pre-installed. All runtime dependencies and native graphics libraries are bundled directly into the distribution.

### 1. Linux (x86_64)

- **Package:** `Arpoon-linux-x64.tar.gz`
- **Prerequisites:**
  - `libpcap` runtime (installed by default on most distributions; on Ubuntu/Debian: `sudo apt install libpcap0.8` or `sudo apt install libpcap-dev`).
- **Execution:**
  - Launch with graphical superuser elevation:
    ```bash
    pkexec ./Arpoon
    ```
    or via terminal:
    ```bash
    sudo -E ./Arpoon
    ```
  - **Running without sudo (Linux Capabilities):** You can assign network capabilities directly to the binary to run as an unprivileged user:
    ```bash
    sudo setcap cap_net_raw,cap_net_admin=eip ./Arpoon
    ./Arpoon
    ```
- **Desktop Application Launcher (Optional):**
  The Linux release archive includes `arpoon.desktop` and `arpoon.png`. To register the application in your desktop environment (GNOME, KDE, XFCE):
  ```bash
  mkdir -p ~/.local/share/applications ~/.local/share/icons/hicolor/256x256/apps
  cp arpoon.desktop ~/.local/share/applications/
  cp arpoon.png ~/.local/share/icons/hicolor/256x256/apps/arpoon.png
  update-desktop-database ~/.local/share/applications/
  ```
- **IP Packet Forwarding (Kernel):** Enable packet routing so traffic passes uninterrupted through your machine during diagnostics:
  ```bash
  sudo sysctl -w net.ipv4.ip_forward=1
  ```

---

### 2. Windows (x86_64)

- **Package:** `Arpoon-windows-x64.zip`
- **Prerequisites:**
  - **Npcap Driver:** Download and install Npcap from [npcap.com](https://npcap.com).
  - **CRITICAL:** During the Npcap installation wizard, you must check the option:
    **"Install Npcap in WinPcap API-compatible Mode"**. This ensures `wpcap.dll` and `packet.dll` are accessible to SharpPcap.
- **Execution:**
  1. Extract `Arpoon-windows-x64.zip`.
  2. Right-click on `Arpoon.exe`.
  3. Select **"Run as administrator"**. Elevated User Account Control (UAC) permissions are mandatory on Windows to open raw network adapters.

---

### 3. macOS (Apple Silicon arm64 & Intel x64)

- **Packages:**
  - Apple Silicon (M1, M2, M3, M4): `Arpoon-macos-arm64.tar.gz`
  - Intel x86_64: `Arpoon-macos-x64.tar.gz`
- **Prerequisites:**
  - macOS 11.0 (Big Sur) or later.
  - Native Berkeley Packet Filter (BPF) capture requires root access.
- **Execution:**
  1. Extract the archive:
     ```bash
     tar -xzf Arpoon-macos-arm64.tar.gz
     cd dist/osx-arm64
     ```
  2. If macOS Gatekeeper marks the unnotarized binary as quarantined:
     ```bash
     xattr -d com.apple.quarantine ./Arpoon
     ```
  3. Grant executable permissions:
     ```bash
     chmod +x ./Arpoon
     ```
  4. Launch with root privileges:
     ```bash
     sudo ./Arpoon
     ```
- **IP Packet Forwarding:**
  ```bash
  sudo sysctl -w net.inet.ip.forwarding=1
  ```

---

## Cryptographic Verification

Verify the integrity of downloaded distribution packages using SHA-256:

- **Linux / macOS:**
  ```bash
  sha256sum -c SHA256SUMS
  ```
- **Windows (PowerShell):**
  ```powershell
  Get-FileHash .\Arpoon-windows-x64.zip -Algorithm SHA256
  ```

---

## Building from Source

### Prerequisites
- [.NET 8.0 SDK](https://dotnet.microsoft.com/download/dotnet/8.0)
- Platform network drivers (`libpcap-dev` on Linux, Npcap on Windows)

### 1. Clone Repository
```bash
# Skip this if you already have a local checkout.
git clone <repo-url> Arpoon
cd Arpoon
```

### 2. Build and Run
```bash
# Build entire solution (produces Arpoon binary)
dotnet build Arpoon.sln

# Run application locally
dotnet run --project src/Arpoon/Arpoon.csproj
```

### 3. Run Automated Tests
The repository includes an automated test suite covering concurrency, rate calculation, OUI lookups, heuristic classification, and boundary conditions:
```bash
dotnet test Arpoon.sln
```

### 4. Publish Standalone Binaries
To compile single-file, self-contained binaries for target platforms:

```bash
# Linux x64 (produces dist/linux-x64/Arpoon)
dotnet publish src/Arpoon/Arpoon.csproj -c Release -r linux-x64 --self-contained -p:PublishSingleFile=true -p:EnableCompressionInSingleFile=true -o dist/linux-x64

# Windows x64 (produces dist/win-x64/Arpoon.exe)
dotnet publish src/Arpoon/Arpoon.csproj -c Release -r win-x64 --self-contained -p:PublishSingleFile=true -p:EnableCompressionInSingleFile=true -o dist/win-x64

# macOS Apple Silicon (produces dist/osx-arm64/Arpoon)
dotnet publish src/Arpoon/Arpoon.csproj -c Release -r osx-arm64 --self-contained -p:PublishSingleFile=true -p:EnableCompressionInSingleFile=true -o dist/osx-arm64

# macOS Intel (produces dist/osx-x64/Arpoon)
dotnet publish src/Arpoon/Arpoon.csproj -c Release -r osx-x64 --self-contained -p:PublishSingleFile=true -p:EnableCompressionInSingleFile=true -o dist/osx-x64
```

### 5. Build One-Click Installers

For an end-user `.exe` or `.dmg` instead of a raw binary:

```bash
# Windows: produces packaging/windows/dist/Arpoon-Setup-x64.exe
# Buildable from Linux/macOS/WSL -- NSIS ships a native Linux compiler, unlike
# Inno Setup (an alternative .iss/.ps1 pair is also in packaging/windows/ for
# Windows hosts that prefer it).
./packaging/windows/build_installer.sh

# macOS, run ON a Mac: produces packaging/macos/dist/Arpoon-<version>.dmg (universal)
./packaging/macos/build_dmg.sh
# macOS, run from Linux instead (hdiutil/lipo aren't available there, so this
# produces two separate per-architecture .dmg files rather than one universal one):
./packaging/macos/build_dmg_linux.sh

# Linux (Debian/Ubuntu and derivatives): produces packaging/linux/arpoon_<version>_amd64.deb
./packaging/linux/build_deb.sh
```

All of these publish a self-contained build (no .NET runtime required on the target machine) and wrap it:
- **Windows**: compiles an NSIS installer with Start Menu/Desktop shortcuts and an uninstaller, and bundles the real Visual C++ Redistributable and Npcap installers so it installs them itself at approval instead of just linking to their download pages (VC++ installs silently, per Microsoft's own supported pattern for bundled redistributables; Npcap launches its own setup UI, since its license requires an interactive install rather than a silent one).
- **macOS**: produces `Arpoon.app` bundled into a `.dmg`, which prompts for admin rights via a native dialog instead of `sudo` in Terminal.
- **Linux**: produces a `.deb` installable with `sudo apt install ./arpoon_*.deb`. It adds an app-menu entry and, on install, grants the binary `cap_net_raw`/`cap_net_admin` directly, so launching it never needs `sudo`/`pkexec` or a password prompt.

---

## License

This project is licensed under the MIT License. See the [LICENSE](LICENSE) file for details.
