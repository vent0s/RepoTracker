; App identity and install mode must remain stable across releases.
#ifndef PayloadDir
  #error PayloadDir is required; run build-installer.ps1.
#endif
#ifndef ProductId
  #define ProductId "Septim.RepoTracker"
#endif
#ifndef ProductName
  #define ProductName "RepoTracker"
#endif
#ifndef AppPathName
  #define AppPathName "RepoTracker.Desktop.exe"
#endif
#ifndef RunningMutex
  #define RunningMutex "Local\Septim.RepoTracker.Installed"
#endif

[Setup]
AppId={#ProductId}
AppName={#ProductName}
AppVersion={#ReleaseVersion}
AppPublisher=RepoTracker
VersionInfoVersion={#ReleaseFileVersion}
DefaultDirName={localappdata}\Programs\{#ProductName}
DefaultGroupName={#ProductName}
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.17763
UsePreviousAppDir=yes
DisableDirPage=auto
DisableProgramGroupPage=yes
AllowNoIcons=yes
AllowRootDirectory=no
AllowUNCPath=no
AllowNetworkDrive=no
UninstallDisplayIcon={app}\RepoTracker.Desktop.exe
UninstallDisplayName={#ProductName}
SetupMutex=Local\{#ProductId}.Setup
CloseApplications=yes
CloseApplicationsFilter=*.exe,*.dll
RestartApplications=yes
OutputDir={#OutputDir}
OutputBaseFilename=RepoTracker-{#ReleaseVersion}-win-x64-Setup
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
SetupIconFile=..\src\RepoTracker.Desktop\Assets\RepoTracker.ico

[Languages]
Name: "chinesesimplified"; MessagesFile: "compiler:Languages\ChineseSimplified.isl"
Name: "english"; MessagesFile: "compiler:Default.isl"

[CustomMessages]
chinesesimplified.UpgradePath=检测到已安装版本。请使用原安装位置：%1
english.UpgradePath=An installation already exists. Please use its registered directory: %1
chinesesimplified.NewerVersion=已安装较新版本。为保护配置，安装程序不会降级覆盖。
english.NewerVersion=A newer version is installed. Downgrading is blocked to protect your settings.
chinesesimplified.NonEmpty=首次安装请选择新的空文件夹。用户配置会自动从原数据目录读取。
english.NonEmpty=Choose a new empty folder for the first installation. Existing settings are read from your separate data folder.
chinesesimplified.CloseBeforeUninstall=请先从 RepoTracker 托盘菜单选择“退出”，然后重新卸载。仓库和个人设置会保留。
english.CloseBeforeUninstall=Exit RepoTracker from its tray menu before uninstalling. Your repositories and settings will be preserved.
chinesesimplified.Launch=启动 RepoTracker
english.Launch=Launch RepoTracker

[Files]
Source: "{#PayloadDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "{#FileList}"; DestDir: "{app}"; DestName: "installed-files.sha256"; Flags: ignoreversion

[Icons]
Name: "{autoprograms}\{#ProductName}"; Filename: "{app}\RepoTracker.Desktop.exe"; AppUserModelID: "{#ProductId}"

[Registry]
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\App Paths\{#AppPathName}"; ValueType: string; ValueName: ""; ValueData: "{app}\RepoTracker.Desktop.exe"; Flags: uninsdeletekey

[INI]
Filename: "{app}\installed.ini"; Section: "Installation"; Key: "AppId"; String: "{#ProductId}"; Flags: uninsdeleteentry uninsdeletesectionifempty

[UninstallDelete]
Type: files; Name: "{app}\installed.ini"

[Run]
Filename: "{app}\RepoTracker.Desktop.exe"; Description: "{cm:Launch}"; Flags: nowait postinstall skipifsilent

[Code]
const
  UninstallKey = 'Software\Microsoft\Windows\CurrentVersion\Uninstall\{#ProductId}_is1';
var
  PreviousFiles: TArrayOfString;

function GetFileAttributes(FileName: String): LongWord;
  external 'GetFileAttributesW@kernel32.dll stdcall';

function CanonicalDir(const Path: String): String;
begin
  Result := RemoveBackslashUnlessRoot(ExpandFileName(Path));
end;

function InitializeSetup(): Boolean;
var
  OldVersion: String;
  OldPacked, NewPacked: Int64;
begin
  Result := True;
  if RegQueryStringValue(HKCU, UninstallKey, 'DisplayVersion', OldVersion) then
    if StrToVersion(OldVersion, OldPacked) and StrToVersion('{#ReleaseFileVersion}', NewPacked) then
      if ComparePackedVersion(OldPacked, NewPacked) > 0 then begin
        SuppressibleMsgBox(CustomMessage('NewerVersion'), mbError, MB_OK, IDOK);
        Result := False;
      end;
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
var
  PreviousDir, Target: String;
  Entry: TFindRec;
begin
  Result := '';
  Target := CanonicalDir(ExpandConstant('{app}'));
  if RegQueryStringValue(HKCU, UninstallKey, 'Inno Setup: App Path', PreviousDir) then begin
    if CompareText(CanonicalDir(PreviousDir), Target) <> 0 then begin
      Result := FmtMessage(CustomMessage('UpgradePath'), [PreviousDir]);
      Exit;
    end;
    LoadStringsFromFile(AddBackslash(Target) + 'installed-files.sha256', PreviousFiles);
  end else if FindFirst(AddBackslash(Target) + '*', Entry) then begin
    try
      repeat
        if (Entry.Name <> '.') and (Entry.Name <> '..') then begin
          Result := CustomMessage('NonEmpty');
          Exit;
        end;
      until not FindNext(Entry);
    finally
      FindClose(Entry);
    end;
  end;
end;

function SafeOwnedPath(const RelativePath: String): Boolean;
var
  Part: String;
  Attributes: LongWord;
begin
  Result := False;
  if (RelativePath = '') or (Pos(':', RelativePath) > 0) or
     (Pos('..', RelativePath) > 0) or (RelativePath[1] = '\') or
     (RelativePath[1] = '/') then Exit;
  Part := ExpandConstant('{app}') + '\' + RelativePath;
  while Length(Part) >= Length(ExpandConstant('{app}')) do begin
    Attributes := GetFileAttributes(Part);
    if (Attributes <> $FFFFFFFF) and ((Attributes and $400) <> 0) then Exit;
    Part := ExtractFileDir(Part);
  end;
  Result := True;
end;

procedure RemoveObsoletePackageFiles;
var
  CurrentFiles: TArrayOfString;
  I, J: Integer;
  Hash, RelativePath, FilePath: String;
  StillPresent: Boolean;
begin
  if not LoadStringsFromFile(ExpandConstant('{app}\installed-files.sha256'), CurrentFiles) then Exit;
  for I := 0 to GetArrayLength(PreviousFiles) - 1 do begin
    if Copy(PreviousFiles[I], 65, 2) = ' *' then begin
      Hash := Copy(PreviousFiles[I], 1, 64);
      RelativePath := Copy(PreviousFiles[I], 67, MaxInt);
      StillPresent := False;
      for J := 0 to GetArrayLength(CurrentFiles) - 1 do
        if CompareText(Copy(CurrentFiles[J], 67, MaxInt), RelativePath) = 0 then StillPresent := True;
      if not StillPresent and SafeOwnedPath(RelativePath) then begin
        FilePath := ExpandConstant('{app}\') + RelativePath;
        // Never remove modified or unlisted user files, even in the program directory.
        if FileExists(FilePath) then
          if CompareText(GetSHA256OfFile(FilePath), Hash) = 0 then begin
            Log('Removing obsolete package file: ' + RelativePath);
            DeleteFile(FilePath);
          end;
      end;
    end;
  end;
end;

procedure CurStepChanged(CurStep: TSetupStep);
begin
  if CurStep = ssPostInstall then RemoveObsoletePackageFiles;
end;

function InitializeUninstall(): Boolean;
begin
  Result := not CheckForMutexes('{#RunningMutex}');
  if not Result then SuppressibleMsgBox(CustomMessage('CloseBeforeUninstall'), mbError, MB_OK, IDOK);
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  Names: TArrayOfString;
  I: Integer;
  Command, Prefix: String;
begin
  if CurUninstallStep <> usUninstall then Exit;
  // Remove only login entries pointing at this exact installed executable.
  Prefix := '"' + ExpandConstant('{app}\RepoTracker.Desktop.exe') + '" --profile "';
  if RegGetValueNames(HKCU, 'Software\Microsoft\Windows\CurrentVersion\Run', Names) then
    for I := 0 to GetArrayLength(Names) - 1 do
      if Copy(Names[I], 1, 12) = 'RepoTracker.' then
        if RegQueryStringValue(HKCU, 'Software\Microsoft\Windows\CurrentVersion\Run', Names[I], Command) then
          if CompareText(Copy(Command, 1, Length(Prefix)), Prefix) = 0 then
            RegDeleteValue(HKCU, 'Software\Microsoft\Windows\CurrentVersion\Run', Names[I]);
end;
