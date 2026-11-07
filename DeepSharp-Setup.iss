#define AppName "DeepSharp"
#define AppVersion "0.3.1"
#define AppPublisher "DeepSharp"
#define AppExecutable "DeepSharp.exe"
#define LauncherExecutable "DeepSharpLauncher.exe"

[Setup]
AppId={{F2187D54-C520-40FC-9FA4-8B029C9F004B}
AppName={#AppName}
AppVersion={#AppVersion}
AppPublisher={#AppPublisher}
DefaultDirName={autopf}\DeepSharp
DefaultGroupName=DeepSharp
DisableProgramGroupPage=yes
PrivilegesRequired=admin
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
OutputDir=installer
OutputBaseFilename=DeepSharp-Setup
SetupIconFile=appicon.ico
UninstallDisplayIcon={app}\bin\{#AppExecutable}
WizardStyle=modern
Compression=lzma2
SolidCompression=yes
ChangesEnvironment=yes

[Tasks]
Name: "desktopicon"; Description: "Create a desktop shortcut"; GroupDescription: "Additional shortcuts:"; Flags: unchecked

[Files]
Source: "dist\DeepSharp-Avalonia-english\*"; DestDir: "{app}\bin"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{autoprograms}\DeepSharp"; Filename: "{app}\bin\{#LauncherExecutable}"; WorkingDir: "{app}\bin"; IconFilename: "{app}\bin\{#LauncherExecutable}"
Name: "{autodesktop}\DeepSharp"; Filename: "{app}\bin\{#LauncherExecutable}"; WorkingDir: "{app}\bin"; IconFilename: "{app}\bin\{#LauncherExecutable}"; Tasks: desktopicon

[Run]
Filename: "{app}\bin\{#LauncherExecutable}"; Description: "Launch DeepSharp"; WorkingDir: "{app}\bin"; Flags: postinstall nowait skipifsilent

[Code]
const
  EnvironmentKey = 'SYSTEM\CurrentControlSet\Control\Session Manager\Environment';
  UninstallKey = 'SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\{F2187D54-C520-40FC-9FA4-8B029C9F004B}_is1';

function NormalizePathEntry(Value: String): String;
begin
  Result := Uppercase(Trim(Value));
  while (Length(Result) > 0) and (Result[Length(Result)] = '\') do
    Delete(Result, Length(Result), 1);
end;

function HasPathEntry(CurrentPath, Directory: String): Boolean;
var
  StartAt, Index: Integer;
  Entry: String;
begin
  Result := False;
  CurrentPath := CurrentPath + ';';
  StartAt := 1;
  for Index := 1 to Length(CurrentPath) do
    if CurrentPath[Index] = ';' then
    begin
      Entry := Copy(CurrentPath, StartAt, Index - StartAt);
      if NormalizePathEntry(Entry) = NormalizePathEntry(Directory) then
      begin
        Result := True;
        Exit;
      end;
      StartAt := Index + 1;
    end;
end;

function RemovePathEntry(CurrentPath, Directory: String): String;
var
  StartAt, Index: Integer;
  Entry: String;
begin
  Result := '';
  CurrentPath := CurrentPath + ';';
  StartAt := 1;
  for Index := 1 to Length(CurrentPath) do
    if CurrentPath[Index] = ';' then
    begin
      Entry := Copy(CurrentPath, StartAt, Index - StartAt);
      if (NormalizePathEntry(Entry) <> '') and
         (NormalizePathEntry(Entry) <> NormalizePathEntry(Directory)) then
      begin
        if Result <> '' then Result := Result + ';';
        Result := Result + Entry;
      end;
      StartAt := Index + 1;
    end;
end;

procedure AddToSystemPath(Directory: String);
var
  CurrentPath: String;
begin
  if not RegQueryStringValue(HKEY_LOCAL_MACHINE, EnvironmentKey, 'Path', CurrentPath) then
    CurrentPath := '';
  if HasPathEntry(CurrentPath, Directory) then Exit;
  if CurrentPath <> '' then CurrentPath := CurrentPath + ';';
  if not RegWriteExpandStringValue(HKEY_LOCAL_MACHINE, EnvironmentKey, 'Path', CurrentPath + Directory) then
    MsgBox('DeepSharp could not be added to the system PATH. You can add its bin folder manually.', mbError, MB_OK)
  else
    RegWriteDWordValue(HKEY_LOCAL_MACHINE, UninstallKey, 'DeepSharpAddedPath', 1);
end;

procedure RemoveFromSystemPath(Directory: String);
var
  CurrentPath: String;
  AddedByInstaller: Cardinal;
begin
  if not RegQueryDWordValue(HKEY_LOCAL_MACHINE, UninstallKey, 'DeepSharpAddedPath', AddedByInstaller) or
     (AddedByInstaller <> 1) then Exit;
  if not RegQueryStringValue(HKEY_LOCAL_MACHINE, EnvironmentKey, 'Path', CurrentPath) then Exit;
  RegWriteExpandStringValue(HKEY_LOCAL_MACHINE, EnvironmentKey, 'Path', RemovePathEntry(CurrentPath, Directory));
end;

function IsDotNet9RuntimeInstalled(): Boolean;
var
  FindRec: TFindRec;
begin
  Result := FindFirst(ExpandConstant('{commonpf64}\dotnet\shared\Microsoft.NETCore.App\9.*'), FindRec);
  if Result then
    FindClose(FindRec);
end;

procedure CurStepChanged(CurStep: TSetupStep);
begin
  if CurStep = ssPostInstall then
    AddToSystemPath(ExpandConstant('{app}\bin'));
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
begin
  if CurUninstallStep = usUninstall then
    RemoveFromSystemPath(ExpandConstant('{app}\bin'));
end;

function InitializeSetup(): Boolean;
var
  ErrorCode: Integer;
begin
  Result := True;
  if not IsDotNet9RuntimeInstalled() then
  begin
    if MsgBox('DeepSharp requires the .NET 9 Runtime (x64). Open the official download page now?', mbConfirmation, MB_YESNO) = IDYES then
      ShellExec('open', 'https://dotnet.microsoft.com/en-us/download/dotnet/9.0', '', '', SW_SHOWNORMAL, ewNoWait, ErrorCode);
    MsgBox('Install the .NET 9 Runtime (x64), then run the DeepSharp installer again.', mbInformation, MB_OK);
    Result := False;
  end;
end;
