; AppLock 应用锁 安装包脚本（Inno Setup 6）
; 由 build-installer.ps1 调用；也可直接在 Inno Setup 里打开编译。
; 通过 /D 传入：AppVersion、SourceDir（publish 目录）、OutputDir

#ifndef AppVersion
  #define AppVersion "1.0.0"
#endif
#ifndef SourceDir
  #define SourceDir "..\publish"
#endif
#ifndef OutputDir
  #define OutputDir "..\dist"
#endif

#define AppName "应用锁"
#define AppNameEn "AppLock"
#define AppPublisher "ZhaoCongLin"
#define AgentExe "AppLock.exe"
#define ServiceExe "AppLock.Service.exe"
#define ServiceName "AppLockService"

[Setup]
AppId={{7E1C6E2B-3F7A-4C4E-9B7B-2A6D5F1C0E93}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}
AppPublisher={#AppPublisher}
DefaultDirName={autopf}\{#AppNameEn}
DefaultGroupName={#AppName}
UninstallDisplayName={#AppName}
UninstallDisplayIcon={app}\{#AgentExe}
OutputDir={#OutputDir}
OutputBaseFilename={#AppNameEn}-Setup-{#AppVersion}
SetupIconFile=..\Lock\Assets\applock.ico
Compression=lzma2/ultra64
SolidCompression=yes
WizardStyle=modern
WizardSizePercent=110
; 服务 + 右键菜单 + Program Files 都需要管理员
PrivilegesRequired=admin
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.17763
DisableProgramGroupPage=yes
DisableWelcomePage=no
CloseApplications=no
; 升级安装时不弹目录选择页
UsePreviousAppDir=yes
VersionInfoVersion={#AppVersion}
VersionInfoDescription={#AppName} 安装程序
VersionInfoProductName={#AppName}

[Languages]
Name: "chinesesimplified"; MessagesFile: "ChineseSimplified.isl"
Name: "english"; MessagesFile: "compiler:Default.isl"

[CustomMessages]
chinesesimplified.Tagline=为程序和文件夹加上密码
chinesesimplified.CreateDesktopIcon=创建桌面快捷方式
chinesesimplified.AutoStart=登录时自动启动托盘程序（推荐）
chinesesimplified.LaunchNow=立即启动 {#AppName}
chinesesimplified.NeedDotNet=未检测到 .NET 10 桌面运行时。%n%n安装程序可以为你打开下载页面，安装完运行时后请重新运行本安装程序。%n%n是否现在打开下载页面？
chinesesimplified.Installing=正在注册后台服务…
chinesesimplified.UninstallRestoring=正在停止服务并恢复被锁文件夹的权限…
chinesesimplified.KeepData=是否保留应用锁的密码、被锁程序列表和日志？%n%n选“是”保留（重新安装后可直接使用），选“否”彻底删除。
chinesesimplified.FoldersRestoreFailed=部分文件夹权限恢复失败，请查看 C:\ProgramData\AppLock\service.log。%n可用管理员 PowerShell 手动恢复：takeown /F "路径" /R /D Y 然后 icacls "路径" /reset /T
english.Tagline=Password-protect apps and folders
english.CreateDesktopIcon=Create a desktop shortcut
english.AutoStart=Start tray app at logon (recommended)
english.LaunchNow=Launch {#AppNameEn} now
english.NeedDotNet=.NET 10 Desktop Runtime was not found.%n%nSetup can open the download page for you. After installing the runtime, run this setup again.%n%nOpen the download page now?
english.Installing=Registering background service…
english.UninstallRestoring=Stopping service and restoring folder permissions…
english.KeepData=Keep AppLock's password, locked-app list and logs?%n%nYes = keep (usable after reinstall), No = delete everything.
english.FoldersRestoreFailed=Some folder permissions could not be restored, see C:\ProgramData\AppLock\service.log.

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"

[Files]
Source: "{#SourceDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\{#AppName}"; Filename: "{app}\{#AgentExe}"
Name: "{group}\卸载 {#AppName}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#AgentExe}"; Tasks: desktopicon

[Run]
; 安装/升级：注册服务 + 计划任务 + 右键菜单并启动服务（服务程序自带 install 子命令）
Filename: "{app}\{#ServiceExe}"; Parameters: "install ""{app}\{#AgentExe}"""; StatusMsg: "{cm:Installing}"; Flags: runhidden waituntilterminated
; 安装完成页勾选后启动托盘（以普通用户身份，而不是安装程序的管理员身份）
Filename: "{app}\{#AgentExe}"; Description: "{cm:LaunchNow}"; Flags: postinstall nowait skipifsilent runasoriginaluser

[UninstallRun]
; 先让服务自己收尾：停服务、删计划任务/右键菜单、恢复所有被锁文件夹权限
Filename: "{app}\{#ServiceExe}"; Parameters: "uninstall"; RunOnceId: "svc-uninstall"; Flags: runhidden waituntilterminated

[Code]
const
  DotNetDownloadUrl = 'https://dotnet.microsoft.com/download/dotnet/10.0';

// ---- .NET 桌面运行时检测 ----
function FindFirstDir(const Pattern: string): Boolean;
var
  FindRec: TFindRec;
begin
  Result := FindFirst(Pattern, FindRec);
  if Result then FindClose(FindRec);
end;

function DotNetDesktopRuntimeInstalled: Boolean;
var
  Names: TArrayOfString;
  I: Integer;
begin
  Result := False;
  // 共享运行时安装后会在这里登记每个版本
  if RegGetSubkeyNames(HKLM, 'SOFTWARE\dotnet\Setup\InstalledVersions\x64\sharedfx\Microsoft.WindowsDesktop.App', Names) then
    for I := 0 to GetArrayLength(Names) - 1 do
      if Copy(Names[I], 1, 3) = '10.' then
      begin
        Result := True;
        Exit;
      end;
  // 兜底：直接看目录
  if DirExists(ExpandConstant('{commonpf}\dotnet\shared\Microsoft.WindowsDesktop.App')) then
    if FindFirstDir(ExpandConstant('{commonpf}\dotnet\shared\Microsoft.WindowsDesktop.App\10.*')) then
      Result := True;
end;

function InitializeSetup: Boolean;
var
  ErrCode: Integer;
begin
  Result := True;
  if not DotNetDesktopRuntimeInstalled then
  begin
    if MsgBox(CustomMessage('NeedDotNet'), mbConfirmation, MB_YESNO) = IDYES then
      ShellExec('open', DotNetDownloadUrl, '', '', SW_SHOWNORMAL, ewNoWait, ErrCode);
    Result := False;
  end;
end;

// ---- 安装前：停旧服务、关托盘，否则文件被占用 ----
procedure StopExisting;
var
  ResultCode: Integer;
begin
  Exec(ExpandConstant('{sys}\sc.exe'), 'stop {#ServiceName}', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
  Exec(ExpandConstant('{sys}\taskkill.exe'), '/IM {#AgentExe} /F', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
  // 等服务真正停下
  Sleep(1500);
end;

procedure CurStepChanged(CurStep: TSetupStep);
begin
  if CurStep = ssInstall then
    StopExisting;
end;

// ---- 卸载 ----
function InitializeUninstall: Boolean;
var
  ResultCode: Integer;
begin
  Result := True;
  Exec(ExpandConstant('{sys}\taskkill.exe'), '/IM {#AgentExe} /F', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  DataDir: string;
begin
  if CurUninstallStep = usPostUninstall then
  begin
    DataDir := ExpandConstant('{commonappdata}\AppLock');
    if DirExists(DataDir) then
      if MsgBox(CustomMessage('KeepData'), mbConfirmation, MB_YESNO) = IDNO then
        DelTree(DataDir, True, True, True);
  end;
end;
