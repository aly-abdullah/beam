; Beam installer - Inno Setup 6 (https://jrsoftware.org/isinfo.php)
; Built automatically by every build (Debug and Release) of Beam.csproj into
; bin\<Configuration>\Installer\Beam-Setup-<version>.exe. The build passes AppVersion
; (from <Version> in the csproj) and SourceDir (the published app).

#ifndef AppVersion
  #define AppVersion "1.0.0"
#endif
#ifndef SourceDir
  #define SourceDir "..\obj\Release\InstallerPayload"
#endif

[Setup]
AppId={{6E1F4B7A-3C52-4D8E-9A41-7B2D5C0E8F13}
AppName=Beam
AppVersion={#AppVersion}
AppVerName=Beam {#AppVersion}
AppPublisher=Aly Abdullah
AppPublisherURL=https://github.com/aly-abdullah/beam
AppSupportURL=https://github.com/aly-abdullah/beam/issues
DefaultDirName={autopf}\Beam
DisableProgramGroupPage=yes
DisableDirPage=yes
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
OutputDir=..\bin\Release\Installer
OutputBaseFilename=Beam-Setup-{#AppVersion}
SetupIconFile=..\Assets\Beam.ico
UninstallDisplayIcon={app}\Beam.exe
UninstallDisplayName=Beam
Compression=lzma2/ultra64
SolidCompression=yes
WizardStyle=modern dark
WizardSmallImageFile=logo-small.png
AppMutex=Beam.SingleInstance.7F3A
CloseApplications=yes

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"
Name: "arabic"; MessagesFile: "compiler:Languages\Arabic.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"

[Files]
; Release publishes one exe; a Debug build publishes the app with its files, so take the whole folder
Source: "{#SourceDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{autoprograms}\Beam"; Filename: "{app}\Beam.exe"
Name: "{autodesktop}\Beam"; Filename: "{app}\Beam.exe"; Tasks: desktopicon

[Run]
Filename: "{app}\Beam.exe"; Description: "{cm:LaunchProgram,Beam}"; Flags: nowait postinstall skipifsilent
