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
Filename: "{app}\{#AppExe}"; Description: "{cm:LaunchProgram,{#AppName}}"; Flags: nowait postinstall skipifsilent

#if Variant == "light"
[Code]
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
