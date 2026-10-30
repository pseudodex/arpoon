; Arpoon Windows installer (NSIS). Builds natively on Linux/macOS/Windows via
; makensis -- unlike Inno Setup's ISCC.exe, no Wine or Windows host is needed.
;
; Build with: packaging/windows/build_installer.sh (handles the dotnet publish
; + makensis run for you), or manually:
;   makensis packaging/windows/Arpoon.nsi
; (after publishing a self-contained win-x64 build to
;  src/Arpoon/bin/Release/net8.0/win-x64/publish/Arpoon.exe yourself).

!define APP_NAME "Arpoon"
!define APP_VERSION "1.0.2"
!define APP_EXE "Arpoon.exe"
!define PUBLISH_DIR "..\..\src\Arpoon\bin\Release\net8.0\win-x64\publish"
!define VENDOR_DIR "vendor"

Name "${APP_NAME}"
OutFile "dist\Arpoon-Setup-x64.exe"
InstallDir "$PROGRAMFILES64\${APP_NAME}"
InstallDirRegKey HKLM "Software\${APP_NAME}" "InstallDir"
RequestExecutionLevel admin
SetCompressor /SOLID lzma

!include "MUI2.nsh"

!define MUI_ICON "..\..\src\Arpoon\Assets\app_icon.ico"
!define MUI_UNICON "..\..\src\Arpoon\Assets\app_icon.ico"
!define MUI_ABORTWARNING

!insertmacro MUI_PAGE_WELCOME
!insertmacro MUI_PAGE_DIRECTORY
!insertmacro MUI_PAGE_INSTFILES
!define MUI_FINISHPAGE_RUN "$INSTDIR\${APP_EXE}"
!define MUI_FINISHPAGE_RUN_TEXT "Launch ${APP_NAME} now"
!insertmacro MUI_PAGE_FINISH

!insertmacro MUI_UNPAGE_CONFIRM
!insertmacro MUI_UNPAGE_INSTFILES

!insertmacro MUI_LANGUAGE "English"

Section "Install"
    ; This installer is a 32-bit process (standard for NSIS); without this,
    ; its HKLM\Software writes get WOW64-redirected into \WOW6432Node. Windows
    ; merges both locations for Programs and Features either way, but writing
    ; straight to the native 64-bit view is clearer and avoids relying on that.
    SetRegView 64
    SetOutPath "$INSTDIR"
    File "${PUBLISH_DIR}\${APP_EXE}"

    CreateDirectory "$SMPROGRAMS\${APP_NAME}"
    CreateShortcut "$SMPROGRAMS\${APP_NAME}\${APP_NAME}.lnk" "$INSTDIR\${APP_EXE}"
    CreateShortcut "$SMPROGRAMS\${APP_NAME}\Uninstall ${APP_NAME}.lnk" "$INSTDIR\Uninstall.exe"
    CreateShortcut "$DESKTOP\${APP_NAME}.lnk" "$INSTDIR\${APP_EXE}"

    WriteRegStr HKLM "Software\${APP_NAME}" "InstallDir" "$INSTDIR"
    WriteUninstaller "$INSTDIR\Uninstall.exe"

    WriteRegStr HKLM "Software\Microsoft\Windows\CurrentVersion\Uninstall\${APP_NAME}" "DisplayName" "${APP_NAME}"
    WriteRegStr HKLM "Software\Microsoft\Windows\CurrentVersion\Uninstall\${APP_NAME}" "UninstallString" "$INSTDIR\Uninstall.exe"
    WriteRegStr HKLM "Software\Microsoft\Windows\CurrentVersion\Uninstall\${APP_NAME}" "DisplayIcon" "$INSTDIR\${APP_EXE}"
    WriteRegStr HKLM "Software\Microsoft\Windows\CurrentVersion\Uninstall\${APP_NAME}" "DisplayVersion" "${APP_VERSION}"
    WriteRegStr HKLM "Software\Microsoft\Windows\CurrentVersion\Uninstall\${APP_NAME}" "Publisher" "Arpoon"

    ; SkiaSharp (the UI's rendering library) needs the Microsoft Visual C++
    ; Redistributable's native runtime DLLs, which --self-contained does NOT
    ; bundle (that only bundles the .NET runtime itself). Without it, Windows
    ; fails to launch the app with "side-by-side configuration is incorrect".
    ; The redistributable is bundled and installed here, silently -- Microsoft
    ; explicitly designs/documents it for exactly this kind of bundled install.
    IfFileExists "$SYSDIR\vcruntime140_1.dll" VCRedistFound
    MessageBox MB_YESNO|MB_ICONQUESTION \
        "Arpoon needs the Microsoft Visual C++ Redistributable (x64) to run, and it was not detected on this system.$\r$\n$\r$\nInstall it now?" \
        IDNO VCRedistFound
    SetOutPath "$TEMP"
    File "${VENDOR_DIR}\vc_redist.x64.exe"
    DetailPrint "Installing Microsoft Visual C++ Redistributable..."
    ExecWait '"$TEMP\vc_redist.x64.exe" /install /quiet /norestart' $0
    Delete "$TEMP\vc_redist.x64.exe"
    SetOutPath "$INSTDIR"
    ; 0 = installed, 1638 = an equal/newer version is already present -- both
    ; are fine. 3010 = installed but needs a reboot before it actually takes
    ; effect; anything else is a real failure. Either way, tell the user
    ; instead of silently launching Arpoon into the same failure again.
    StrCmp $0 "0" VCRedistFound
    StrCmp $0 "1638" VCRedistFound
    StrCmp $0 "3010" 0 +3
        MessageBox MB_OK|MB_ICONEXCLAMATION "The Visual C++ Redistributable needs a reboot to finish installing. Restart Windows, then launch Arpoon."
        Goto VCRedistFound
    MessageBox MB_OK|MB_ICONEXCLAMATION "The Visual C++ Redistributable installer exited with an error (code $0). Arpoon may fail to launch; try installing it manually from https://aka.ms/vs/17/release/vc_redist.x64.exe"
    VCRedistFound:

    ; Npcap is a separate kernel driver; its license requires an interactive
    ; install (no silent/unattended flags) rather than a fully automated one,
    ; so this bundles the real installer and launches its own setup UI for the
    ; user to approve, instead of just opening a download page in a browser.
    IfFileExists "$SYSDIR\Npcap\wpcap.dll" NpcapFound
    IfFileExists "$SYSDIR\wpcap.dll" NpcapFound
    MessageBox MB_YESNO|MB_ICONQUESTION \
        "Arpoon needs Npcap to capture network traffic, and it was not detected on this system.$\r$\n$\r$\nRun the Npcap installer now? In its setup, check 'Install Npcap in WinPcap API-compatible Mode'." \
        IDNO NpcapFound
    SetOutPath "$TEMP"
    File "${VENDOR_DIR}\npcap-setup.exe"
    DetailPrint "Launching the Npcap installer..."
    ExecWait '"$TEMP\npcap-setup.exe"' $0
    Delete "$TEMP\npcap-setup.exe"
    SetOutPath "$INSTDIR"
    NpcapFound:
SectionEnd

Section "Uninstall"
    SetRegView 64
    Delete "$INSTDIR\${APP_EXE}"
    Delete "$INSTDIR\Uninstall.exe"
    RMDir "$INSTDIR"
    Delete "$SMPROGRAMS\${APP_NAME}\${APP_NAME}.lnk"
    Delete "$SMPROGRAMS\${APP_NAME}\Uninstall ${APP_NAME}.lnk"
    RMDir "$SMPROGRAMS\${APP_NAME}"
    Delete "$DESKTOP\${APP_NAME}.lnk"
    DeleteRegKey HKLM "Software\Microsoft\Windows\CurrentVersion\Uninstall\${APP_NAME}"
    DeleteRegKey HKLM "Software\${APP_NAME}"
SectionEnd
