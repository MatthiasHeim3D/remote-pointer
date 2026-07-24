#ifndef MyAppVersion
  #define MyAppVersion "1.0.0"
#endif
#ifndef PublishDir
  #error PublishDir must point to the published client.
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
SetupIconFile=..\icons\exe_icon.ico
UninstallDisplayIcon={app}\RemotePointer.Client.exe

#ifdef RelayRootCertificate
[Tasks]
Name: "trustrelay"; Description: "Trust the Remote Pointer relay certificate for this Windows account"; GroupDescription: "HTTPS certificate:"
#endif

[Files]
Source: "{#PublishDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs
#ifdef RelayRootCertificate
Source: "{#RelayRootCertificate}"; DestDir: "{app}"; DestName: "relay-root.crt"; Flags: ignoreversion
#endif

[Icons]
Name: "{userprograms}\Remote Pointer"; Filename: "{app}\RemotePointer.Client.exe"; WorkingDir: "{app}"

[Run]
#ifdef RelayRootCertificate
Filename: "{sys}\certutil.exe"; Parameters: "-user -f -addstore Root ""{app}\relay-root.crt"""; StatusMsg: "Trusting the relay HTTPS certificate..."; Flags: runhidden waituntilterminated; Tasks: trustrelay
#endif
Filename: "{app}\RemotePointer.Client.exe"; Description: "Launch Remote Pointer"; Flags: nowait postinstall skipifsilent

[Code]
procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  UserDataDir: String;
begin
  if CurUninstallStep = usPostUninstall then
  begin
    UserDataDir := ExpandConstant('{localappdata}\RemotePointer');
    if DirExists(UserDataDir) and not UninstallSilent() then
    begin
      if MsgBox('Also remove your saved Remote Pointer settings and profile data?' + #13#10 + UserDataDir,
           mbConfirmation, MB_YESNO) = IDYES then
      begin
        DelTree(UserDataDir, True, True, True);
      end;
    end;
  end;
end;
