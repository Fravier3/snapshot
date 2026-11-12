; Instalador de Fran Capture Studio (Inno Setup 6)
; Compilar:  iscc /DAppVersion=1.0.0 /DSourceDir=..\publish installer\FranCaptureStudio.iss
#ifndef AppVersion
  #define AppVersion "1.0.0"
#endif
#ifndef SourceDir
  #define SourceDir "..\publish"
#endif
#define AppName "Fran Capture Studio"
#define AppExe "FranCaptureStudio.exe"
#define AppPublisher "Desarrollos Francisco J Vazquez Santiago"

[Setup]
AppId={{6B0E2D5C-4C1B-4F52-9E3A-FCS0CAPTURE01}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}
AppPublisher={#AppPublisher}
VersionInfoVersion={#AppVersion}.0
VersionInfoCompany={#AppPublisher}
VersionInfoDescription=Instalador de {#AppName}
VersionInfoProductName={#AppName}
DefaultDirName={autopf}\{#AppName}
DefaultGroupName={#AppName}
DisableProgramGroupPage=yes
; Instalación por usuario: no pide privilegios de administrador (se puede elegir «para todos» en el diálogo).
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=dialog
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.19041
OutputDir=..\artifacts
OutputBaseFilename=FranCaptureStudio-Setup
SetupIconFile=..\src\FranCaptureStudio\Assets\app.ico
UninstallDisplayIcon={app}\{#AppExe}
UninstallDisplayName={#AppName}
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
CloseApplications=yes
RestartApplications=no
LicenseFile=..\LICENSE.txt
ShowLanguageDialog=auto
UsePreviousAppDir=yes
UsePreviousTasks=yes

[Languages]
Name: "spanish"; MessagesFile: "compiler:Languages\Spanish.isl"
Name: "english"; MessagesFile: "compiler:Default.isl"

[CustomMessages]
spanish.DesktopIcon=Crear un acceso directo en el escritorio
english.DesktopIcon=Create a desktop shortcut
spanish.StartupTask=Iniciar {#AppName} con Windows (minimizado en la bandeja)
english.StartupTask=Start {#AppName} with Windows (minimized to tray)
spanish.Extras=Opciones adicionales:
english.Extras=Additional options:
spanish.LaunchApp=Abrir {#AppName}
english.LaunchApp=Launch {#AppName}
spanish.RemoveData=¿Quieres eliminar también la configuración y el historial de la biblioteca de {#AppName}?%n%nTus capturas, videos y documentos NO se eliminarán en ningún caso.
english.RemoveData=Do you also want to remove {#AppName} settings and library history?%n%nYour captures, videos and documents will NOT be deleted in any case.

[Tasks]
Name: "desktopicon"; Description: "{cm:DesktopIcon}"; GroupDescription: "{cm:Extras}"; Flags: unchecked
Name: "startup"; Description: "{cm:StartupTask}"; GroupDescription: "{cm:Extras}"; Flags: unchecked

[Files]
Source: "{#SourceDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{autoprograms}\{#AppName}"; Filename: "{app}\{#AppExe}"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#AppExe}"; Tasks: desktopicon

[Registry]
; Inicio con Windows: solo si el usuario marca la tarea (desmarcada por defecto). Mismo valor que usa la aplicación.
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: string; ValueName: "FranCaptureStudio"; ValueData: """{app}\{#AppExe}"" --minimized"; Tasks: startup; Flags: uninsdeletevalue

[Run]
Filename: "{app}\{#AppExe}"; Description: "{cm:LaunchApp}"; Flags: nowait postinstall skipifsilent

[UninstallRun]
; Cierra la instancia en ejecución (libera atajos e icono de bandeja) antes de borrar archivos.
Filename: "{app}\{#AppExe}"; Parameters: "--exit"; Flags: runhidden skipifdoesntexist; RunOnceId: "CloseApp"

[UninstallDelete]
Type: filesandordirs; Name: "{localappdata}\FranCaptureStudio\temp"
Type: filesandordirs; Name: "{localappdata}\FranCaptureStudio\thumbs"

[Code]
procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
begin
  if CurUninstallStep = usUninstall then
    Sleep(1500);
  if CurUninstallStep = usPostUninstall then
  begin
    { La configuración se conserva salvo que el usuario pida eliminarla. Nunca se tocan Imágenes ni Videos. }
    if not UninstallSilent then
      if MsgBox(ExpandConstant('{cm:RemoveData}'), mbConfirmation, MB_YESNO or MB_DEFBUTTON2) = IDYES then
      begin
        DelTree(ExpandConstant('{userappdata}\FranCaptureStudio'), True, True, True);
        DelTree(ExpandConstant('{localappdata}\FranCaptureStudio'), True, True, True);
        RegDeleteValue(HKEY_CURRENT_USER, 'Software\Microsoft\Windows\CurrentVersion\Run', 'FranCaptureStudio');
      end;
  end;
end;
