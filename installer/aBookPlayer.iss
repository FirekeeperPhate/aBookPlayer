; aBookPlayer installer (Inno Setup). Built by build.ps1, which passes:
;   /DVariant=full|light   full = self-contained (.NET runtime included), light = requires the .NET 10 Desktop Runtime
;   /DAppVersion=x.y.z     /DPublishDir=<dotnet publish output>

#ifndef Variant
  #define Variant "full"
#endif
#ifndef AppVersion
  #define AppVersion "1.0.0"
#endif
#ifndef PublishDir
  #error PublishDir is not defined: build with build.ps1
#endif

#if Variant == "light"
  #define Suffix "-light"
#else
  #define Suffix ""
#endif

#define AppName "aBookPlayer"
#define AppExe "aBookPlayer.exe"

[Setup]
; Same AppId for both variants: installing one upgrades/replaces the other
AppId={{7E1C4A2B-3D5F-4B8E-9A61-2F0C8D4B7A13}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}
AppPublisher={#AppName}
DefaultDirName={autopf}\{#AppName}
DefaultGroupName={#AppName}
DisableProgramGroupPage=yes
; Lets the user choose between installing for everyone (admin) or just for themselves (no admin)
PrivilegesRequired=admin
PrivilegesRequiredOverridesAllowed=dialog
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0
SetupIconFile=..\Resources\aBookPlayer.ico
UninstallDisplayIcon={app}\{#AppExe}
UninstallDisplayName={#AppName}
InfoBeforeFile=License.rtf
WizardStyle=modern
Compression=lzma2/ultra64
SolidCompression=yes
ChangesAssociations=yes
CloseApplications=yes
OutputDir=out
OutputBaseFilename={#AppName}-{#AppVersion}-x64{#Suffix}-setup
VersionInfoVersion={#AppVersion}
VersionInfoProductName={#AppName}

; English only, like the app: no language to choose
ShowLanguageDialog=no

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[CustomMessages]
DotNetMissing=aBookPlayer requires the .NET 10 Desktop Runtime (x64), which is not installed on this PC.%n%nOpen the Microsoft download page now? Setup will close: run it again after installing the runtime.%n%nAlternatively, use the full aBookPlayer installer, which includes the runtime.

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Files]
Source: "{#PublishDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{autoprograms}\{#AppName}"; Filename: "{app}\{#AppExe}"; Comment: "Audiobook player with chapters and subtitles"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#AppExe}"; Tasks: desktopicon

[Registry]
; "Open with" entries for the supported formats (the default app is not changed).
; HKA = HKLM when installing for all users, HKCU when installing just for the current user.
Root: HKA; Subkey: "Software\Classes\aBookPlayer.AudioFile"; ValueType: string; ValueData: "Audio file (aBookPlayer)"; Flags: uninsdeletekey
Root: HKA; Subkey: "Software\Classes\aBookPlayer.AudioFile\DefaultIcon"; ValueType: string; ValueData: """{app}\{#AppExe}"",0"
Root: HKA; Subkey: "Software\Classes\aBookPlayer.AudioFile\shell\open\command"; ValueType: string; ValueData: """{app}\{#AppExe}"" ""%1"""
Root: HKA; Subkey: "Software\Classes\Applications\{#AppExe}"; ValueType: string; ValueName: "FriendlyAppName"; ValueData: "{#AppName}"; Flags: uninsdeletekey
Root: HKA; Subkey: "Software\Classes\Applications\{#AppExe}\shell\open\command"; ValueType: string; ValueData: """{app}\{#AppExe}"" ""%1"""
#define Ext(E) \
  "Root: HKA; Subkey: ""Software\Classes\." + E + "\OpenWithProgids""; ValueType: string; ValueName: ""aBookPlayer.AudioFile""; ValueData: """"; Flags: uninsdeletevalue" + NewLine + \
  "Root: HKA; Subkey: ""Software\Classes\Applications\" + AppExe + "\SupportedTypes""; ValueType: string; ValueName: ""." + E + """; ValueData: """""
{#Ext("mp3")}
{#Ext("m4a")}
{#Ext("m4b")}
{#Ext("aac")}
{#Ext("mp4")}
{#Ext("wma")}
{#Ext("wav")}
{#Ext("flac")}
{#Ext("aiff")}
{#Ext("aif")}
{#Ext("ogg")}
{#Ext("srt")}

[Run]
; Phones on the local network may reach the library the app shares (File → Share with your phone): a firewall rule
; for the app, from local addresses only. Installing for everyone only: a rule needs administrator rights (the app
; itself offers to add it otherwise). Added only when the app has no rule yet: an earlier install's is kept, and so is
; a "block" rule (access refused in Windows' own question), which would win over this one anyway.
Filename: "{sys}\netsh.exe"; Parameters: "advfirewall firewall add rule name={#AppName} dir=in action=allow program=""{app}\{#AppExe}"" enable=yes profile=any remoteip=LocalSubnet,10.0.0.0/8,172.16.0.0/12,192.168.0.0/16,169.254.0.0/16,fe80::/10"; Flags: runhidden; Check: IsAdminInstallMode and not HasFirewallRule; StatusMsg: "Allowing phones on this network through Windows Firewall..."
Filename: "{app}\{#AppExe}"; Description: "{cm:LaunchProgram,{#AppName}}"; Flags: nowait postinstall skipifsilent
; Started by the app's own update (silent, /UPDATE=1): open it again when done, as the user (not elevated)
Filename: "{app}\{#AppExe}"; Flags: nowait runasoriginaluser; Check: IsAppUpdate

[UninstallRun]
Filename: "{sys}\netsh.exe"; Parameters: "advfirewall firewall delete rule name={#AppName} dir=in program=""{app}\{#AppExe}"""; Flags: runhidden; Check: IsAdminInstallMode; RunOnceId: "FirewallRule"

[Code]
{ Whether Windows Firewall has a rule named after the app for this copy of it (allow or block) }
function HasFirewallRule: Boolean;
var
  Policy, Rule: Variant;
begin
  Result := False;
  try
    Policy := CreateOleObject('HNetCfg.FwPolicy2');
    Rule := Policy.Rules.Item('{#AppName}');
    Result := CompareText(Rule.ApplicationName, ExpandConstant('{app}\{#AppExe}')) = 0;
  except
    { No rule of that name: one is added }
  end;
end;

function IsAppUpdate: Boolean;
begin
  Result := ExpandConstant('{param:UPDATE|0}') = '1';
end;

#if Variant == "light"
function IsDesktopRuntime10Installed: Boolean;
var
  FindRec: TFindRec;
begin
  Result := FindFirst(ExpandConstant('{commonpf64}\dotnet\shared\Microsoft.WindowsDesktop.App\10.*'), FindRec);
  if Result then
    FindClose(FindRec);
end;

function InitializeSetup: Boolean;
var
  ErrorCode: Integer;
begin
  Result := True;
  if not IsDesktopRuntime10Installed then
  begin
    if SuppressibleMsgBox(CustomMessage('DotNetMissing'), mbConfirmation, MB_YESNO, IDYES) = IDYES then
      ShellExec('open', 'https://dotnet.microsoft.com/download/dotnet/10.0', '', '', SW_SHOWNORMAL, ewNoWait, ErrorCode);
    Result := False;
  end;
end;
#endif
