; Arpoon Windows installer (Inno Setup 6).
; Build with: packaging\windows\build_installer.ps1 (does the dotnet publish + ISCC run for you),
; or manually: iscc packaging\windows\Arpoon.iss  (after publishing Arpoon.exe yourself).

#define MyAppName "Arpoon"
#ifndef MyAppVersion
  #define MyAppVersion "1.0.2"
#endif
#define MyAppExeName "Arpoon.exe"
#define MyPublishDir "..\..\src\Arpoon\bin\Release\net8.0\win-x64\publish"

[Setup]
AppId={{25199C4A-D678-476B-8B68-CE6A3997CE5E}}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppPublisher=Arpoon
DefaultDirName={autopf}\{#MyAppName}
DefaultGroupName={#MyAppName}
DisableProgramGroupPage=yes
OutputBaseFilename=Arpoon-Setup-x64
OutputDir=dist
Compression=lzma2
SolidCompression=yes
ArchitecturesAllowed=x64
ArchitecturesInstallIn64BitMode=x64
UninstallDisplayIcon={app}\{#MyAppExeName}
SetupIconFile=..\..\src\Arpoon\Assets\app_icon.ico
; Arpoon.exe requests elevation itself via its manifest (requireAdministrator),
; so every launch already gets a UAC prompt without any script wrapper.
PrivilegesRequired=admin

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Files]
Source: "{#MyPublishDir}\{#MyAppExeName}"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
Name: "{group}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"
Name: "{group}\Uninstall {#MyAppName}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; Tasks: desktopicon

[Tasks]
Name: "desktopicon"; Description: "Create a &desktop shortcut"; GroupDescription: "Additional shortcuts:"

[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "Launch Arpoon now"; Flags: nowait postinstall skipifsilent

[Code]
function NpcapInstalled: Boolean;
begin
  Result := FileExists(ExpandConstant('{sys}\Npcap\wpcap.dll')) or FileExists(ExpandConstant('{sys}\wpcap.dll'));
end;

procedure CurStepChanged(CurStep: TSetupStep);
var
  ErrorCode: Integer;
begin
  if (CurStep = ssPostInstall) and (not NpcapInstalled) then
  begin
    if MsgBox('Arpoon needs Npcap to capture network traffic, and it was not detected on this system.' + #13#10 + #13#10 +
              'Open the Npcap download page now? During its setup, check "Install Npcap in WinPcap API-compatible Mode".',
              mbConfirmation, MB_YESNO) = IDYES then
      ShellExec('open', 'https://npcap.com/#download', '', '', SW_SHOW, ewNoWait, ErrorCode);
  end;
end;
