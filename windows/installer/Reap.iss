; Inno Setup 6 安装脚本。由 scripts/publish.ps1 调用，参数通过 /D 传入。
#ifndef AppVersion
  #define AppVersion "0.1.0"
#endif
#ifndef Arch
  #define Arch "x64"
#endif
#ifndef SourceDir
  #define SourceDir "..\dist\Reap-x64"
#endif

[Setup]
AppId={{6B1F6C3E-7A44-4C55-9D2B-1C0E5A8F2D11}
AppName=Reap
AppVersion={#AppVersion}
AppPublisher=Reap
DefaultDirName={localappdata}\Programs\Reap
DefaultGroupName=Reap
DisableProgramGroupPage=yes
; 装在用户目录下，不需要管理员权限。
PrivilegesRequired=lowest
OutputBaseFilename=Reap-Setup-{#AppVersion}-{#Arch}
SetupIconFile=..\src\Reap.App\Assets\Reap.ico
UninstallDisplayIcon={app}\Reap.exe
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
#if Arch == "arm64"
ArchitecturesAllowed=arm64
ArchitecturesInstallIn64BitMode=arm64
#else
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
#endif
CloseApplications=yes
RestartApplications=no

[Languages]
; Inno Setup 官方语言包里没有简体中文，安装向导的标准按钮用英文，下面自定义的选项用中文。
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "startup"; Description: "登录 Windows 时自动启动（只在托盘运行）"; Flags: unchecked
Name: "desktopicon"; Description: "创建桌面快捷方式"; Flags: unchecked

[Files]
Source: "{#SourceDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\Reap"; Filename: "{app}\Reap.exe"
Name: "{autodesktop}\Reap"; Filename: "{app}\Reap.exe"; Tasks: desktopicon

[Registry]
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: string; ValueName: "Reap"; \
  ValueData: """{app}\Reap.exe"" --background"; Flags: uninsdeletevalue; Tasks: startup

[Run]
Filename: "{app}\Reap.exe"; Description: "启动 Reap"; Flags: nowait postinstall skipifsilent

[UninstallRun]
Filename: "{cmd}"; Parameters: "/C taskkill /IM Reap.exe /F"; Flags: runhidden; RunOnceId: "StopReap"

[UninstallDelete]
Type: filesandordirs; Name: "{localappdata}\Reap"
