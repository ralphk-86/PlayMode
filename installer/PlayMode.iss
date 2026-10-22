; PlayMode installer (Inno Setup 6). Per-user: no admin prompt, installs to %LOCALAPPDATA%\Programs\PlayMode.
; Service control is set up separately from the tray menu, with its own one-time UAC prompt.
;
; Build: ./build/publish.ps1 -Version 1.2.3, then
;        ISCC.exe /DAppVersion=1.2.3 /DSourceDir=..\artifacts\publish installer\PlayMode.iss

#ifndef AppVersion
  #define AppVersion "0.0.0-dev"
#endif
#ifndef SourceDir
  #define SourceDir "..\artifacts\publish"
#endif

[Setup]
AppId={{820028D1-2569-4D53-B3A5-C3EEAA316D96}
AppName=PlayMode
AppVersion={#AppVersion}
AppVerName=PlayMode {#AppVersion}
AppPublisher=ralphk-86
AppPublisherURL=https://github.com/ralphk-86/PlayMode
AppSupportURL=https://github.com/ralphk-86/PlayMode/issues
AppUpdatesURL=https://github.com/ralphk-86/PlayMode/releases
AppCopyright=Copyright (c) 2026 ralphk-86
DefaultDirName={autopf}\PlayMode
DisableProgramGroupPage=yes
DisableDirPage=auto
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.19041
LicenseFile=..\LICENSE
SetupIconFile=..\src\PlayMode.App\PlayMode.ico
UninstallDisplayIcon={app}\PlayMode.exe
UninstallDisplayName=PlayMode
OutputDir=..\artifacts
OutputBaseFilename=PlayMode-{#AppVersion}-setup-x64
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
; The tray app is asked to exit politely first (see PrepareToInstall); Restart Manager is the fallback.
CloseApplications=yes
ChangesEnvironment=yes
RestartApplications=no

[Tasks]
Name: "autostart"; Description: "Start PlayMode when I sign in to Windows"
Name: "path"; Description: "Add playmodectl (the command-line tool) to PATH"

[Files]
Source: "{#SourceDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs; Excludes: "*.pdb"

[Icons]
Name: "{autoprograms}\PlayMode"; Filename: "{app}\PlayMode.exe"; Comment: "Switch between your dev setup and your gaming setup"

[Registry]
; Same value the tray's "Start with Windows" menu item writes, so the two stay in step.
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: string; ValueName: "PlayMode"; ValueData: """{app}\PlayMode.exe"""; Tasks: autostart; Flags: uninsdeletevalue
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueName: "PlayMode"; Flags: dontcreatekey uninsdeletevalue
; playmodectl on PATH; the uninstaller takes it off again (RemoveFromPath).
Root: HKCU; Subkey: "Environment"; ValueType: expandsz; ValueName: "Path"; ValueData: "{olddata};{app}"; Tasks: path; Check: NotOnPath

[Run]
Filename: "{app}\PlayMode.exe"; Description: "Start PlayMode now"; Flags: nowait postinstall skipifsilent

[Code]
const
  DotNetUrl = 'https://dotnet.microsoft.com/download/dotnet/10.0';

function DesktopRuntimeInstalled: Boolean;
var
  Found: TFindRec;
begin
  Result := FindFirst(ExpandConstant('{commonpf64}\dotnet\shared\Microsoft.WindowsDesktop.App\10.*'), Found);
  if Result then
    FindClose(Found);
end;

function InitializeSetup: Boolean;
var
  ErrorCode: Integer;
begin
  Result := True;
  // winget installs the runtime as a dependency; only interactive installs need telling.
  if not DesktopRuntimeInstalled and not WizardSilent then
    if MsgBox('PlayMode needs the .NET 10 Desktop Runtime (x64), which isn''t installed yet.' + #13#10#13#10 +
              'Open the download page now? Setup will carry on either way; PlayMode starts once the runtime is installed.' + #13#10#13#10 +
              'Or from a terminal: winget install Microsoft.DotNet.DesktopRuntime.10',
              mbConfirmation, MB_YESNO) = IDYES then
      ShellExec('open', DotNetUrl, '', '', SW_SHOWNORMAL, ewNoWait, ErrorCode);
end;

function Ctl(const Params: String): Boolean;
var
  ResultCode: Integer;
  Exe: String;
begin
  Exe := ExpandConstant('{app}\playmodectl.exe');
  Result := FileExists(Exe) and Exec(Exe, Params, '', SW_HIDE, ewWaitUntilTerminated, ResultCode) and (ResultCode = 0);
end;

function NotOnPath: Boolean;
var
  Path: String;
begin
  if not RegQueryStringValue(HKCU, 'Environment', 'Path', Path) then
    Path := '';
  Result := Pos(';' + Uppercase(ExpandConstant('{app}')) + ';', ';' + Uppercase(Path) + ';') = 0;
end;

procedure RemoveFromPath;
var
  Path, App: String;
  At: Integer;
begin
  if not RegQueryStringValue(HKCU, 'Environment', 'Path', Path) then
    exit;
  App := ExpandConstant('{app}');
  Path := ';' + Path + ';';
  At := Pos(';' + Uppercase(App) + ';', Uppercase(Path));
  if At = 0 then
    exit;
  Delete(Path, At, Length(App) + 1);
  Path := Copy(Path, 2, Length(Path) - 2);
  RegWriteExpandStringValue(HKCU, 'Environment', 'Path', Path);
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
begin
  // Upgrading: let the running tray save its state and exit rather than be closed from outside.
  if Ctl('tray exit') then
    Sleep(1500);
  Result := '';
end;

function InPlayMode: Boolean;
var
  State: AnsiString;
begin
  Result := LoadStringFromFile(ExpandConstant('{localappdata}\PlayMode\state.json'), State) and (Pos('"mode": "Play"', State) > 0);
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
begin
  if CurUninstallStep = usPostUninstall then
    RemoveFromPath;
  if CurUninstallStep <> usUninstall then
    exit;
  if Ctl('tray exit') then
    Sleep(1500);
  // Don't leave someone's apps closed: switch back to dev mode first.
  if InPlayMode then
    Ctl('off');
  // Service control lives in Program Files and scheduled tasks; removing it asks for admin once.
  if DirExists(ExpandConstant('{commonpf64}\PlayMode\Helper')) then
    Ctl('remove-helper');
  if not UninstallSilent and DirExists(ExpandConstant('{userappdata}\PlayMode')) then
    if MsgBox('Also delete your PlayMode settings and profiles?', mbConfirmation, MB_YESNO or MB_DEFBUTTON2) = IDYES then
    begin
      DelTree(ExpandConstant('{userappdata}\PlayMode'), True, True, True);
      DelTree(ExpandConstant('{localappdata}\PlayMode'), True, True, True);
    end;
end;
