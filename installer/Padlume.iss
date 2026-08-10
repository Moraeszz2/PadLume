; Inno Setup script for the Padlume installer.
; Compile with: ISCC.exe installer\Padlume.iss (from the project root, after publishing to publish\).

#define MyAppName "Padlume"
; The release workflow passes the version via /DMyAppVersion=x.y.z (the git tag); this is just the
; default for anyone compiling locally without passing anything.
#ifndef MyAppVersion
  #define MyAppVersion "1.0.0"
#endif
#define MyAppPublisher "Padlume"
#define MyAppExeName "Padlume.exe"
#define MyPublishDir "..\publish"

[Setup]
AppId={{B8E5F4A2-6D3C-4E7A-9F1B-2C8D5A6E7F90}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppPublisher={#MyAppPublisher}
DefaultDirName={autopf}\{#MyAppName}
DefaultGroupName={#MyAppName}
DisableProgramGroupPage=yes
; Padlume.exe itself already requires elevation (app.manifest); installing into Program Files does too
; — so the installer just asks for admin once, instead of Windows asking again every time the app opens.
PrivilegesRequired=admin
OutputDir=..
OutputBaseFilename=Setup
SetupIconFile=..\Assets\app.ico
UninstallDisplayIcon={app}\{#MyAppExeName}
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
; Self-contained single-file already embeds the .NET runtime — nothing to check for that here.

; Portuguese first: if the Windows language doesn't match either of the two listed, Inno Setup uses
; the first one in the list as the default — the same fallback rule Strings.cs uses in the app itself.
[Languages]
Name: "brazilianportuguese"; MessagesFile: "compiler:Languages\BrazilianPortuguese.isl"
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
; {cm:...} uses the default messages Inno Setup itself already translates in the two languages above —
; no manual translation needed for this generic installer text.
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"

[Files]
Source: "{#MyPublishDir}\{#MyAppExeName}"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#MyPublishDir}\Assets\*.png"; DestDir: "{app}\Assets"; Flags: ignoreversion

[Icons]
Name: "{group}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"
Name: "{group}\{cm:UninstallProgram,{#MyAppName}}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; Tasks: desktopicon

[Run]
; Launching Padlume.exe directly here fails with Win32 error 740 (ERROR_ELEVATION_REQUIRED): Setup
; itself runs elevated (PrivilegesRequired=admin above), but Inno Setup's [Run] launcher hands the
; post-install process off to the original (non-elevated) user's shell token to avoid leaving stray
; elevated apps running — which collides with Padlume.exe's own manifest also demanding elevation.
; Routing through "cmd /c start" forces a real ShellExecute-based launch instead, which correctly
; re-triggers UAC for the target's manifest. This is the standard workaround for "elevated installer
; + elevated app" in Inno Setup.
Filename: "{cmd}"; Parameters: "/C start """" ""{app}\{#MyAppExeName}"""; Description: "{cm:LaunchProgram,{#MyAppName}}"; Flags: nowait postinstall skipifsilent runhidden

[UninstallDelete]
; The app writes history/config to %AppData%\Padlume — asks explicitly instead of deleting it outright,
; since it's user data (battery history) and not just disposable cache.
Type: dirifempty; Name: "{app}"

[Code]
procedure KillPadlume;
var
  ResultCode: Integer;
begin
  // Best-effort: ignores failure (e.g. it wasn't running). /T also kills the compact widget window,
  // which runs as a child window of the same process, not a separate one.
  Exec('taskkill.exe', '/F /IM {#MyAppExeName} /T', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
end;

procedure CurStepChanged(CurStep: TSetupStep);
begin
  // Without this, installing an update over a running instance leaves the old process locking
  // Padlume.exe, and the new build either fails to overwrite it or ends up running two instances
  // (which would fight over enabling/disabling the same controllers) until the user manually closes
  // the old one and reopens the app.
  if CurStep = ssInstall then
    KillPadlume;
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  ResultCode: Integer;
begin
  if CurUninstallStep = usUninstall then
  begin
    // Without this, the running process keeps Padlume.exe locked (some files can't be removed, and
    // the app/tray icon stays open even though it's just been "uninstalled" from the user's perspective).
    KillPadlume;

    // Neither of these is created by [Files]/[Registry] entries — they're written by the app itself
    // at runtime (StartupManager, RemoteControlServer), so Inno Setup's own uninstall log never
    // learns about them and won't clean them up on its own.
    RegDeleteValue(HKCU, 'SOFTWARE\Microsoft\Windows\CurrentVersion\Run', '{#MyAppName}');
    Exec('netsh.exe', 'advfirewall firewall delete rule name="{#MyAppName} Phone Control"', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
  end;
end;
