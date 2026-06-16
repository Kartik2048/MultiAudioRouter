; Inno Setup script for MultiAudioRouter
; Packages the self-contained .NET 8.0 WPF build

[Setup]
AppName=MultiAudioRouter
AppVersion=1.0.0
AppPublisher=Kartik
DefaultDirName={localappdata}\Programs\MultiAudioRouter
DefaultGroupName=MultiAudioRouter
OutputDir=Output
OutputBaseFilename=MultiAudioRouterSetup
Compression=lzma2
SolidCompression=yes
PrivilegesRequired=lowest
DisableProgramGroupPage=yes
DisableDirPage=no

[Files]
Source: "MultiAudioRouter\bin\Release\net8.0-windows\win-x64\publish\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\MultiAudioRouter"; Filename: "{app}\MultiAudioRouter.exe"
Name: "{userdesktop}\MultiAudioRouter"; Filename: "{app}\MultiAudioRouter.exe"

[Run]
Filename: "{app}\MultiAudioRouter.exe"; Description: "Launch MultiAudioRouter"; Flags: nowait postinstall skipifsilent
