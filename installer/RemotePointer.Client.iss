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
AppMutex=RemotePointer.Client.Running
DefaultDirName={autopf}\Remote Pointer
DefaultGroupName=Remote Pointer
DisableProgramGroupPage=yes
; Per-user is the default so the common case needs no administrator. Choosing "all users" in the
; install-mode dialog (or passing /ALLUSERS) elevates and moves the whole install to machine scope;
; every {auto*} constant below follows that choice.
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=dialog commandline
ArchitecturesAllowed=x64compatible
; Without this an all-users install would land in "Program Files (x86)" despite being x64-only.
ArchitecturesInstallIn64BitMode=x64compatible
OutputDir={#InstallerOutputDir}
OutputBaseFilename=RemotePointer.Client-{#MyAppVersion}-x64-Setup
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
WizardSmallImageFile=..\icons\exe_icon_padded.png
SetupLogging=yes
CloseApplications=yes
RestartApplications=no
SetupIconFile=..\icons\exe_icon.ico
UninstallDisplayIcon={app}\RemotePointer.Client.exe

#ifdef RelayRootCertificate
[Tasks]
; One task for both modes so /MERGETASKS="!trustrelay" keeps working; the store it writes to
; follows the install mode, which is why the description does not name an account.
Name: "trustrelay"; Description: "Trust the Remote Pointer relay certificate"; GroupDescription: "HTTPS certificate:"
#endif

[Files]
Source: "{#PublishDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs
#ifdef RelayRootCertificate
Source: "{#RelayRootCertificate}"; DestDir: "{app}"; DestName: "relay-root.crt"; Flags: ignoreversion
#endif

[Icons]
Name: "{autoprograms}\Remote Pointer"; Filename: "{app}\RemotePointer.Client.exe"; WorkingDir: "{app}"

[Run]
#ifdef RelayRootCertificate
; An all-users install already holds the elevation needed for the machine root store, so every
; account on the PC trusts the relay. A per-user install can only reach its own store.
Filename: "{sys}\certutil.exe"; Parameters: "-f -addstore Root ""{app}\relay-root.crt"""; StatusMsg: "Trusting the relay HTTPS certificate..."; Flags: runhidden waituntilterminated; Tasks: trustrelay; Check: IsAdminInstallMode
Filename: "{sys}\certutil.exe"; Parameters: "-user -f -addstore Root ""{app}\relay-root.crt"""; StatusMsg: "Trusting the relay HTTPS certificate..."; Flags: runhidden waituntilterminated; Tasks: trustrelay; Check: not IsAdminInstallMode
#endif
Filename: "{app}\RemotePointer.Client.exe"; Description: "Launch Remote Pointer"; Flags: nowait postinstall skipifsilent

[Code]
procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  UserDataDir: String;
begin
  if CurUninstallStep = usPostUninstall then
  begin
    { The application owns this value: it writes it when "Launch at startup" is
      enabled and removes it when the setting is turned off. Setup must not create
      it, or an update would overwrite the user's choice with an empty command. }
    RegDeleteValue(HKEY_CURRENT_USER,
      'Software\Microsoft\Windows\CurrentVersion\Run', 'RemotePointer');

    { That value and the tree below are per-account, so an uninstall only ever reaches the account
      running it. After an all-users install, other accounts keep their own settings and their own
      Run value, which Windows ignores once the executable is gone. }
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
