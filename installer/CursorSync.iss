#define MyAppName "CursorSync"
#ifndef MyAppVersion
  #define MyAppVersion "1.0.0"
#endif
#define MyAppPublisher "CursorSync"
#define MyAppExeName "CursorSync.exe"

[Setup]
AppId={{8F3C2A91-6B47-4E1D-9A55-2C8E7D14B6F0}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppVerName={#MyAppName} {#MyAppVersion}
AppPublisher={#MyAppPublisher}
AppCopyright=Copyright (C) 2026 {#MyAppPublisher}
VersionInfoVersion={#MyAppVersion}
VersionInfoProductName={#MyAppName}
DefaultDirName={autopf}\{#MyAppName}
DefaultGroupName={#MyAppName}
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=dialog
UsePreviousPrivileges=yes
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0
OutputDir=..\artifacts\installer
OutputBaseFilename=CursorSync-Setup-{#MyAppVersion}
SetupIconFile=..\src\CursorSync\Assets\app.ico
UninstallDisplayIcon={app}\{#MyAppExeName}
UninstallDisplayName={#MyAppName}
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
CloseApplications=yes
RestartApplications=no
UsedUserAreasWarning=no
AllowNoIcons=yes
ChangesEnvironment=no

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"
Name: "german"; MessagesFile: "compiler:Languages\German.isl"

[Messages]
english.PrivilegesRequiredOverrideTitle=Install CursorSync
english.PrivilegesRequiredOverrideInstruction=Choose where CursorSync should be installed
english.PrivilegesRequiredOverrideText1=This installer can put CursorSync in your user profile or on this machine for every account.
english.PrivilegesRequiredOverrideAllUsers=Install for all users on this computer (requires administrator)
english.PrivilegesRequiredOverrideCurrentUser=Install only for me (no administrator required)
german.PrivilegesRequiredOverrideTitle=CursorSync installieren
german.PrivilegesRequiredOverrideInstruction=Wählen Sie den Installationsbereich
german.PrivilegesRequiredOverrideText1=CursorSync kann nur für Ihr Benutzerkonto oder für alle Benutzer dieses PCs installiert werden.
german.PrivilegesRequiredOverrideAllUsers=Für alle Benutzer installieren (Administrator erforderlich)
german.PrivilegesRequiredOverrideCurrentUser=Nur für mich installieren (kein Administrator erforderlich)

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Files]
Source: "..\artifacts\publish\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs; Excludes: "createdump.exe,*.pdb"

[Icons]
Name: "{autoprograms}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "{cm:LaunchProgram,{#StringChange(MyAppName, '&', '&&')}}"; Flags: nowait postinstall skipifsilent

[Code]
function InitializeUninstall(): Boolean;
var
  ResultCode: Integer;
begin
  Result := True;
  Exec('taskkill.exe', '/F /IM CursorSync.exe', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
end;
