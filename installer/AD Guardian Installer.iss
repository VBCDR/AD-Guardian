#define MyAppName "AD Guardian"
#define MyAppExeName "Domain Guardian.exe"
#define MyAppPublisher "AD Guardian"
#define MyAppURL "https://github.com/VBCDR/AD-Guardian"

[Setup]
AppId=ADGuardian
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppVerName={#MyAppName} {#MyAppVersion}
AppPublisher={#MyAppPublisher}
AppPublisherURL={#MyAppURL}
AppSupportURL={#MyAppURL}
AppUpdatesURL={#MyAppURL}
DefaultDirName={autopf}\AD Guardian
DefaultGroupName=AD Guardian
AllowNoIcons=yes
OutputDir={#InstallerOutputDir}
OutputBaseFilename=AD Guardian Installer
SetupIconFile={#SourcePayloadDir}\AD Guardian logo.ico
WizardStyle=modern
WizardImageFile=wizard-image.png
WizardSmallImageFile=wizard-small.png
WizardImageStretch=no
WizardImageBackColor=clWhite
Compression=lzma2/ultra64
SolidCompression=yes
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
PrivilegesRequired=admin
PrivilegesRequiredOverridesAllowed=dialog
UninstallDisplayIcon={app}\{#MyAppExeName}
DisableProgramGroupPage=no
UsePreviousAppDir=no
DisableWelcomePage=no
DisableDirPage=no
DisableReadyMemo=no
CloseApplications=yes
RestartApplications=no

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Files]
Source: "{#SourcePayloadDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"
Name: "{group}\Uninstall {#MyAppName}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#MyAppExeName}"; Parameters: "-initialize-state"; Flags: runhidden waituntilterminated
Filename: "{app}\{#MyAppExeName}"; Description: "Launch {#MyAppName} now"; Flags: postinstall skipifsilent shellexec unchecked
