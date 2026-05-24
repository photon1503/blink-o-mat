#define MyAppName "Rejector"
#define MyAppExeName "Rejector.exe"
#ifndef MyAppVersion
  #define MyAppVersion "1.0.4"
#endif
#define MyAppPublisher "Gerald Hitz"
#define MyAppURL "https://github.com/photon1503/blink-o-mat"
#ifndef MyAppPublishDir
	#define MyAppPublishDir "src\\bin\\Release\\net10.0-windows\\publish"
#endif
#define MyAppIcon "src\\Icon\\6dd39f16-9f8b-454e-86c4-b44c409cb647.ico"

[Setup]
AppId={{C2C8C9B2-47C6-4B34-B37B-7F36B317A61A}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppVerName={#MyAppName} {#MyAppVersion}
AppPublisher={#MyAppPublisher}
AppPublisherURL={#MyAppURL}
AppSupportURL={#MyAppURL}
AppUpdatesURL={#MyAppURL}
DefaultDirName={autopf}\{#MyAppName}
DefaultGroupName={#MyAppName}
DisableProgramGroupPage=yes
LicenseFile=src\LICENSE.txt
OutputDir=.\installer
OutputBaseFilename=Rejector-Setup-{#MyAppVersion}
SetupIconFile={#MyAppIcon}
UninstallDisplayIcon={app}\{#MyAppExeName}
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
PrivilegesRequired=lowest

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "Create a desktop shortcut"; GroupDescription: "Additional shortcuts:"; Flags: unchecked

[Files]
Source: "{#MyAppPublishDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "{#MyAppIcon}"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
Name: "{autoprograms}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "Launch {#MyAppName}"; Flags: nowait postinstall skipifsilent
