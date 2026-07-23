#ifndef MyAppVersion
  #define MyAppVersion "1.0.0"
#endif
#ifndef PublishDir
  #error PublishDir must point to the published client.
#endif
#ifndef RelayRootCertificate
  #error RelayRootCertificate must point to Caddy's public root certificate.
#endif
#ifndef InstallerOutputDir
  #define InstallerOutputDir "."
#endif

[Setup]
AppId={{6E17309C-3CC5-4F34-BE90-387557FC7416}
AppName=Remote Pointer
AppVersion={#MyAppVersion}
AppPublisher=Remote Pointer
AppVerName=Remote Pointer {#MyAppVersion}
DefaultDirName={localappdata}\Programs\Remote Pointer
DefaultGroupName=Remote Pointer
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
OutputDir={#InstallerOutputDir}
OutputBaseFilename=RemotePointer.Client-{#MyAppVersion}-x64-Setup
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
SetupLogging=yes
CloseApplications=yes
RestartApplications=no
UninstallDisplayIcon={app}\RemotePointer.Client.exe

[Tasks]
Name: "trustrelay"; Description: "Trust the Remote Pointer relay certificate for this Windows account"; GroupDescription: "HTTPS certificate:"

[Files]
Source: "{#PublishDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "{#RelayRootCertificate}"; DestDir: "{app}"; DestName: "relay-root.crt"; Flags: ignoreversion

[Icons]
Name: "{userprograms}\Remote Pointer"; Filename: "{app}\RemotePointer.Client.exe"; WorkingDir: "{app}"

[Run]
Filename: "{sys}\certutil.exe"; Parameters: "-user -f -addstore Root ""{app}\relay-root.crt"""; StatusMsg: "Trusting the relay HTTPS certificate..."; Flags: runhidden waituntilterminated; Tasks: trustrelay
Filename: "{app}\RemotePointer.Client.exe"; Description: "Launch Remote Pointer"; Flags: nowait postinstall skipifsilent
