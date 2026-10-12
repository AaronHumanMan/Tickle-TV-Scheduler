; Tickle TV Scheduler installer. Build it with installer\build.ps1, which publishes the app and then runs this script.
; Installs per user by default (no administrator prompt), puts Tickle TV in the Start Menu, and can start it at sign-in.

#ifndef Version
  #define Version "0.1.0"
#endif
#define AppName "Tickle TV Scheduler"
#define ExeName "TickleTV.Scheduler.Web.exe"

[Setup]
AppId={{7C1E3C6A-5D2B-4E8F-9A61-2F0B6D4C8E11}
AppName={#AppName}
AppVersion={#Version}
AppVerName={#AppName} {#Version}
AppPublisher=Tickle TV
DefaultDirName={autopf}\Tickle TV
DefaultGroupName=Tickle TV
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=commandline
OutputDir=dist
OutputBaseFilename=TickleTV-Setup-{#Version}
SetupIconFile=..\src\TickleTV.Scheduler.Web\app.ico
UninstallDisplayIcon={app}\{#ExeName}
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
CloseApplications=no

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Messages]
WelcomeLabel2=This will install [name/ver] on your computer.%n%nTickle TV turns your video files into a TV channel with commercial breaks, and hands it to ErsatzTV to play. It runs quietly by the clock on your taskbar and opens in your web browser.

[Tasks]
Name: "startup"; Description: "Start Tickle TV automatically when I sign in to Windows (it builds each night's schedule on its own)"; GroupDescription: "Running in the background:"
Name: "desktopicon"; Description: "Put a Tickle TV shortcut on the desktop"; GroupDescription: "Shortcuts:"; Flags: unchecked

[Files]
Source: "publish\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\Tickle TV"; Filename: "{app}\{#ExeName}"; Comment: "Open Tickle TV in your browser"
Name: "{group}\Uninstall Tickle TV"; Filename: "{uninstallexe}"
Name: "{autodesktop}\Tickle TV"; Filename: "{app}\{#ExeName}"; Tasks: desktopicon
Name: "{userstartup}\Tickle TV"; Filename: "{app}\{#ExeName}"; Parameters: "--quiet"; Tasks: startup

[Run]
Filename: "{app}\{#ExeName}"; Description: "Start Tickle TV now"; Flags: nowait postinstall skipifsilent

[Code]
// The app has no window to close, so stop it before files are replaced or removed.
procedure StopRunningApp;
var
  ResultCode: Integer;
begin
  Exec(ExpandConstant('{sys}\taskkill.exe'), '/F /IM {#ExeName}', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
begin
  StopRunningApp;
  Result := '';
end;

function InitializeUninstall(): Boolean;
begin
  StopRunningApp;
  Result := True;
end;
