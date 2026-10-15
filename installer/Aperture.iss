; Aperture's Windows installer (Inno Setup 6). Built by tools/Build-Release.ps1, which
; publishes the app and passes these defines:
;   /DAppVersion=0.1.0  /DPlatform=x64|ARM64  /DSourceDir=<published app>  /DOutputDir=<folder>
;
; The installer is per user: no administrator prompt, the app goes to
; %LOCALAPPDATA%\Programs\Aperture, and it appears in Settings > Apps > Installed apps.
; Installing a newer version over an older one replaces the program files and keeps the
; library, history and settings in %LOCALAPPDATA%\Aperture. See docs/install.md.

#ifndef AppVersion
  #error AppVersion must be defined (see tools/Build-Release.ps1)
#endif
#ifndef Platform
  #error Platform must be defined (x64 or ARM64)
#endif
#ifndef SourceDir
  #error SourceDir must be defined
#endif
#ifndef OutputDir
  #define OutputDir "."
#endif

[Setup]
; Never change AppId: it is how Windows and this installer recognise an earlier install.
AppId={{940B2F38-5C0F-4831-81FA-4F24BDF190AC}
AppName=Aperture
AppVersion={#AppVersion}
AppVerName=Aperture {#AppVersion}
AppPublisher=Aperture
AppPublisherURL=https://github.com/KhanAdeeb1/Aperture-Video-Player
AppSupportURL=https://github.com/KhanAdeeb1/Aperture-Video-Player/issues
VersionInfoVersion={#AppVersion}
PrivilegesRequired=lowest
DefaultDirName={autopf}\Aperture
; A fixed folder of Aperture's own, so an upgrade can safely clear out the old program files.
DisableDirPage=yes
DisableProgramGroupPage=yes
DefaultGroupName=Aperture
#if Platform == "ARM64"
ArchitecturesAllowed=arm64
ArchitecturesInstallIn64BitMode=arm64
#else
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
#endif
MinVersion=10.0.22000
SetupIconFile=..\src\Aperture.App\Assets\Aperture.ico
UninstallDisplayIcon={app}\Aperture.exe
UninstallDisplayName=Aperture
WizardStyle=modern
Compression=lzma2/max
SolidCompression=yes
OutputDir={#OutputDir}
OutputBaseFilename=Aperture-{#AppVersion}-{#Platform}-setup
CloseApplications=yes
RestartApplications=no
ChangesAssociations=yes

[Tasks]
Name: "desktopicon"; Description: "Add a desktop shortcut"; Flags: unchecked

[InstallDelete]
; Program files from an earlier version (never the user's data, which lives elsewhere).
Type: filesandordirs; Name: "{app}\*"

[Files]
Source: "{#SourceDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{autoprograms}\Aperture"; Filename: "{app}\Aperture.exe"
Name: "{autodesktop}\Aperture"; Filename: "{app}\Aperture.exe"; Tasks: desktopicon

[Registry]
; "Open with" entries for video files. Aperture is offered in the list; it never makes
; itself the default player (Windows leaves that choice to the user).
Root: HKCU; Subkey: "Software\Classes\Aperture.Video"; ValueType: string; ValueData: "Video"; Flags: uninsdeletekey
Root: HKCU; Subkey: "Software\Classes\Aperture.Video\DefaultIcon"; ValueType: string; ValueData: """{app}\Aperture.exe"",0"
Root: HKCU; Subkey: "Software\Classes\Aperture.Video\shell\open\command"; ValueType: string; ValueData: """{app}\Aperture.exe"" ""%1"""
Root: HKCU; Subkey: "Software\Classes\Applications\Aperture.exe"; ValueType: string; ValueName: "FriendlyAppName"; ValueData: "Aperture"; Flags: uninsdeletekey
Root: HKCU; Subkey: "Software\Classes\Applications\Aperture.exe\shell\open\command"; ValueType: string; ValueData: """{app}\Aperture.exe"" ""%1"""
; Keep in step with MediaFileTypes.VideoExtensions (checked by InstallerTests).
; video-extensions-begin
Root: HKCU; Subkey: "Software\Classes\.3gp\OpenWithProgids"; ValueType: string; ValueName: "Aperture.Video"; ValueData: ""; Flags: uninsdeletevalue
Root: HKCU; Subkey: "Software\Classes\.avi\OpenWithProgids"; ValueType: string; ValueName: "Aperture.Video"; ValueData: ""; Flags: uninsdeletevalue
Root: HKCU; Subkey: "Software\Classes\.m2ts\OpenWithProgids"; ValueType: string; ValueName: "Aperture.Video"; ValueData: ""; Flags: uninsdeletevalue
Root: HKCU; Subkey: "Software\Classes\.m4v\OpenWithProgids"; ValueType: string; ValueName: "Aperture.Video"; ValueData: ""; Flags: uninsdeletevalue
Root: HKCU; Subkey: "Software\Classes\.mkv\OpenWithProgids"; ValueType: string; ValueName: "Aperture.Video"; ValueData: ""; Flags: uninsdeletevalue
Root: HKCU; Subkey: "Software\Classes\.mov\OpenWithProgids"; ValueType: string; ValueName: "Aperture.Video"; ValueData: ""; Flags: uninsdeletevalue
Root: HKCU; Subkey: "Software\Classes\.mp4\OpenWithProgids"; ValueType: string; ValueName: "Aperture.Video"; ValueData: ""; Flags: uninsdeletevalue
Root: HKCU; Subkey: "Software\Classes\.mpeg\OpenWithProgids"; ValueType: string; ValueName: "Aperture.Video"; ValueData: ""; Flags: uninsdeletevalue
Root: HKCU; Subkey: "Software\Classes\.mpg\OpenWithProgids"; ValueType: string; ValueName: "Aperture.Video"; ValueData: ""; Flags: uninsdeletevalue
Root: HKCU; Subkey: "Software\Classes\.mts\OpenWithProgids"; ValueType: string; ValueName: "Aperture.Video"; ValueData: ""; Flags: uninsdeletevalue
Root: HKCU; Subkey: "Software\Classes\.ogv\OpenWithProgids"; ValueType: string; ValueName: "Aperture.Video"; ValueData: ""; Flags: uninsdeletevalue
Root: HKCU; Subkey: "Software\Classes\.ts\OpenWithProgids"; ValueType: string; ValueName: "Aperture.Video"; ValueData: ""; Flags: uninsdeletevalue
Root: HKCU; Subkey: "Software\Classes\.webm\OpenWithProgids"; ValueType: string; ValueName: "Aperture.Video"; ValueData: ""; Flags: uninsdeletevalue
Root: HKCU; Subkey: "Software\Classes\.wmv\OpenWithProgids"; ValueType: string; ValueName: "Aperture.Video"; ValueData: ""; Flags: uninsdeletevalue
; video-extensions-end

[Run]
Filename: "{app}\Aperture.exe"; Description: "Open Aperture"; Flags: nowait postinstall skipifsilent

[Code]
const
  DataFolderName = 'Aperture';

// The media server keeps running after the app closes; stop it so its files can be replaced
// or removed. "stop" asks it to shut down cleanly and returns once it has (or wasn't running).
procedure StopMediaServer(const AppDir: String);
var
  Server: String;
  ResultCode: Integer;
begin
  Server := AddBackslash(AppDir) + 'Server\Aperture.Server.exe';
  if FileExists(Server) then
    Exec(Server, 'stop', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
begin
  StopMediaServer(ExpandConstant('{app}'));
  Result := '';
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  DataDir: String;
  ResultCode: Integer;
begin
  if CurUninstallStep = usUninstall then
    StopMediaServer(ExpandConstant('{app}'));

  // Data is kept unless the person asks for it to go. A silent uninstall never removes it.
  if (CurUninstallStep = usPostUninstall) and (not UninstallSilent) then
  begin
    DataDir := ExpandConstant('{localappdata}\') + DataFolderName;
    if DirExists(DataDir) and (MsgBox(
      'Also remove your Aperture library, watch history, bookmarks and settings?' + #13#10 + #13#10 +
      'Your videos are not touched either way. Keep them if you might install Aperture again.',
      mbConfirmation, MB_YESNO or MB_DEFBUTTON2) = IDYES) then
    begin
      DelTree(DataDir, True, True, True);
      // Keys and sign-ins saved in Windows Credential Manager.
      Exec(ExpandConstant('{sys}\cmdkey.exe'), '/delete:Aperture/TMDB', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
      Exec(ExpandConstant('{sys}\cmdkey.exe'), '/delete:Aperture/YouTube', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
      Exec(ExpandConstant('{sys}\cmdkey.exe'), '/delete:Aperture/YouTubeClientId', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
      Exec(ExpandConstant('{sys}\cmdkey.exe'), '/delete:Aperture/YouTubeClientSecret', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
      Exec(ExpandConstant('{sys}\cmdkey.exe'), '/delete:Aperture/YouTubeRefreshToken', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
      // The media server's own certificate authority, kept in the user's certificate store.
      Exec(ExpandConstant('{sys}\WindowsPowerShell\v1.0\powershell.exe'),
        '-NoProfile -NonInteractive -Command "Get-ChildItem Cert:\CurrentUser\My | Where-Object { $_.Subject -like ''CN=Aperture Media Server*'' } | Remove-Item"',
        '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
    end;
  end;
end;
