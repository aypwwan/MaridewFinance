; ============================================================================
;  Maridew Finance - Inno Setup 6 installer script
; ----------------------------------------------------------------------------
;  Produces a classic Windows setup.exe that:
;    * installs the app into Program Files (x64)
;    * creates a Start-menu shortcut (plus optional desktop shortcut)
;    * installs the .NET 8 Desktop Runtime and WebView2 runtime if missing
;    * offers to launch the app when setup finishes
;    * uninstalls cleanly (files, shortcuts, uninstall entry)
;
;  Compile from the command line (or just run installer\build.ps1):
;    ISCC.exe /DAppVersion=1.0.0 /DPublishDir=..\publish-installer setup.iss
; ============================================================================

#ifndef AppVersion
  #define AppVersion "1.0.0"
#endif

#ifndef PublishDir
  #define PublishDir "..\publish-installer"
#endif

#define MyAppName "Maridew Finance"
#define MyAppExe "MaridewFinance.exe"
#define MyAppGroup "Maridew Finance"
#define DotNetRuntimeUrl "https://aka.ms/dotnet/8.0/windowsdesktop-runtime-win-x64.exe"

[Setup]
AppId={{7E1A2C64-9B5D-4F0E-8A31-52C6B7D9E4F8}
AppName={#MyAppName}
AppVersion={#AppVersion}
AppVerName={#MyAppName} {#AppVersion}
AppPublisher=Maridew
DefaultDirName={autopf}\{#MyAppName}
DefaultGroupName={#MyAppGroup}
DisableProgramGroupPage=yes
UninstallDisplayName={#MyAppName}
UninstallDisplayIcon={app}\{#MyAppExe}
OutputDir=dist
OutputBaseFilename=MaridewFinanceSetup-{#AppVersion}
SetupIconFile=..\MaridewFinance.App\app.ico
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0
PrivilegesRequired=admin
CloseApplications=yes
RestartApplications=no
SetupLogging=yes
VersionInfoVersion={#AppVersion}.0
VersionInfoProductName={#MyAppName}
VersionInfoProductVersion={#AppVersion}
VersionInfoCopyright=Copyright (C) 2026 Maridew
VersionInfoDescription={#MyAppName} installer

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"

[Files]
Source: "{#PublishDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "MicrosoftEdgeWebview2Setup.exe"; DestDir: "{tmp}"; Flags: deleteafterinstall

[Icons]
Name: "{group}\{#MyAppName}"; Filename: "{app}\{#MyAppExe}"
Name: "{group}\Uninstall {#MyAppName}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExe}"; Tasks: desktopicon

[Run]
Filename: "{tmp}\MicrosoftEdgeWebview2Setup.exe"; Parameters: "/install /silent /norestart"; StatusMsg: "Installing the Microsoft Edge WebView2 runtime (needed to display the dashboard)"; Flags: waituntilterminated
Filename: "{app}\{#MyAppExe}"; Description: "{cm:LaunchProgram,{#MyAppName}}"; Flags: nowait postinstall skipifsilent

; Wipe the previous install before writing the new one (keeps upgrades clean)
[InstallDelete]
Type: filesandordirs; Name: "{app}"

; Remove every installed file on uninstall (user data in %LOCALAPPDATA% is kept)
[UninstallDelete]
Type: filesandordirs; Name: "{app}"

[Code]
var
  RuntimePage: TInputOptionWizardPage;
  SkipDotNetDownload: Boolean;

const
  WebView2Key64 = 'SOFTWARE\Microsoft\EdgeUpdate\Clients\{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}';
  WebView2Key32 = 'SOFTWARE\WOW6432Node\Microsoft\EdgeUpdate\Clients\{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}';

// True when a .NET 8 Desktop Runtime (8.x) is already on the machine.
function IsDotNet8DesktopInstalled: Boolean;
var
  FindRec: TFindRec;
  Dir: string;
begin
  Result := False;
  Dir := AddBackslash(ExpandConstant('{commonpf}\dotnet\shared\Microsoft.WindowsDesktop.App'));
  if FindFirst(Dir + '*', FindRec) then
  begin
    try
      repeat
        if (FindRec.Attributes and FILE_ATTRIBUTE_DIRECTORY) <> 0 then
          if (FindRec.Name <> '.') and (FindRec.Name <> '..') and
             (Copy(FindRec.Name, 1, 2) = '8.') then
          begin
            Result := True;
            Exit;
          end;
      until not FindNext(FindRec);
    finally
      FindClose(FindRec);
    end;
  end;
end;

// True when the Microsoft Edge WebView2 runtime is already installed.
function IsWebView2Installed: Boolean;
begin
  Result :=
    RegKeyExists(HKLM, WebView2Key32) or
    RegKeyExists(HKLM, WebView2Key64) or
    RegKeyExists(HKCU, WebView2Key64);
end;

procedure InitializeWizard;
begin
  RuntimePage := CreateInputOptionPage(wpSelectDir,
    'Runtime Components',
    'Choose what setup should install',
    'Maridew Finance runs on the .NET 8 Desktop Runtime and draws its dashboard with the Microsoft Edge WebView2 runtime. Setup can install either component for you if it is missing.' + #13#10 + #13#10 +
    'Note: the .NET runtime installer opens in your browser - after it finishes, come back to this setup window and click Next.',
    True, False);
  RuntimePage.Add('Download & install the .NET 8 Desktop Runtime (opens in your browser)');
  RuntimePage.Add('Skip - the .NET 8 Desktop Runtime is already installed');
  RuntimePage.Values[0] := not IsDotNet8DesktopInstalled;
  RuntimePage.Values[1] := IsDotNet8DesktopInstalled;
end;

function NextButtonClick(CurPageID: Integer): Boolean;
begin
  Result := True;
  if CurPageID = RuntimePage.ID then
    SkipDotNetDownload := RuntimePage.Values[1];
end;

procedure CurStepChanged(CurStep: TSetupStep);
var
  Res: Integer;
begin
  if (CurStep = ssInstall) and (not SkipDotNetDownload) then
  begin
    WizardForm.StatusLabel.Caption := 'After the .NET runtime installer finishes, return to this window and click Next to continue.';
    if not ShellExec('open', '{#DotNetRuntimeUrl}', '', '', SW_SHOW, ewWaitUntilTerminated, Res) then
      MsgBox('Setup could not open the .NET 8 Desktop Runtime download page.' + #13#10 +
             'Please download and install it manually from https://dotnet.microsoft.com/download/dotnet/8.0, then continue.', mbError, MB_OK);
  end;
end;

// Make sure the app is not running when the uninstaller runs, so removal is clean.
procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  Res: Integer;
begin
  if CurUninstallStep = usUninstall then
    ExecAsOriginalUser(ExpandConstant('{sys}\taskkill.exe'), '/F /IM MaridewFinance.exe /T', '', SW_HIDE, ewWaitUntilTerminated, Res);
end;