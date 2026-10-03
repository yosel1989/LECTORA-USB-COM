; Instalador de Lector USB (SerialBridge) (Inno Setup 6). Se compila con .\build.ps1
; Instala el ejecutable, registra el servicio de Windows con inicio y reinicio automáticos,
; pregunta el puerto del WebSocket y deja el icono del lector de códigos en la barra de tareas de cada usuario.

#ifndef AppVersion
  #define AppVersion "1.0.0"
#endif
#ifndef SourceDir
  #define SourceDir "..\output\publish"
#endif
; Nombre visible para el usuario. Los identificadores internos (servicio, .exe, carpetas y AppId)
; se mantienen como "SerialBridge" para que las actualizaciones sobre instalaciones previas funcionen.
#define AppName "Lector USB"
#define ServiceName "SerialBridge"
#define DefaultPort "21818"
#define AppPublisher "Yosel Edwin Aguirre Balbin (+51952387795)"

[Setup]
AppId={{7B3E5C2A-9D41-4F6B-8A2E-3C5D7E9F1A24}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}
AppPublisher={#AppPublisher}
AppContact=+51952387795
VersionInfoCompany={#AppPublisher}
VersionInfoCopyright=© {#AppPublisher}
VersionInfoDescription=Instalador de {#AppName}
VersionInfoProductName={#AppName}
UninstallDisplayName={#AppName}
UninstallDisplayIcon={app}\SerialBridge.exe
DefaultDirName={autopf}\SerialBridge
DisableProgramGroupPage=yes
PrivilegesRequired=admin
; Un solo instalador para Windows de 64 y 32 bits: en 64 bits instala la versión x64 en
; "Program Files"; en 32 bits instala la versión x86.
ArchitecturesAllowed=x86compatible x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0
CloseApplications=no
OutputDir=..\output
OutputBaseFilename=LectorUSB-Setup-{#AppVersion}
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
SetupIconFile=..\assets\scanner.ico

[Languages]
Name: "es"; MessagesFile: "compiler:Languages\Spanish.isl"

[Files]
Source: "{#SourceDir}\x64\SerialBridge.exe"; DestDir: "{app}"; Flags: ignoreversion; Check: Is64BitInstallMode
Source: "{#SourceDir}\x86\SerialBridge.exe"; DestDir: "{app}"; Flags: ignoreversion; Check: not Is64BitInstallMode
Source: "{#SourceDir}\x64\appsettings.json"; DestDir: "{app}"; Flags: ignoreversion

[Registry]
; Icono de la barra de tareas: se inicia con la sesión de cualquier usuario del equipo.
Root: HKLM; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: string; ValueName: "SerialBridgeTray"; ValueData: """{app}\SerialBridge.exe"" --tray"; Flags: uninsdeletevalue

[Run]
Filename: "{app}\SerialBridge.exe"; Parameters: "--tray"; Flags: nowait runasoriginaluser
Filename: "http://127.0.0.1:{code:GetPort}/"; Description: "Abrir la página de configuración"; Flags: postinstall shellexec nowait skipifsilent

[UninstallRun]
Filename: "{sys}\net.exe"; Parameters: "stop {#ServiceName}"; Flags: runhidden waituntilterminated; RunOnceId: "StopService"
Filename: "{sys}\taskkill.exe"; Parameters: "/F /IM SerialBridge.exe"; Flags: runhidden waituntilterminated; RunOnceId: "CloseTray"
Filename: "{sys}\sc.exe"; Parameters: "delete {#ServiceName}"; Flags: runhidden waituntilterminated; RunOnceId: "DeleteService"

[Code]
var
  PortPage: TInputQueryWizardPage;

function SettingsDir: String;
begin
  Result := ExpandConstant('{commonappdata}\SerialBridge');
end;

{ Lee "webSocketPort" de una instalación anterior para proponerlo por defecto. }
function ReadExistingPort: String;
var
  Json: AnsiString;
  S: String;
  I, Start: Integer;
begin
  Result := '';
  if not LoadStringFromFile(SettingsDir + '\settings.json', Json) then
    Exit;
  S := String(Json);
  I := Pos('"webSocketPort"', S);
  if I = 0 then
    Exit;
  I := I + Length('"webSocketPort"');
  while (I <= Length(S)) and ((S[I] = ' ') or (S[I] = ':') or (S[I] = #9)) do
    I := I + 1;
  Start := I;
  while (I <= Length(S)) and (S[I] >= '0') and (S[I] <= '9') do
    I := I + 1;
  Result := Copy(S, Start, I - Start);
end;

function GetPort(Param: String): String;
begin
  Result := IntToStr(StrToIntDef(Trim(PortPage.Values[0]), {#DefaultPort}));
end;

procedure InitializeWizard;
var
  Existing: String;
begin
  PortPage := CreateInputQueryPage(wpSelectDir,
    'Puerto del WebSocket',
    'Puerto por el que las aplicaciones recibirán las lecturas.',
    'La aplicación web se conectará a ws://127.0.0.1:<puerto>. Use el mismo puerto en todos ' +
    'los equipos y el mismo que tenga configurado la aplicación. Si no está seguro, deje el valor sugerido.');
  PortPage.Add('Puerto:', False);

  Existing := ReadExistingPort;
  if Existing <> '' then
    PortPage.Values[0] := Existing
  else
    PortPage.Values[0] := '{#DefaultPort}';
end;

function NextButtonClick(CurPageID: Integer): Boolean;
var
  Port: Integer;
begin
  Result := True;
  if CurPageID = PortPage.ID then
  begin
    Port := StrToIntDef(Trim(PortPage.Values[0]), -1);
    if (Port < 1) or (Port > 65535) then
    begin
      MsgBox('Ingrese un número de puerto entre 1 y 65535.', mbError, MB_OK);
      Result := False;
    end;
  end;
end;

procedure RunHidden(const FileName, Params: String);
var
  ResultCode: Integer;
begin
  if Exec(FileName, Params, '', SW_HIDE, ewWaitUntilTerminated, ResultCode) then
    Log(Format('%s %s -> %d', [FileName, Params, ResultCode]))
  else
    Log('No se pudo ejecutar ' + FileName);
end;

{ En una actualización el servicio y los iconos de la barra de tareas tienen el .exe en uso:
  se cierran antes de copiar (el icono vuelve a abrirse al final de la instalación). }
function PrepareToInstall(var NeedsRestart: Boolean): String;
begin
  RunHidden(ExpandConstant('{sys}\net.exe'), 'stop {#ServiceName}');
  RunHidden(ExpandConstant('{sys}\taskkill.exe'), '/F /IM SerialBridge.exe');
  Result := '';
end;

procedure CurStepChanged(CurStep: TSetupStep);
var
  Exe, Sc, BinPath: String;
begin
  if CurStep <> ssPostInstall then
    Exit;

  Exe := ExpandConstant('{app}\SerialBridge.exe');
  Sc := ExpandConstant('{sys}\sc.exe');
  BinPath := 'binPath= "\"' + Exe + '\""';

  RunHidden(Exe, '--set-port ' + GetPort(''));
  { "create" falla si ya existe (actualización); "config" deja la ruta correcta en ambos casos. }
  RunHidden(Sc, 'create {#ServiceName} ' + BinPath + ' start= auto DisplayName= "{#AppName}"');
  RunHidden(Sc, 'config {#ServiceName} ' + BinPath + ' start= auto DisplayName= "{#AppName}"');
  RunHidden(Sc, 'description {#ServiceName} "Lee la lectora USB (puerto COM virtual) y reenvía cada lectura por WebSocket en 127.0.0.1."');
  { Recuperación: si el proceso termina con error (también la opción "Reiniciar" del icono), Windows lo vuelve a iniciar. }
  RunHidden(Sc, 'failure {#ServiceName} reset= 86400 actions= restart/2000/restart/2000/restart/5000');
  RunHidden(ExpandConstant('{sys}\net.exe'), 'start {#ServiceName}');
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
begin
  if (CurUninstallStep = usPostUninstall) and DirExists(SettingsDir) then
    { En desinstalación silenciosa se conserva la configuración. }
    if not UninstallSilent and
       (MsgBox('¿Eliminar también la configuración guardada (lectora elegida, puerto, etc.)?' + #13#10 +
               'Si piensa volver a instalar {#AppName}, elija No para conservarla.',
               mbConfirmation, MB_YESNO or MB_DEFBUTTON2) = IDYES) then
      DelTree(SettingsDir, True, True, True);
end;
