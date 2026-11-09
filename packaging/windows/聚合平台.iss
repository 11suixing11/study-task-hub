#ifndef PayloadDirectory
  #error PayloadDirectory is required.
#endif
#ifndef PackageVersion
  #define PackageVersion "1.1.1"
#endif
#ifndef BuildVersion
  #define BuildVersion "local"
#endif
#ifndef PackageOutput
  #define PackageOutput "."
#endif
#ifndef WebViewBootstrapper
  #error WebViewBootstrapper is required.
#endif
#ifdef TestInstallation
  #ifndef TestDataRoot
    #error TestDataRoot is required for an isolated installation.
  #endif
#endif

[Setup]
#ifdef TestInstallation
AppId={{B0BCA8EE-7AA7-4613-BEB5-ED9C6D5FF9EB}
AppName=聚合平台（隔离安装验证）
#else
AppId={{74E4712C-9858-423F-BAF3-2362E925DB7D}
AppName=聚合平台
#endif
AppVersion={#PackageVersion}
AppVerName=聚合平台 {#PackageVersion}
AppPublisher=聚合平台
DefaultDirName={localappdata}\Programs\LearningTaskAggregator
DefaultGroupName=聚合平台
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.17763
DisableProgramGroupPage=yes
DisableWelcomePage=no
UsePreviousAppDir=yes
UninstallDisplayName=聚合平台
UninstallDisplayIcon={app}\Assets\Icons\app.ico
SetupIconFile={#PayloadDirectory}\Assets\Icons\app.ico
OutputDir={#PackageOutput}
OutputBaseFilename=聚合平台-Setup-{#BuildVersion}-win-x64
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
CloseApplications=no
RestartApplications=no
#ifdef TestInstallation
SetupMutex=LearningTaskAggregator.IsolatedSetup
AppMutex=LearningTaskAggregator.IsolatedInstallation
#else
SetupMutex=LearningTaskAggregator.Setup
AppMutex=LearningTaskAggregator.Installed
#endif
Uninstallable=yes

[Languages]
Name: "chinesesimp"; MessagesFile: "compiler:Default.isl,ChineseSimplified.isl"

[Tasks]
Name: "desktopicon"; Description: "创建桌面快捷方式"; GroupDescription: "快捷方式："

[Files]
Source: "{#PayloadDirectory}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs; Excludes: "*.pdb"
Source: "{#WebViewBootstrapper}"; Flags: dontcopy

[Icons]
#ifndef TestInstallation
Name: "{userprograms}\聚合平台"; Filename: "{app}\AccessProbe.exe"; WorkingDir: "{app}"; IconFilename: "{app}\Assets\Icons\app.ico"
Name: "{userdesktop}\聚合平台"; Filename: "{app}\AccessProbe.exe"; WorkingDir: "{app}"; IconFilename: "{app}\Assets\Icons\app.ico"; Tasks: desktopicon
Name: "{group}\卸载聚合平台"; Filename: "{uninstallexe}"
#endif

[Run]
Filename: "{app}\AccessProbe.exe"; Description: "启动聚合平台"; Flags: nowait postinstall skipifsilent

[Code]
var
  ImportPage: TInputDirWizardPage;
  ImportChoice: TInputOptionWizardPage;

function DataRoot: String;
begin
#ifdef TestInstallation
  Result := '{#TestDataRoot}';
#else
  Result := ExpandConstant('{localappdata}\LearningTaskAggregator');
#endif
end;

function WebViewPresent: Boolean;
var
  Version: String;
  ClientKey: String;
begin
  ClientKey := 'Software\Microsoft\EdgeUpdate\Clients\{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}';
  Result := (RegQueryStringValue(HKCU, ClientKey, 'pv', Version) and (Version <> '') and (Version <> '0.0.0.0'))
    or (RegQueryStringValue(HKLM32, ClientKey, 'pv', Version) and (Version <> '') and (Version <> '0.0.0.0'))
    or (RegQueryStringValue(HKLM64, ClientKey, 'pv', Version) and (Version <> '') and (Version <> '0.0.0.0'));
end;

procedure InitializeWizard;
begin
  ImportChoice := CreateInputOptionPage(wpSelectDir, '保留已有记录', '选择安装版的数据来源',
    '任务、课程选择和 Cookie 保存在当前 Windows 用户的数据目录。升级与卸载会保留这些记录。', True, False);
  ImportChoice.Add('首次安装，创建新的任务记录');
  ImportChoice.Add('导入这台电脑上的旧版聚合平台记录');
  ImportChoice.SelectedValueIndex := 0;
  ImportPage := CreateInputDirPage(ImportChoice.ID, '导入旧版记录', '选择旧版聚合平台文件夹',
    '先在旧版首页点击“退出程序”。选择包含 .local\data\tasks.db 的项目文件夹；只复制任务库和加密 Cookie，旧文件保留。', False, '');
  ImportPage.Add('旧版文件夹：');
  ImportPage.Values[0] := ExpandConstant('{userdesktop}\聚合平台');
end;

function ShouldSkipPage(PageID: Integer): Boolean;
begin
  Result := ((PageID = ImportChoice.ID) or (PageID = ImportPage.ID)) and FileExists(DataRoot + '\.local\data\tasks.db');
  if PageID = ImportPage.ID then
    Result := Result or (ImportChoice.SelectedValueIndex <> 1);
end;

function NextButtonClick(CurPageID: Integer): Boolean;
begin
  Result := True;
  if (CurPageID = ImportPage.ID) and not FileExists(ImportPage.Values[0] + '\.local\data\tasks.db') then
  begin
    MsgBox('所选文件夹中没有 .local\data\tasks.db，请选择旧版聚合平台项目文件夹。', mbError, MB_OK);
    Result := False;
  end;
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
var
  ExitCode: Integer;
begin
  Result := '';
#ifndef TestInstallation
  if CheckForMutexes('LearningTaskAggregator.Installed') then
  begin
    Result := '聚合平台正在运行。请在首页点击“退出程序”，再继续安装；右上角 × 只会隐藏到托盘。';
    Exit;
  end;
#endif
  if not WebViewPresent then
  begin
#ifdef TestInstallation
    Result := '隔离安装验证需要已安装的 WebView2，不会修改系统运行环境。';
#else
    ExtractTemporaryFile('MicrosoftEdgeWebview2Setup.exe');
    if not Exec(ExpandConstant('{tmp}\MicrosoftEdgeWebview2Setup.exe'), '/silent /install', '', SW_HIDE, ewWaitUntilTerminated, ExitCode) then
      Result := '无法启动 Microsoft WebView2 安装。请联网后重试。'
    else if (ExitCode <> 0) and (ExitCode <> 3010) then
      Result := 'Microsoft WebView2 安装未完成。请检查网络后重试，错误码：' + IntToStr(ExitCode);
    if ExitCode = 3010 then NeedsRestart := True;
#endif
  end;
end;

procedure CurStepChanged(CurStep: TSetupStep);
var
  SourceRoot: String;
  ExitCode: Integer;
begin
  if CurStep = ssPostInstall then
  begin
    SourceRoot := ExpandConstant('{param:IMPORTPROJECT|}');
    if (SourceRoot = '') and (ImportChoice.SelectedValueIndex = 1) then SourceRoot := ImportPage.Values[0];
    if (SourceRoot <> '') and not FileExists(DataRoot + '\.local\data\tasks.db') then
    begin
      if not Exec(ExpandConstant('{app}\AccessProbe.exe'), '--import-project "' + SourceRoot + '" --data-root "' + DataRoot + '"', '', SW_HIDE, ewWaitUntilTerminated, ExitCode)
        or (ExitCode <> 0) then
        MsgBox('软件已安装，但旧版记录导入未完成。旧文件已保留；请退出旧版后重新运行安装包并选择导入。', mbError, MB_OK);
    end;
  end;
end;
