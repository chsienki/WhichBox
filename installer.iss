; WhichBox Inno Setup Installer Script
; Builds a per-user installer (no admin required)

#define MyAppName "WhichBox"
#define MyAppExeName "WhichBox.exe"
; Version and architecture are passed via /D on the ISCC command line:
;   /DMyAppVersion=0.1.0
;   /DMyAppArch=x64
; Publish output directory is also passed:
;   /DPublishDir=publish\win-x64

[Setup]
AppId={{7B2A4F8E-3C1D-4E5F-9A8B-6D0E2F1C3A4B}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppPublisher=chsienki
AppPublisherURL=https://github.com/chsienki/WhichBox
AppSupportURL=https://github.com/chsienki/WhichBox/issues
DefaultDirName={localappdata}\{#MyAppName}
DefaultGroupName={#MyAppName}
DisableProgramGroupPage=yes
OutputBaseFilename={#MyAppName}-{#MyAppArch}-Setup
Compression=lzma
SolidCompression=yes
PrivilegesRequired=lowest
OutputDir=installer-output
CloseApplications=force
RestartApplications=no
UninstallDisplayName={#MyAppName}
SetupIconFile=src\WhichBox\app.ico
UninstallDisplayIcon={app}\{#MyAppExeName}
#if MyAppArch == "arm64"
ArchitecturesAllowed=arm64
#else
ArchitecturesAllowed={#MyAppArch}compatible
#endif

[Tasks]
Name: "runatstartup"; Description: "Run at Windows startup"; GroupDescription: "Additional options:"; Flags: checkedonce
Name: "launchapp"; Description: "Launch {#MyAppName} after install"; GroupDescription: "Additional options:"; Flags: checkedonce

[Files]
Source: "{#PublishDir}\*"; Excludes: "*.pdb,Microsoft.Web.WebView2.Core.dll,WebView2Loader.dll,Microsoft.Windows.ApplicationModel.Background.UniversalBGTask.dll"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"
Name: "{group}\Uninstall {#MyAppName}"; Filename: "{uninstallexe}"

[Registry]
; Run at startup (only if task selected; auto-removed on uninstall)
Root: HKCU; Subkey: "SOFTWARE\Microsoft\Windows\CurrentVersion\Run"; ValueType: string; ValueName: "{#MyAppName}"; ValueData: """{app}\{#MyAppExeName}"""; Flags: uninsdeletevalue; Tasks: runatstartup

[Run]
; Launch after interactive install (only if task selected)
Filename: "{app}\{#MyAppExeName}"; Description: "Launch {#MyAppName}"; Flags: nowait postinstall skipifsilent; Tasks: launchapp
; Always relaunch after silent install (auto-update)
Filename: "{app}\{#MyAppExeName}"; Flags: nowait skipifdoesntexist; Check: WizardSilent

[UninstallRun]
; Kill WhichBox before uninstalling files
Filename: "taskkill"; Parameters: "/F /IM {#MyAppExeName}"; Flags: runhidden; RunOnceId: "KillWhichBox"

[Code]
// Kill any running instance before install
procedure CurStepChanged(CurStep: TSetupStep);
var
  ResultCode: Integer;
begin
  if CurStep = ssInstall then
  begin
    Exec('taskkill', '/F /IM {#MyAppExeName}', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
    // Brief pause to let the process fully exit
    Sleep(500);
  end;
end;
