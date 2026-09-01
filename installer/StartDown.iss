#define MyAppName "StartDown"
#define MyAppPublisher "Linho"
#define MyAppURL "https://github.com/Linho1219/startdown"
#define MyAppExeName "StartDown.exe"
#define RepoRoot AddBackslash(SourcePath) + ".."
#ifndef PublishDir
  #define PublishDir RepoRoot + "\artifacts\installer-publish\self-contained"
#endif
#ifndef InstallerFlavor
  #define InstallerFlavor "self-contained"
#endif
#define MyAppExe PublishDir + "\" + MyAppExeName
#define MyAppVersion GetFileProductVersionString(MyAppExe)
#define MyAppFileVersion GetVersionNumbersString(MyAppExe)

[Setup]
; Stable product identity. Never change this AppId after publishing a release.
AppId={{B6BA0951-4AE9-4FC3-AA9F-EAA82D9D1F69}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppVerName={#MyAppName} {#MyAppVersion}
AppPublisher={#MyAppPublisher}
AppPublisherURL={#MyAppURL}
AppSupportURL={#MyAppURL}/issues
AppUpdatesURL={#MyAppURL}/releases
AppComments=登录时启动后台应用并按规则收起其窗口（{#InstallerFlavor}）

VersionInfoVersion={#MyAppFileVersion}
VersionInfoProductVersion={#MyAppFileVersion}
VersionInfoCompany={#MyAppPublisher}
VersionInfoDescription={#MyAppName} Installer ({#InstallerFlavor})
VersionInfoProductName={#MyAppName}

PrivilegesRequired=lowest
DefaultDirName={localappdata}\Programs\{#MyAppName}
DefaultGroupName={#MyAppName}
DisableDirPage=yes
DisableProgramGroupPage=yes
UsePreviousAppDir=no
DirExistsWarning=no

SetupArchitecture=x64
ArchitecturesAllowed=x64compatible
MinVersion=10.0.17763

SetupIconFile={#RepoRoot}\assets\icon.ico
UninstallDisplayName={#MyAppName}
UninstallDisplayIcon={app}\app\{#MyAppExeName}

OutputDir={#RepoRoot}\artifacts\installer
OutputBaseFilename={#MyAppName}-Setup-{#MyAppVersion}-win-x64-{#InstallerFlavor}
Compression=lzma2/ultra64
SolidCompression=yes
WizardStyle=modern dynamic

CloseApplications=yes
RestartApplications=no
ChangesAssociations=no
ChangesEnvironment=no

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"
Name: "chinesesimp"; MessagesFile: "compiler:Languages\ChineseSimplified.isl"

[CustomMessages]
english.DotNet10DesktopRuntimeMissing=This installer requires Microsoft .NET Desktop Runtime 10 (x64). Install it first, or use the StartDown self-contained installer.%n%nhttps://dotnet.microsoft.com/download/dotnet/10.0
chinesesimp.DotNet10DesktopRuntimeMissing=此安装包需要 Microsoft .NET Desktop Runtime 10（x64）。请先安装该运行时，或改用 StartDown self-contained 安装包。%n%nhttps://dotnet.microsoft.com/download/dotnet/10.0

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Files]
Source: "{#PublishDir}\*"; DestDir: "{app}\app"; Excludes: "*.pdb"; Flags: ignoreversion recursesubdirs createallsubdirs

[InstallDelete]
Type: filesandordirs; Name: "{app}\app"
Type: files; Name: "{group}\{#MyAppName}.lnk"
Type: files; Name: "{autodesktop}\{#MyAppName}.lnk"

[Icons]
Name: "{group}\{#MyAppName}"; Filename: "{app}\app\{#MyAppExeName}"; WorkingDir: "{app}\app"
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\app\{#MyAppExeName}"; WorkingDir: "{app}\app"; Tasks: desktopicon

[Run]
Filename: "{app}\app\{#MyAppExeName}"; Description: "{cm:LaunchProgram,{#StringChange(MyAppName, '&', '&&')}}"; WorkingDir: "{app}\app"; Flags: nowait postinstall skipifsilent

[Code]
const
  RunKey = 'Software\Microsoft\Windows\CurrentVersion\Run';
  RunValueName = 'StartDown';

function CommandStartsWithExecutable(const CommandLine, ExecutablePath: String): Boolean;
var
  ExpectedExecutable: String;
  ExpectedLength: Integer;
begin
  Result := False;
  ExpectedExecutable := '"' + ExecutablePath + '"';
  ExpectedLength := Length(ExpectedExecutable);

  if CompareText(Copy(CommandLine, 1, ExpectedLength), ExpectedExecutable) <> 0 then
    Exit;

  if Length(CommandLine) = ExpectedLength then
  begin
    Result := True;
    Exit;
  end;

  if Length(CommandLine) > ExpectedLength then
    Result := CommandLine[ExpectedLength + 1] = ' ';
end;

function IsOwnedAutoStartCommand(const CommandLine: String): Boolean;
begin
  Result :=
    CommandStartsWithExecutable(
      CommandLine, ExpandConstant('{app}\app\{#MyAppExeName}')) or
    CommandStartsWithExecutable(
      CommandLine, ExpandConstant('{app}\{#MyAppExeName}'));
end;

#if InstallerFlavor == "framework-dependent"
function HasDotNet10DesktopRuntimeAt(const DotNetRoot: String): Boolean;
var
  FindRec: TFindRec;
  RuntimePath: String;
begin
  Result := False;
  RuntimePath := AddBackslash(DotNetRoot) +
    'shared\Microsoft.WindowsDesktop.App\10.*';

  if FindFirst(RuntimePath, FindRec) then
  begin
    try
      repeat
        if (FindRec.Attributes and FILE_ATTRIBUTE_DIRECTORY) <> 0 then
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

function InitializeSetup: Boolean;
begin
  Result :=
    HasDotNet10DesktopRuntimeAt(ExpandConstant('{commonpf}\dotnet')) or
    HasDotNet10DesktopRuntimeAt(ExpandConstant('{userprofile}\.dotnet'));

  if not Result then
    SuppressibleMsgBox(
      CustomMessage('DotNet10DesktopRuntimeMissing'),
      mbError, MB_OK, IDOK);
end;
#endif

procedure RemoveOwnedAutoStartValue;
var
  CommandLine: String;
begin
  if not RegQueryStringValue(HKEY_CURRENT_USER, RunKey, RunValueName, CommandLine) then
    Exit;

  if IsOwnedAutoStartCommand(CommandLine) then
  begin
    if RegDeleteValue(HKEY_CURRENT_USER, RunKey, RunValueName) then
      Log('Removed the StartDown current-user auto-start value.')
    else
      Log('Could not remove the StartDown current-user auto-start value.');
  end
  else
    Log('Preserved a StartDown auto-start value owned by another executable path.');
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
begin
  if CurUninstallStep = usUninstall then
    RemoveOwnedAutoStartValue;
end;
