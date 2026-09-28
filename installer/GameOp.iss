; Inno Setup script for GameOp. Built by build.ps1 (pass /DAppVersion=x.y.z /DSourceDir=<publish dir>).
#ifndef AppVersion
  #define AppVersion "1.0.0"
#endif
#ifndef SourceDir
  #define SourceDir "..\publish"
#endif

[Setup]
AppId={{7C1B7E52-3F7A-4B8E-9C61-0A5D2E6F1B34}
AppName=GameOp
AppVersion={#AppVersion}
AppVerName=GameOp {#AppVersion}
AppPublisher=GameOp
AppPublisherURL=https://github.com/nahalewski/GameOp
AppSupportURL=https://github.com/nahalewski/GameOp/issues
DefaultDirName={autopf}\GameOp
DefaultGroupName=GameOp
DisableProgramGroupPage=yes
PrivilegesRequired=admin
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.19041
OutputDir=..\dist
OutputBaseFilename=GameOp-Setup-v{#AppVersion}
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
UninstallDisplayIcon={app}\GameOp.exe
CloseApplications=yes

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"

[Files]
Source: "{#SourceDir}\GameOp.exe"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\README.md"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
Name: "{group}\GameOp"; Filename: "{app}\GameOp.exe"
Name: "{group}\Uninstall GameOp"; Filename: "{uninstallexe}"
Name: "{autodesktop}\GameOp"; Filename: "{app}\GameOp.exe"; Tasks: desktopicon

[Run]
Filename: "{app}\GameOp.exe"; Description: "{cm:LaunchProgram,GameOp}"; Flags: nowait postinstall skipifsilent shellexec
