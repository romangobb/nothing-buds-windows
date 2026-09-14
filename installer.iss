; Inno Setup script for NothingBuds (optional - requires Inno Setup 6).
; Run: iscc installer.iss   (output: installer\NothingBuds-Setup-0.1.0.exe)
#define AppVersion "0.1.0"

[Setup]
AppName=NothingBuds
AppVersion={#AppVersion}
AppPublisher=NothingBuds (unofficial)
DefaultDirName={localappdata}\Programs\NothingBuds
DefaultGroupName=NothingBuds
PrivilegesRequired=lowest
OutputDir=installer
OutputBaseFilename=NothingBuds-Setup-{#AppVersion}
Compression=lzma2
SolidCompression=yes
WizardStyle=modern

[Files]
Source: "publish\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs

[Icons]
Name: "{group}\NothingBuds"; Filename: "{app}\NothingBuds.exe"
Name: "{group}\Uninstall NothingBuds"; Filename: "{uninstallexe}"

[Run]
Filename: "{app}\NothingBuds.exe"; Description: "Launch NothingBuds now"; Flags: nowait postinstall skipifsilent
