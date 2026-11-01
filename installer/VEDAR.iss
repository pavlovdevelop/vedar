; VEDAR — Твоят гласов приятел
; Inno Setup 6 script. Build with build/create-installer.ps1 (or: ISCC.exe /DSourceDir=... VEDAR.iss).

#ifndef AppVersion
  #define AppVersion "0.1.0"
#endif
#ifndef SourceDir
  #define SourceDir "..\artifacts\publish\win-x64"
#endif
#ifndef OutputDir
  #define OutputDir "..\dist"
#endif
#define AppName "VEDAR"
#define AppExe "VEDAR.exe"
#define AppPublisher "VEDAR Project"
#define AppUrl "https://github.com/pavlovdevelop/vedar"

[Setup]
; Never change the AppId: it identifies VEDAR for upgrades and uninstall.
AppId={{8F0E9A2C-5B1D-4E7A-9C3F-2D6B7A1E4F90}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}
AppPublisher={#AppPublisher}
AppPublisherURL={#AppUrl}
AppSupportURL={#AppUrl}
AppUpdatesURL={#AppUrl}
AppComments=VEDAR — Твоят гласов приятел. Безплатен гласов асистент за незрящи и слабовиждащи хора.
VersionInfoVersion={#AppVersion}.0
VersionInfoProductName={#AppName}
VersionInfoDescription=VEDAR — инсталатор
VersionInfoCompany={#AppPublisher}
; Per-user installation by default: no administrator rights needed. "All users" can be chosen in a dialog.
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=dialog commandline
DefaultDirName={autopf}\{#AppName}
DefaultGroupName={#AppName}
DisableProgramGroupPage=yes
DisableWelcomePage=no
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.17763
OutputDir={#OutputDir}
OutputBaseFilename=VEDAR-Setup-v{#AppVersion}-x64
SetupIconFile=..\assets\branding\vedar.ico
UninstallDisplayIcon={app}\{#AppExe}
UninstallDisplayName={#AppName} {#AppVersion}
WizardStyle=modern
WizardSizePercent=120
WizardImageFile=..\assets\branding\wizard-image.bmp,..\assets\branding\wizard-image-2x.bmp
WizardSmallImageFile=..\assets\branding\wizard-small.bmp,..\assets\branding\wizard-small-2x.bmp
InfoBeforeFile=info-before-bg.txt
Compression=lzma2/ultra64
SolidCompression=yes
LZMAUseSeparateProcess=yes
CloseApplications=yes
RestartApplications=no
SetupLogging=yes
ShowLanguageDialog=auto
UsePreviousAppDir=yes
UsePreviousTasks=yes

[Languages]
Name: "bg"; MessagesFile: "compiler:Languages\Bulgarian.isl"
Name: "en"; MessagesFile: "compiler:Default.isl"

[CustomMessages]
bg.DesktopIcon=Икона на работния плот
bg.AutoStart=Стартирай VEDAR автоматично при влизане в Windows (препоръчително)
bg.Startup=Стартиране:
bg.Shortcuts=Преки пътища:
bg.LaunchVedar=Стартирай VEDAR сега
bg.UserGuide=Ръководство на потребителя
bg.Uninstall=Деинсталиране на VEDAR
bg.ModelCheckFailed=Проверката на речевите модели не успя за файла:%n%1%n%nИнсталирайте VEDAR отново.
bg.DeleteUserData=Искате ли да изтриете и личните данни на VEDAR?%n%nТова са настройките, защитеният ключ за BgGPT, историята на разговорите (ако е била включена) и дневниците.%n%nИзберете „Не“, ако ще инсталирате VEDAR отново и искате да запазите настройките.
en.DesktopIcon=Desktop icon
en.AutoStart=Start VEDAR automatically when signing in to Windows (recommended)
en.Startup=Startup:
en.Shortcuts=Shortcuts:
en.LaunchVedar=Start VEDAR now
en.UserGuide=User guide (Bulgarian)
en.Uninstall=Uninstall VEDAR
en.ModelCheckFailed=The speech model check failed for:%n%1%n%nPlease reinstall VEDAR.
en.DeleteUserData=Do you also want to delete VEDAR's personal data (settings, protected BgGPT key, conversation history and logs)?

[Tasks]
Name: "autostart"; Description: "{cm:AutoStart}"; GroupDescription: "{cm:Startup}"
Name: "desktopicon"; Description: "{cm:DesktopIcon}"; GroupDescription: "{cm:Shortcuts}"

[Files]
Source: "{#SourceDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
; Ctrl+Alt+V opens VEDAR from anywhere (Windows shortcut hotkey).
Name: "{autoprograms}\{#AppName}"; Filename: "{app}\{#AppExe}"; WorkingDir: "{app}"; HotKey: ctrl+alt+v; Comment: "VEDAR — Твоят гласов приятел (Ctrl+Alt+V)"
Name: "{autoprograms}\{#AppName} — {cm:UserGuide}"; Filename: "{app}\docs\USER_GUIDE_BG.txt"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#AppExe}"; WorkingDir: "{app}"; Tasks: desktopicon; Comment: "VEDAR — Твоят гласов приятел"

[Registry]
; Autostart in the context of the signed-in user (HKCU), without administrator rights.
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: string; ValueName: "VEDAR"; ValueData: """{app}\{#AppExe}"" --background"; Flags: uninsdeletevalue; Tasks: autostart

[Run]
Filename: "{app}\{#AppExe}"; Description: "{cm:LaunchVedar}"; Flags: nowait postinstall skipifsilent

[Code]
const
  RunKey = 'Software\Microsoft\Windows\CurrentVersion\Run';

// Ask a running VEDAR to exit before files are replaced (it normally hides to the tray when closed).
function PrepareToInstall(var NeedsRestart: Boolean): String;
var
  ResultCode: Integer;
  Exe: String;
begin
  Result := '';
  Exe := ExpandConstant('{app}\{#AppExe}');
  if FileExists(Exe) then
  begin
    Exec(Exe, '--exit', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
    Sleep(2500);
  end;
end;

// Verifies the bundled speech models against models\SHA256SUMS.txt written by the build.
procedure VerifyModels();
var
  Lines: TArrayOfString;
  I, P: Integer;
  Line, Expected, RelPath, FullPath, Actual: String;
begin
  if not LoadStringsFromFile(ExpandConstant('{app}\models\SHA256SUMS.txt'), Lines) then
    Exit;
  for I := 0 to GetArrayLength(Lines) - 1 do
  begin
    Line := Trim(Lines[I]);
    P := Pos('  ', Line);
    if (Line = '') or (P = 0) then
      Continue;
    Expected := Lowercase(Copy(Line, 1, P - 1));
    RelPath := Copy(Line, P + 2, Length(Line));
    StringChangeEx(RelPath, '/', '\', True);
    FullPath := ExpandConstant('{app}\models\') + RelPath;
    Actual := '';
    if FileExists(FullPath) then
      Actual := Lowercase(GetSHA256OfFile(FullPath));
    if Actual <> Expected then
    begin
      SuppressibleMsgBox(FmtMessage(CustomMessage('ModelCheckFailed'), [RelPath]), mbCriticalError, MB_OK, IDOK);
      Exit;
    end;
  end;
  Log('All bundled speech models verified.');
end;

procedure CurStepChanged(CurStep: TSetupStep);
begin
  if CurStep = ssPostInstall then
    VerifyModels();
end;

// Personal data is kept on upgrade and only deleted on uninstall if the user explicitly agrees.
procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  DataDir: String;
begin
  if CurUninstallStep = usUninstall then
  begin
    // The app itself may have created the autostart entry; never leave it dangling.
    RegDeleteValue(HKCU, RunKey, 'VEDAR');
  end;
  if CurUninstallStep = usPostUninstall then
  begin
    DataDir := ExpandConstant('{localappdata}\VEDAR');
    if DirExists(DataDir) and not UninstallSilent() then
    begin
      if SuppressibleMsgBox(CustomMessage('DeleteUserData'), mbConfirmation, MB_YESNO or MB_DEFBUTTON2, IDNO) = IDYES then
        DelTree(DataDir, True, True, True);
    end;
  end;
end;
