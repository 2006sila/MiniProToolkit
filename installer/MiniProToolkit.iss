#ifndef AppVersion
  #define AppVersion "0.1.0"
#endif
#ifndef PayloadRoot
  #define PayloadRoot AddBackslash(SourcePath) + "..\dist\MiniProToolkit-x64-v" + AppVersion
#endif
#ifndef InstallerOutput
  #define InstallerOutput AddBackslash(SourcePath) + "..\dist\installer"
#endif

#define ProductName "MiniProToolkit"
#define StagingDir "{tmp}\MiniProToolkitPayload"

[Setup]
AppId={{8B07DA31-E0A5-4B64-A8DD-3C0B2A1DCD74}}
AppName={#ProductName}
AppVersion={#AppVersion}
; Stamp the shell version resource from the same single version source as AppVersion and the
; payload, so Explorer's File version / Product version are 0.1.0 (not the 0.0.0.0 default of
; an Inno Setup build that only sets AppVersion).
VersionInfoVersion={#AppVersion}
VersionInfoTextVersion={#AppVersion}
AppPublisher=MiniProToolkit
DefaultDirName={autopf}\MiniProToolkit
DefaultGroupName=MiniProToolkit
UsedUserAreasWarning=no
DisableProgramGroupPage=yes
PrivilegesRequired=admin
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
UninstallDisplayIcon={app}\App\MiniProToolkit.exe
OutputDir={#InstallerOutput}
OutputBaseFilename=MiniProToolkit-Setup-x64-v{#AppVersion}
Compression=lzma2/ultra64
SolidCompression=yes
WizardStyle=modern
CloseApplications=no
RestartApplications=no
Uninstallable=yes

[Tasks]
Name: "startup"; Description: "随 Windows 启动（首次安装）"; GroupDescription: "可选设置："; Flags: unchecked
Name: "desktopicon"; Description: "创建桌面快捷方式"; GroupDescription: "可选设置："; Flags: unchecked

[Files]
Source: "{#PayloadRoot}\*"; DestDir: "{#StagingDir}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{commonprograms}\MiniProToolkit"; Filename: "{app}\App\MiniProToolkit.exe"; WorkingDir: "{app}\App"
Name: "{autodesktop}\MiniProToolkit"; Filename: "{app}\App\MiniProToolkit.exe"; WorkingDir: "{app}\App"; Tasks: desktopicon

[UninstallDelete]
Type: filesandordirs; Name: "{app}\App"
Type: filesandordirs; Name: "{app}\Guardian"
Type: filesandordirs; Name: "{app}\scripts"
Type: filesandordirs; Name: "{app}\docs"
Type: files; Name: "{app}\release-manifest.json"
Type: files; Name: "{app}\install-state.json"
Type: dirifempty; Name: "{app}"

[Code]
function RunPowerShell(const ScriptPath, ExtraArguments: String; var ErrorText: String): Boolean;
var
  ResultCode: Integer;
  Params: String;
  PowerShell: String;
begin
  PowerShell := ExpandConstant('{sys}\WindowsPowerShell\v1.0\powershell.exe');
  Params := '-NoProfile -ExecutionPolicy Bypass -File "' + ScriptPath + '" ' + ExtraArguments;
  Result := Exec(PowerShell, Params, ExtractFileDir(ScriptPath), SW_HIDE, ewWaitUntilTerminated, ResultCode);
  if Result and (ResultCode <> 0) then
  begin
    Result := False;
    ErrorText := 'PowerShell 生命周期脚本退出码：' + IntToStr(ResultCode);
  end;
  if not Result and (ErrorText = '') then
    ErrorText := '无法启动 PowerShell 生命周期脚本。';
end;

procedure CurStepChanged(CurStep: TSetupStep);
var
  ScriptPath: String;
  Args: String;
  ErrorText: String;
  IsUpgrade: Boolean;
  IsRepair: Boolean;
begin
  if CurStep = ssPostInstall then
  begin
    IsUpgrade := FileExists(ExpandConstant('{app}\install-state.json'));
    IsRepair := IsUpgrade and (CompareText(ExpandConstant('{param:REPAIR|0}'), '1') = 0);
    if IsRepair then
      ScriptPath := ExpandConstant('{#StagingDir}\scripts\repair-package.ps1')
    else if IsUpgrade then
      ScriptPath := ExpandConstant('{#StagingDir}\scripts\upgrade-package.ps1')
    else
      ScriptPath := ExpandConstant('{#StagingDir}\scripts\install-package.ps1');
    Args := '-InstallRoot "' + ExpandConstant('{app}') + '" -PackageRoot "' + ExpandConstant('{#StagingDir}') + '"';
    if (not IsUpgrade) and WizardIsTaskSelected('startup') then
      Args := Args + ' -EnableStartup';
    ErrorText := '';
    if not RunPowerShell(ScriptPath, Args, ErrorText) then
      RaiseException('MiniProToolkit 安装/升级未完成。' + #13#10 + ErrorText + #13#10 + '请检查租约、Guardian 状态和安装日志后重试。');
  end;
end;

procedure DeinitializeSetup;
begin
  DelTree(ExpandConstant('{#StagingDir}'), True, True, True);
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  ScriptPath: String;
  ErrorText: String;
begin
  if CurUninstallStep = usUninstall then
  begin
    ScriptPath := ExpandConstant('{app}\scripts\uninstall-package.ps1');
    ErrorText := '';
    if not RunPowerShell(ScriptPath,
      '-InstallRoot "' + ExpandConstant('{app}') + '" -KeepInstallRoot', ErrorText) then
    begin
      MsgBox('MiniProToolkit 卸载已中止。' + #13#10 + ErrorText + #13#10 + '若存在活动租约，请先按恢复说明请求 EC 自动控制，再重新卸载。', mbError, MB_OK);
      Abort;
    end;
  end;
end;
