; ---------------------------------------------------------------------------------------------------------------------------------------------------------
; Sportify.iss - the installer of Sportify (Revit 2025 add-in, web app, local API, library).
;
; Built by Build-Installer.ps1 (which stages the payload and the library and passes the version), never by hand:
;   ISCC.exe /DAppVersion=0.1.0-beta.1 /DAppVersionNumeric=0.1.0.0 Sportify.iss
;
; What the person sees: welcome, the license agreement (accept + Next), the install folder, the folder for their deliverables, Install (a progress bar while everything is
; copied, the add-in is registered with Revit, the Microsoft components the web app needs are installed when missing, and the local API is started once so that its
; database is ready), and a last page with "Open the Sportify web app" (Chrome) or "Open Revit with the web app docked inside it". No DLL is ever copied by hand.
;
; Per-user (no administrator rights): the files go to a folder of the person's choice, the Revit manifest to %APPDATA%\Autodesk\Revit\Addins\2025.
;
; Test switches (used by Tools/test-installer.ps1, harmless otherwise): /ADDINSDIR=<folder for the .addin>  /SETTINGSDIR=<folder for settings.json>  /DELIVERABLES=<folder>
; /NOSTARTAPI=1 (do not start the API)  /NOPREREQS=1 (do not look for or install Microsoft components)  /DISABLELEGACY=1 (switch off an old Sportify.addin without asking)  /NOSHORTCUTS=1 (no Start menu entries)  /ALLUSERSADDINSDIR=<folder> (where an all-users Sportify.addin is looked for).
; ---------------------------------------------------------------------------------------------------------------------------------------------------------

#ifndef AppVersion
  #define AppVersion "0.1.0-beta.1"
#endif
#ifndef AppVersionNumeric
  #define AppVersionNumeric "0.1.0.0"
#endif
#ifndef PayloadDir
  #define PayloadDir "..\dist\payload"
#endif
#ifndef LibraryDir
  #define LibraryDir "..\dist\library"
#endif
#ifndef PrereqDir
  #define PrereqDir "..\dist\prereqs"
#endif
#ifndef OutputDir
  #define OutputDir "..\dist"
#endif
#define AppName "Sportify"
#define Publisher "Digital Tools and Methods - Group of Sports and Gardens"
#define RevitYear "2025"
#define RepoUrl "https://github.com/nfe15rajab-bot/Sportify_Revit_and_API"
#define ApiPort "5107"

[Setup]
AppId={{6D1C5A3E-4B0F-4C57-9E5B-51F7A0C0D1A7}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}
AppPublisher={#Publisher}
AppPublisherURL={#RepoUrl}
AppSupportURL={#RepoUrl}/issues
AppUpdatesURL={#RepoUrl}/releases
AppCopyright=Open source, for educational purposes
VersionInfoVersion={#AppVersionNumeric}
VersionInfoProductVersion={#AppVersionNumeric}
VersionInfoCompany={#Publisher}
VersionInfoProductName={#AppName}
VersionInfoDescription={#AppName} setup: Revit {#RevitYear} add-in, web app and local API
DefaultDirName={localappdata}\Programs\Sportify
UsePreviousAppDir=yes
DisableProgramGroupPage=yes
DisableWelcomePage=no
DefaultGroupName=Sportify
PrivilegesRequired=lowest
LicenseFile=LICENSE_AGREEMENT.txt
OutputDir={#OutputDir}
OutputBaseFilename=Sportify-Setup-{#AppVersion}-Revit{#RevitYear}
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0
UninstallDisplayName={#AppName} {#AppVersion}
UninstallFilesDir={app}\uninstall
ShowLanguageDialog=no
CloseApplications=no
RestartApplications=no
SetupLogging=yes

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Messages]
WelcomeLabel1=Welcome to Sportify
WelcomeLabel2=This installs Sportify {#AppVersion}, the Revit {#RevitYear} add-in with its web app, its local API and database, and a library of templates, families and documentation.%n%nSportify is an open source project of the {#Publisher}, TH OWL, and is still under testing.%n%nClose Revit before you continue.
FinishedHeadingLabel=Sportify is installed
FinishedLabelNoIcons=Sportify was installed and registered with Revit {#RevitYear}. It loads the next time you start Revit; you do not have to copy any files.

[Files]
; the add-in, the bundled web app (web\), the self-contained local API (api\), the SOLIDWORKS tool when it was built (mechanical\): what BuildDistribution.ps1 stages as the payload
Source: "{#PayloadDir}\*"; DestDir: "{app}"; Excludes: "reference.db*"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "tools\*.ps1"; DestDir: "{app}\tools"; Flags: ignoreversion
Source: "LICENSE_AGREEMENT.txt"; DestDir: "{app}"; Flags: ignoreversion
; the library: Templates, Families, Worksets, Documentation, Database (staged by Build-Installer.ps1; a folder that was not staged is simply not installed)
Source: "{#LibraryDir}\*"; DestDir: "{app}\Library"; Flags: ignoreversion recursesubdirs createallsubdirs skipifsourcedoesntexist
; Microsoft components the web app needs, when the build could fetch them: setup uses them only if the computer lacks them
#if FileExists(PrereqDir + "\MicrosoftEdgeWebview2Setup.exe")
Source: "{#PrereqDir}\MicrosoftEdgeWebview2Setup.exe"; Flags: dontcopy
#define HaveWebView2Bootstrapper
#endif
#if FileExists(PrereqDir + "\vc_redist.x64.exe")
Source: "{#PrereqDir}\vc_redist.x64.exe"; Flags: dontcopy
#define HaveVcRedist
#endif

[Icons]
Name: "{autoprograms}\Sportify\Sportify web app"; Filename: "{sys}\WindowsPowerShell\v1.0\powershell.exe"; Parameters: "-NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File ""{app}\tools\Open-Sportify-WebApp.ps1"""; Comment: "Starts the local API if needed and opens the Sportify web app in Chrome"; Check: Shortcuts
Name: "{autoprograms}\Sportify\Sportify web app inside Revit"; Filename: "{sys}\WindowsPowerShell\v1.0\powershell.exe"; Parameters: "-NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File ""{app}\tools\Open-Sportify-InRevit.ps1"""; Comment: "Starts Revit {#RevitYear} with the web app docked inside it"; Check: Shortcuts and RevitInstalled
Name: "{autoprograms}\Sportify\Sportify folder (my files)"; Filename: "{code:DeliverablesDir}"; Check: Shortcuts
Name: "{autoprograms}\Sportify\Sportify library"; Filename: "{app}\Library"; Check: Shortcuts
Name: "{autoprograms}\Sportify\User guide"; Filename: "{app}\Library\Documentation\Sportify-User-Guide.pdf"; Check: Shortcuts and FileExists(ExpandConstant('{app}\Library\Documentation\Sportify-User-Guide.pdf'))
Name: "{autoprograms}\Sportify\Stop Sportify API"; Filename: "{sys}\WindowsPowerShell\v1.0\powershell.exe"; Parameters: "-NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File ""{app}\tools\Stop-Sportify.ps1"""; Check: Shortcuts
Name: "{autoprograms}\Sportify\Uninstall Sportify"; Filename: "{uninstallexe}"; Check: Shortcuts

[Run]
Filename: "{sys}\WindowsPowerShell\v1.0\powershell.exe"; Parameters: "-NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File ""{app}\tools\Open-Sportify-WebApp.ps1"""; Description: "Open the Sportify web app"; Flags: postinstall nowait skipifsilent runhidden
Filename: "{sys}\WindowsPowerShell\v1.0\powershell.exe"; Parameters: "-NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File ""{app}\tools\Open-Sportify-InRevit.ps1"""; Description: "Open Revit {#RevitYear} with the web app docked inside it instead"; Flags: postinstall nowait skipifsilent runhidden unchecked; Check: RevitInstalled

[UninstallRun]
Filename: "{sys}\WindowsPowerShell\v1.0\powershell.exe"; Parameters: "-NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File ""{app}\tools\Stop-Sportify.ps1"""; RunOnceId: "StopSportifyApi"; Flags: runhidden waituntilterminated

[UninstallDelete]
; what the program made after setup: the database, the generated-family cache, logs
Type: filesandordirs; Name: "{app}\api"
Type: filesandordirs; Name: "{app}\SportifyGeneratedFamilies"
Type: filesandordirs; Name: "{app}\logs"
Type: files; Name: "{code:AddinsDir}\SportfyRevit.addin"

[Code]
const
  WorkspaceFolders = 'Layouts,Sport fields,Garden,Physical analysis,Videos,Analysis reports,Schedules,Diagrams,Mechanical,Profile';
  WebView2Client = 'SOFTWARE\Microsoft\EdgeUpdate\Clients\{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}';
  LegacyDisabledSuffix = '.disabled';

var
  DeliverablesPage: TInputDirWizardPage;
  LegacyDisabledByUs: Boolean;

// ------------------------------------------------------------------------------------------------------------------------------ small helpers

function AddinsDir(Param: String): String;
begin
  Result := ExpandConstant('{param:ADDINSDIR|{userappdata}\Autodesk\Revit\Addins\{#RevitYear}}');
end;

function SettingsDir: String;
begin
  Result := ExpandConstant('{param:SETTINGSDIR|{userappdata}\Sportify}');
end;

function DeliverablesDir(Param: String): String;
begin
  if Assigned(DeliverablesPage) then Result := DeliverablesPage.Values[0] else Result := ExpandConstant('{userdocs}\Sportify Workspace');
end;

function Flag(const Name: String): Boolean;
begin
  Result := ExpandConstant('{param:' + Name + '|0}') <> '0';
end;

function Shortcuts: Boolean;
begin
  Result := not Flag('NOSHORTCUTS');
end;

function JsonEscape(const S: String): String;
begin
  Result := S;
  StringChangeEx(Result, '\', '\\', True);
  StringChangeEx(Result, '"', '\"', True);
end;

function XmlEscape(const S: String): String;
begin
  Result := S;
  StringChangeEx(Result, '&', '&amp;', True);
  StringChangeEx(Result, '<', '&lt;', True);
  StringChangeEx(Result, '>', '&gt;', True);
end;

function RevitExe: String;
begin
  Result := ExpandConstant('{commonpf64}\Autodesk\Revit {#RevitYear}\Revit.exe');
end;

function RevitInstalled: Boolean;
begin
  Result := FileExists(RevitExe);
end;

function RevitRunning: Boolean;
var
  ResultCode: Integer;
begin
  Result := Exec(ExpandConstant('{sys}\cmd.exe'), '/C tasklist /FI "IMAGENAME eq Revit.exe" | find /I "Revit.exe" >nul', '', SW_HIDE, ewWaitUntilTerminated, ResultCode) and (ResultCode = 0);
end;

function PowerShellExe: String;
begin
  Result := ExpandConstant('{sys}\WindowsPowerShell\v1.0\powershell.exe');
end;

// what an earlier setup remembered in settings.json: the deliverables folder ("workspace_folder"), so an update proposes the same one
function PreviousWorkspace: String;
var
  Text: AnsiString;
  P, Q: Integer;
  Value: String;
begin
  Result := '';
  if not LoadStringFromFile(SettingsDir + '\settings.json', Text) then Exit;
  P := Pos('"workspace_folder"', Text);
  if P = 0 then Exit;
  Value := Copy(Text, P + Length('"workspace_folder"'), Length(Text));
  P := Pos('"', Value);
  if P = 0 then Exit;
  Value := Copy(Value, P + 1, Length(Value));
  Q := Pos('"', Value);
  if Q = 0 then Exit;
  Value := Copy(Value, 1, Q - 1);
  StringChangeEx(Value, '\\', '\', True);
  Result := Value;
end;

procedure Status(const Text: String);
begin
  WizardForm.StatusLabel.Caption := Text;
  WizardForm.Refresh;
end;

// ------------------------------------------------------------------------------------------------------------------------------ the wizard

procedure InitializeWizard;
var
  Previous: String;
begin
  DeliverablesPage := CreateInputDirPage(wpSelectDir, 'Your Sportify folder', 'Where should Sportify keep what you produce?',
    'Your layouts, charts, videos, reports and schedules go into subfolders of this folder, and the web app finds them there. Choose the folder, then click Next.', False, '');
  DeliverablesPage.Add('');
  Previous := PreviousWorkspace;
  if Previous <> '' then
    DeliverablesPage.Values[0] := Previous
  else
    DeliverablesPage.Values[0] := ExpandConstant('{param:DELIVERABLES|{userdocs}\Sportify Workspace}');
end;

function InitializeSetup: Boolean;
begin
  Result := True;
  if WizardSilent then Exit;
  if RevitRunning then
    if MsgBox('Revit is running. Sportify can only be installed cleanly while Revit is closed (Revit holds the add-in''s files).' + #13#10#13#10 + 'Close Revit first, then click Yes to continue. Click No to stop setup.', mbConfirmation, MB_YESNO) = IDNO then
      Result := False;
  if Result and (not RevitInstalled) then
    MsgBox('Revit {#RevitYear} was not found on this computer.' + #13#10#13#10 + 'Setup continues: the web app and its local API work without Revit, and the add-in loads as soon as Revit {#RevitYear} is installed.', mbInformation, MB_OK);
end;

function NextButtonClick(CurPageID: Integer): Boolean;
var
  Folder: String;
begin
  Result := True;
  if CurPageID = DeliverablesPage.ID then
  begin
    Folder := Trim(DeliverablesPage.Values[0]);
    if Folder = '' then
    begin
      MsgBox('Choose a folder for your Sportify files.', mbError, MB_OK);
      Result := False;
    end
    else if not ForceDirectories(Folder) then
    begin
      MsgBox('That folder cannot be created or written: ' + Folder + #13#10#13#10 + 'Choose another one.', mbError, MB_OK);
      Result := False;
    end;
  end;
end;

// ------------------------------------------------------------------------------------------------------------------------------ what setup does besides copying files

procedure StopPreviousApi;
var
  ResultCode: Integer;
  Script: String;
begin
  Script := ExpandConstant('{app}\tools\Stop-Sportify.ps1');
  if FileExists(Script) then
    Exec(PowerShellExe, '-NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File "' + Script + '"', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
end;

// an older Sportify add-in (Sportify.addin, loading Sportify\Sportify.dll) builds the same ribbon tab: Revit then reports "The name already exists" at every start
function AllUsersAddinsDir: String;
begin
  Result := ExpandConstant('{param:ALLUSERSADDINSDIR|{commonappdata}\Autodesk\Revit\Addins\{#RevitYear}}');
end;

procedure DisableLegacyManifest(const Path: String; const ForAllUsers: Boolean);
var
  Text: AnsiString;
  Disable: Boolean;
begin
  if not FileExists(Path) then Exit;
  if not LoadStringFromFile(Path, Text) then Exit;
  if Pos('Sportify.dll', Text) = 0 then Exit;
  if WizardSilent then
    Disable := Flag('DISABLELEGACY')
  else
    Disable := MsgBox('An older Sportify add-in was found (' + Path + ').' + #13#10#13#10 + 'It builds the same ribbon tab as this version, so Revit would report "The name already exists" at every start.' + #13#10#13#10 + 'Switch it off? (It is only renamed to Sportify.addin.disabled.)', mbConfirmation, MB_YESNO) = IDYES;
  if not Disable then Exit;
  if RenameFile(Path, Path + LegacyDisabledSuffix) then
  begin
    if not ForAllUsers then LegacyDisabledByUs := True;
    Log('The older add-in manifest was renamed to ' + Path + LegacyDisabledSuffix);
  end
  else
  begin
    Log('The older add-in manifest could not be renamed: ' + Path);
    if not WizardSilent then
      MsgBox('The older Sportify add-in could not be switched off: ' + Path + #13#10#13#10 + 'It belongs to every user of this computer and needs administrator rights. Rename that file to Sportify.addin.disabled yourself (right-click, Rename, as administrator), or Revit will keep reporting "The name already exists".', mbInformation, MB_OK);
  end;
end;

procedure OfferToDisableLegacyAddin;
begin
  DisableLegacyManifest(AddinsDir('') + '\Sportify.addin', False);
  DisableLegacyManifest(AllUsersAddinsDir + '\Sportify.addin', True);
end;

procedure CreateWorkspace;
var
  Root, Names, Name: String;
  P: Integer;
begin
  Root := DeliverablesDir('');
  ForceDirectories(Root);
  Names := WorkspaceFolders + ',';
  while Names <> '' do
  begin
    P := Pos(',', Names);
    Name := Copy(Names, 1, P - 1);
    Names := Copy(Names, P + 1, Length(Names));
    if Name <> '' then ForceDirectories(Root + '\' + Name);
  end;
end;

procedure WriteSettings;
var
  Lines: TArrayOfString;
begin
  ForceDirectories(SettingsDir);
  SetArrayLength(Lines, 5);
  Lines[0] := '{';
  Lines[1] := '  "workspace_folder": "' + JsonEscape(DeliverablesDir('')) + '",';
  Lines[2] := '  "install_folder": "' + JsonEscape(ExpandConstant('{app}')) + '",';
  Lines[3] := '  "version": "{#AppVersion}"';
  Lines[4] := '}';
  SaveStringsToUTF8File(SettingsDir + '\settings.json', Lines, False);
end;

// Revit reads every .addin in its Addins\2025 folder at start: this one points at the install folder, so nothing is ever pasted next to Revit
procedure WriteAddinManifest;
var
  Lines: TArrayOfString;
begin
  ForceDirectories(AddinsDir(''));
  SetArrayLength(Lines, 12);
  Lines[0] := '<?xml version="1.0" encoding="utf-8"?>';
  Lines[1] := '<!-- Written by the Sportify setup ({#AppVersion}). Uninstalling Sportify removes it. -->';
  Lines[2] := '<RevitAddIns>';
  Lines[3] := '  <AddIn Type="Application">';
  Lines[4] := '    <Name>Sportify</Name>';
  Lines[5] := '    <Assembly>' + XmlEscape(ExpandConstant('{app}\SportfyRevit.dll')) + '</Assembly>';
  Lines[6] := '    <FullClassName>SportfyRevit.SportfyRevitApp</FullClassName>';
  Lines[7] := '    <AddInId>b7c46fc3-f9d9-4b51-8323-24632d2b42db</AddInId>';
  Lines[8] := '    <VendorId>DTMSG</VendorId>';
  Lines[9] := '    <VendorDescription>{#Publisher}</VendorDescription>';
  Lines[10] := '  </AddIn>';
  Lines[11] := '</RevitAddIns>';
  SaveStringsToUTF8File(AddinsDir('') + '\SportfyRevit.addin', Lines, False);
end;

function WebView2Installed: Boolean;
var
  Version: String;
begin
  Result := (RegQueryStringValue(HKLM32, WebView2Client, 'pv', Version) and (Version <> '') and (Version <> '0.0.0.0'))
         or (RegQueryStringValue(HKCU, WebView2Client, 'pv', Version) and (Version <> '') and (Version <> '0.0.0.0'));
end;

function VcRuntimeInstalled: Boolean;
var
  Installed: Cardinal;
begin
  Result := RegQueryDWordValue(HKLM64, 'SOFTWARE\Microsoft\VisualStudio\14.0\VC\Runtimes\x64', 'Installed', Installed) and (Installed = 1);
end;

// runs a prerequisite from what the installer carries, or downloads it when the build could not include it
function RunPrerequisite(const FileName, Url, Arguments, Caption: String): Boolean;
var
  Path: String;
  ResultCode: Integer;
begin
  Result := False;
  Status('Installing ' + Caption + ' ...');
  try
    Path := ExpandConstant('{tmp}\' + FileName);
    if not FileExists(Path) then
    begin
      try
        ExtractTemporaryFile(FileName);
      except
        Log('Not carried in setup: ' + FileName + ', downloading it.');
      end;
    end;
    if not FileExists(Path) then DownloadTemporaryFile(Url, FileName, '', nil);
    Result := ShellExec('open', Path, Arguments, '', SW_HIDE, ewWaitUntilTerminated, ResultCode) and ((ResultCode = 0) or (ResultCode = 3010) or (ResultCode = 1638));
    Log(Caption + ': exit code ' + IntToStr(ResultCode));
  except
    Log(Caption + ' could not be installed: ' + GetExceptionMessage);
  end;
end;

procedure InstallPrerequisites;
begin
  if Flag('NOPREREQS') then Exit;
  if not VcRuntimeInstalled then
  begin
    if not RunPrerequisite('vc_redist.x64.exe', 'https://aka.ms/vs/17/release/vc_redist.x64.exe', '/install /quiet /norestart', 'the Microsoft Visual C++ runtime') then
      Log('The Visual C++ runtime is missing and could not be installed: the local API may not start.');
  end;
  if not WebView2Installed then
  begin
    if not RunPrerequisite('MicrosoftEdgeWebview2Setup.exe', 'https://go.microsoft.com/fwlink/p/?LinkId=2124703', '/silent /install', 'the Microsoft WebView2 runtime') then
      MsgBox('The Microsoft WebView2 runtime could not be installed (no internet?).' + #13#10#13#10 + 'The web app still works in Chrome; the docked web app pane inside Revit needs WebView2 (run setup again later with internet access).', mbInformation, MB_OK);
  end;
end;

function ApiAnswers: Boolean;
var
  Request: Variant;
begin
  Result := False;
  try
    Request := CreateOleObject('WinHttp.WinHttpRequest.5.1');
    Request.Open('GET', 'http://localhost:{#ApiPort}/api/AnalysisParameters', False);
    Request.SetTimeouts(1500, 1500, 3000, 3000);
    Request.Send;
    Result := Request.Status = 200;
  except
    Result := False;
  end;
end;

// the local API serves the catalogue and the web app; started once here so that its database (reference.db) exists when setup ends, and left running for the last page
procedure StartApi;
var
  ResultCode, Waited: Integer;
  ApiExe: String;
begin
  if Flag('NOSTARTAPI') then Exit;
  ApiExe := ExpandConstant('{app}\api\Sportify.Api.exe');
  if not FileExists(ApiExe) then
  begin
    Log('The API was not installed (no api\Sportify.Api.exe).');
    Exit;
  end;
  Status('Starting the local API and preparing its database ...');
  if not ApiAnswers then
    Exec(ApiExe, '', ExpandConstant('{app}\api'), SW_HIDE, ewNoWait, ResultCode);
  Waited := 0;
  while (Waited < 90) and (not ApiAnswers) do
  begin
    Sleep(1000);
    Waited := Waited + 1;
  end;
  if ApiAnswers then
    Log('The local API answers on port {#ApiPort} (waited ' + IntToStr(Waited) + ' s).')
  else
    Log('The local API did not answer within 90 s; it starts when the web app is opened.');
end;

procedure CurStepChanged(CurStep: TSetupStep);
begin
  if CurStep = ssInstall then
  begin
    StopPreviousApi;
    OfferToDisableLegacyAddin;
  end;
  if CurStep = ssPostInstall then
  begin
    Status('Making your Sportify folder ...');
    CreateWorkspace;
    WriteSettings;
    Status('Registering the add-in with Revit ...');
    WriteAddinManifest;
    InstallPrerequisites;
    StartApi;
  end;
end;

// ------------------------------------------------------------------------------------------------------------------------------ uninstall

function InitializeUninstall: Boolean;
begin
  Result := True;
  if UninstallSilent then Exit;
  if RevitRunning then
    Result := MsgBox('Revit is running. Close it first so that the add-in''s files can be removed.' + #13#10#13#10 + 'Continue anyway?', mbConfirmation, MB_YESNO) = IDYES;
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  Legacy: String;
begin
  if CurUninstallStep = usPostUninstall then
  begin
    // an older add-in that setup switched off comes back
    Legacy := AddinsDir('') + '\Sportify.addin' + LegacyDisabledSuffix;
    if FileExists(Legacy) and (not FileExists(AddinsDir('') + '\Sportify.addin')) then
      if (not UninstallSilent) and (MsgBox('Setup switched off an older Sportify add-in. Switch it back on?', mbConfirmation, MB_YESNO) = IDYES) then
        RenameFile(Legacy, AddinsDir('') + '\Sportify.addin');
    if (not UninstallSilent) and DirExists(SettingsDir) then
      if SuppressibleMsgBox('Your Sportify folder with your own files is kept.' + #13#10#13#10 + 'Delete Sportify''s settings file too?', mbConfirmation, MB_YESNO, IDNO) = IDYES then
        DeleteFile(SettingsDir + '\settings.json');
  end;
end;
