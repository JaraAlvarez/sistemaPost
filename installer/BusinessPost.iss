; =====================================================================================================================================
; Instalador de BusinessPost (Fase 13, docs/fases/fase-13-propuesta.md, ADR-0055 y ADR-0056).
; Se compila con tools/scripts/build-installer.ps1 (Inno Setup 6): no lo compile a mano, el script prepara artifacts\package.
;
; Modos (nombres claros, Fase 13-B §6.2): "Este equipo es la única caja" (Caja Única) · "Este es el equipo principal" (Multicaja;
;       internamente "servidor") · "Esta es una caja adicional" (Multicaja; internamente "caja").
; Primer arranque (Fase 13-B, D13B-01 y D13B-02): precalienta los programas (Windows Defender) antes de crear los servicios, muestra el
;       paso en curso y espera a que BusinessPost responda antes de abrir el navegador.
; Deja: C:\Program Files\BusinessPost\app\<versión>\{server,migrator,agent,desktop}, app\current (unión NTFS a la versión activa),
;       updater\ y pgsql\ (PostgreSQL 18 empaquetado). Datos, backups y configuración en C:\ProgramData\BusinessPost (se CONSERVAN al
;       desinstalar salvo que el usuario pida borrarlos, con doble confirmación).
; =====================================================================================================================================

#ifndef AppVersion
  #define AppVersion "0.1.0"
#endif
#ifndef PackageDir
  #define PackageDir "..\artifacts\package"
#endif
#ifndef LicenseServer
  #define LicenseServer ""
#endif
#ifndef UpdateManifest
  #define UpdateManifest ""
#endif
#ifndef OutputName
  ; El build arma con este paquete el instalador premium (BusinessPost-Setup-<versión>.exe, Fase 13-B D13B-08) y lo llama "Clasico".
  #define OutputName "BusinessPost-Setup-" + AppVersion
#endif
#define Product "BusinessPost"
#define DataDir "{commonappdata}\BusinessPost"

[Setup]
; AppId nuevo con el nombre BusinessPost (antes no había instalaciones en clientes). No lo cambie: identifica la instalación en
; el registro de Windows (desinstalar y actualizar encima).
AppId={{ED01E874-EA07-4102-813B-7DAD1DE455D9}
AppName=BusinessPost
AppVersion={#AppVersion}
AppPublisher={#Product}
DefaultDirName={autopf}\{#Product}
DisableDirPage=yes
DisableProgramGroupPage=yes
PrivilegesRequired=admin
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
; Windows 10 22H2 o posterior (D13, riesgo "Windows 10 viejo").
MinVersion=10.0.19045
Compression=lzma2/ultra64
SolidCompression=yes
WizardStyle=modern
SetupIconFile=BusinessPost-Setup.ico
UninstallDisplayIcon={app}\app\current\desktop\BusinessPost.exe
OutputDir=..\artifacts\releases
OutputBaseFilename={#OutputName}
UninstallDisplayName=BusinessPost
SetupLogging=yes
CloseApplications=no

[Languages]
Name: "es"; MessagesFile: "compiler:Languages\Spanish.isl"

[Files]
Source: "{#PackageDir}\app\{#AppVersion}\server\*";   DestDir: "{app}\app\{#AppVersion}\server";   Flags: recursesubdirs ignoreversion; Check: IsServerMode
Source: "{#PackageDir}\app\{#AppVersion}\migrator\*"; DestDir: "{app}\app\{#AppVersion}\migrator"; Flags: recursesubdirs ignoreversion; Check: IsServerMode
Source: "{#PackageDir}\app\{#AppVersion}\agent\*";    DestDir: "{app}\app\{#AppVersion}\agent";    Flags: recursesubdirs ignoreversion
Source: "{#PackageDir}\pgsql\*";                      DestDir: "{app}\pgsql";                      Flags: recursesubdirs ignoreversion; Check: IsServerMode
Source: "{#PackageDir}\updater\Pos.Server.Updater.exe"; DestDir: "{app}\updater"; Flags: ignoreversion
; Ventana propia BusinessPost.exe (Fase 13-B, D13B-07), en los dos modos. Va dentro de la versión (app\<versión>\desktop, también en el
; paquete de actualización): los accesos directos abren app\current\desktop y el actualizador la cambia con la versión. Si está abierta
; al reinstalar, se reemplaza al reiniciar. La segunda línea solo refresca la carpeta {app}\desktop de instalaciones anteriores (accesos
; directos o ventana anclada a la barra de tareas que todavía apunten allí); en instalaciones nuevas esa carpeta no existe.
Source: "{#PackageDir}\app\{#AppVersion}\desktop\*"; DestDir: "{app}\app\{#AppVersion}\desktop"; Flags: recursesubdirs ignoreversion restartreplace uninsrestartdelete skipifsourcedoesntexist
Source: "{#PackageDir}\app\{#AppVersion}\desktop\*"; DestDir: "{app}\desktop"; Flags: recursesubdirs ignoreversion restartreplace uninsrestartdelete skipifsourcedoesntexist; Check: HasLegacyDesktopApp
; Ayudantes del primer arranque (Fase 13-B): no se instalan; se usan desde la carpeta temporal durante la instalación.
Source: "scripts\precalentar.ps1";      Flags: dontcopy
Source: "scripts\esperar-servicio.ps1"; Flags: dontcopy

[Icons]
; La interfaz se abre en la ventana propia BusinessPost.exe (Fase 13-B, D13B-07): lee la dirección de C:\ProgramData\BusinessPost\config
; (equipo principal: localhost; caja adicional: el equipo principal de updater.json) y muestra la pantalla de carga mientras arranca.
; Apuntan a app\current (unión NTFS a la versión activa): después de una actualización o de una vuelta atrás abren la ventana de esa versión.
Name: "{commondesktop}\BusinessPost"; Filename: "{app}\app\current\desktop\BusinessPost.exe"; WorkingDir: "{app}\app\current\desktop"; Comment: "Abrir BusinessPost"; Check: HasDesktopApp
Name: "{commonprograms}\BusinessPost\BusinessPost"; Filename: "{app}\app\current\desktop\BusinessPost.exe"; WorkingDir: "{app}\app\current\desktop"; Comment: "Abrir BusinessPost"; Check: HasDesktopApp
; Respaldo solo si falta BusinessPost.exe: Edge en modo aplicación (Fase 15, D15-03), como antes.
Name: "{commondesktop}\BusinessPost"; Filename: "{code:EdgePath}"; Parameters: "--app={code:StartUrl}"; IconFilename: "{app}\updater\Pos.Server.Updater.exe"; Check: not HasDesktopApp
Name: "{commonprograms}\BusinessPost\BusinessPost"; Filename: "{code:EdgePath}"; Parameters: "--app={code:StartUrl}"; Check: not HasDesktopApp
Name: "{commonprograms}\BusinessPost\Paquete de soporte"; Filename: "{app}\app\current\migrator\Pos.Server.Migrator.exe"; Parameters: "support-bundle"; Check: IsServerMode

[UninstallRun]
; Los servicios ya se DETUVIERON (esperando a que paren) en CurUninstallStepChanged(usUninstall); aquí solo se quitan.
Filename: "{sys}\sc.exe"; Parameters: "delete {#Product}-Updater"; Flags: runhidden; RunOnceId: "DelUpdater"
Filename: "{sys}\sc.exe"; Parameters: "delete {#Product}-TerminalAgent"; Flags: runhidden; RunOnceId: "DelAgent"
Filename: "{sys}\sc.exe"; Parameters: "delete {#Product}-Server"; Flags: runhidden; RunOnceId: "DelServer"
Filename: "{app}\pgsql\bin\pg_ctl.exe"; Parameters: "unregister -N {#Product}-DB"; Flags: runhidden skipifdoesntexist; RunOnceId: "DelDb"
Filename: "{sys}\netsh.exe"; Parameters: "advfirewall firewall delete rule name=""{#Product} LAN"""; Flags: runhidden; RunOnceId: "DelFirewall"
Filename: "{cmd}"; Parameters: "/c rmdir ""{app}\app\current"""; Flags: runhidden; RunOnceId: "DelJunction"

[UninstallDelete]
; Copia del instalador premium que deja la instalación para desinstalar con el mismo diseño (Fase 13-B §8).
Type: filesandordirs; Name: "{app}\instalador"
; Todas las versiones de app\ (también las que instaló el actualizador, que Inno no conoce), current.json y PostgreSQL. La unión
; app\current se quita antes en [UninstallRun]. Los DATOS (ProgramData) solo se borran si el usuario lo pidió.
Type: filesandordirs; Name: "{app}\app"
Type: filesandordirs; Name: "{app}\pgsql"
Type: filesandordirs; Name: "{app}\updater"
Type: filesandordirs; Name: "{app}\desktop"
Type: dirifempty; Name: "{app}"

[Code]
const
  // Clave de desinstalación que crea Inno con el AppId de [Setup] (aquí sin la doble llave de esa sección). Ahí quedan DisplayVersion y
  // los datos previos ("Inno Setup CodeFile: Modo"); el instalador premium lee la misma clave (Sistema.cs).
  UninstallKey = 'Software\Microsoft\Windows\CurrentVersion\Uninstall\{ED01E874-EA07-4102-813B-7DAD1DE455D9}_is1';
  // Códigos de salida del paquete cuando algo falló después de copiar los archivos (IN7; GetCustomSetupExitCode). Con ellos el instalador
  // premium muestra el error en vez de "instalado".
  ExitDatabaseFailed = 20;   // la base de datos o la recuperación fallaron; los datos y la versión anterior quedaron como estaban
  ExitNeedsSupport = 21;     // la migración falló y NO se pudo volver a los datos de antes: restaurar con soporte (no arranca el servidor)
  ExitActivationFailed = 22; // no se pudo cambiar app\current a la versión nueva

var
  ModePage: TInputOptionWizardPage;
  RecoveryPage: TInputQueryWizardPage;
  RecoveryFilePage: TInputFileWizardPage;
  TerminalPage: TInputQueryWizardPage;
  DeleteData: Boolean;
  LastProgress: Integer;
  // Modo con el que ya estaba instalado el equipo (-1 = instalación nueva) y si el usuario confirmó cambiarlo (I5).
  PreviousMode: Integer;
  ModeChangeConfirmed: Boolean;
  PublicNetwork: Boolean;
  // Lo que había antes de esta instalación, para la vuelta atrás (IN3, IN5, IN7): versión y modo guardados por Inno y los servicios que
  // estaban EN MARCHA cuando se detuvieron (lista "|nombre|nombre|").
  PreviousVersion: String;
  PreviousModeSaved: String;
  StoppedServices: String;
  // IN6: la base de datos de una caja adicional que pasa a equipo principal se habilita antes de migrar; si falla, se vuelve a deshabilitar.
  DbReEnabled: Boolean;
  // IN7: distinto de cero si algo falló después de copiar los archivos (código de salida del paquete).
  FailureCode: Integer;

// ------------------------------------------------------------------------------------------------------------------------------------
// Instalador premium (Fase 13-B, D13B-08): BusinessPost-Setup.exe recoge los datos con su propio diseño y ejecuta ESTE paquete en
// silencio (/VERYSILENT /SUPPRESSMSGBOXES /LOG=...) con estos parámetros. Lee el avance del registro: líneas "Progreso: N" (copia de
// archivos), "Paso: ..." (cada etapa) y "Resultado: ..." (al final). Sin /PREMIUM=1 el asistente clásico funciona como siempre.
//   /PREMIUM=1                          no abre el navegador al final (el instalador premium configura y abre BusinessPost)
//   /MODO=unica|principal|adicional     tipo de instalación
//   /SERVIDOR=https://...:5443/ /HUELLA=...   caja adicional: equipo principal y huella de su certificado
//   /BACKUP=ruta.posbak /RECUPERACION=archivo  recuperar: el archivo con el código se borra apenas se lee (el código no va en la línea
//                                               de comandos)
//   /CAMBIARMODO=si                     el usuario confirmó cambiar el modo de un equipo ya instalado (sin él, en silencio, se cancela)
// Desinstalar: /BORRARDATOS=si (solo en silencio; el premium ya pidió la doble confirmación).
// ------------------------------------------------------------------------------------------------------------------------------------
function Premium: Boolean;
begin
  Result := ExpandConstant('{param:PREMIUM|0}') = '1';
end;

procedure ApplyPremiumParams;
var
  Mode, CodeFile: String;
  Code: AnsiString;
begin
  Mode := Lowercase(ExpandConstant('{param:MODO|}'));
  if Mode = 'unica' then ModePage.SelectedValueIndex := 0;
  if Mode = 'principal' then ModePage.SelectedValueIndex := 1;
  if Mode = 'adicional' then ModePage.SelectedValueIndex := 2;
  if ExpandConstant('{param:SERVIDOR|}') <> '' then
    TerminalPage.Values[0] := ExpandConstant('{param:SERVIDOR|}');
  if ExpandConstant('{param:HUELLA|}') <> '' then
    TerminalPage.Values[1] := ExpandConstant('{param:HUELLA|}');
  if ExpandConstant('{param:BACKUP|}') <> '' then
    RecoveryFilePage.Values[0] := ExpandConstant('{param:BACKUP|}');
  CodeFile := ExpandConstant('{param:RECUPERACION|}');
  if (CodeFile <> '') and LoadStringFromFile(CodeFile, Code) then
  begin
    RecoveryPage.Values[0] := Trim(String(Code));
    DeleteFile(CodeFile);
  end;
  if Mode <> '' then
    Log('Instalador premium: modo ' + Mode);
  if Lowercase(ExpandConstant('{param:CAMBIARMODO|no}')) = 'si' then
    ModeChangeConfirmed := True;
end;

// Avance real de la copia de archivos para el instalador premium (una línea por cada punto porcentual).
procedure CurInstallProgressChanged(CurProgress, MaxProgress: Integer);
var
  Percent: Integer;
begin
  if MaxProgress <= 0 then
    exit;
  Percent := (CurProgress * 100) div MaxProgress;
  if Percent <> LastProgress then
  begin
    LastProgress := Percent;
    Log('Progreso: ' + IntToStr(Percent));
  end;
end;

function IsServerMode: Boolean;
begin
  Result := ModePage.SelectedValueIndex <> 2;
end;

function IsMulti: Boolean;
begin
  Result := ModePage.SelectedValueIndex <> 0;
end;

function StartUrl(Param: String): String;
begin
  if IsServerMode then
    Result := 'http://localhost:5480/'
  else
    Result := TerminalPage.Values[0];
end;

function EdgePath(Param: String): String;
begin
  Result := ExpandConstant('{commonpf32}\Microsoft\Edge\Application\msedge.exe');
  if not FileExists(Result) then
    Result := ExpandConstant('{commonpf64}\Microsoft\Edge\Application\msedge.exe');
end;

// Fase 13-B (D13B-07): los accesos directos abren BusinessPost.exe; Edge en modo aplicación queda solo de respaldo si falta.
function HasDesktopApp: Boolean;
begin
  Result := FileExists(ExpandConstant('{app}\app\{#AppVersion}\desktop\BusinessPost.exe'));
end;

// Instalaciones anteriores dejaban la ventana en {app}\desktop (sin versión): se refresca para no dejar una ventana vieja anclada.
function HasLegacyDesktopApp: Boolean;
begin
  Result := FileExists(ExpandConstant('{app}\desktop\BusinessPost.exe'));
end;

function ModeName(Index: Integer): String;
begin
  case Index of
    0: Result := 'unica';
    1: Result := 'principal';
    2: Result := 'adicional';
  else
    Result := '';
  end;
end;

function ModeLabel(Index: Integer): String;
begin
  case Index of
    0: Result := 'la única caja (Caja Única)';
    1: Result := 'el equipo principal (Multicaja)';
    2: Result := 'una caja adicional (Multicaja)';
  else
    Result := 'otro modo';
  end;
end;

// Modo con el que YA está instalado este equipo (I5): lo recuerda Inno (datos previos de la instalación) y, en instalaciones anteriores a
// este cambio, se deduce de la configuración: updater.json en modo "Terminal" = caja adicional; server.json con edición MULTI = equipo
// principal, SINGLE = única caja. -1 si es una instalación nueva.
function DetectPreviousMode: Integer;
var
  Saved, Data: String;
  Text: AnsiString;
begin
  Result := -1;
  Saved := GetPreviousData('Modo', '');
  if Saved = 'unica' then Result := 0;
  if Saved = 'principal' then Result := 1;
  if Saved = 'adicional' then Result := 2;
  if Result >= 0 then
    exit;
  Data := ExpandConstant('{#DataDir}\config');
  if LoadStringFromFile(Data + '\updater.json', Text) and (Pos('"Terminal"', String(Text)) > 0) then
    Result := 2
  else if LoadStringFromFile(Data + '\server.json', Text) then
  begin
    if Pos('"MULTI"', String(Text)) > 0 then Result := 1 else Result := 0;
  end;
end;

// IN7: el modo nuevo solo se recuerda si la instalación terminó bien; si falló se deja el de antes (el equipo sigue en ese modo).
function ModeToRemember: String;
begin
  if (FailureCode <> 0) and (PreviousMode >= 0) then
    Result := ModeName(PreviousMode)
  else
    Result := ModeName(ModePage.SelectedValueIndex);
end;

procedure RegisterPreviousData(PreviousDataKey: Integer);
begin
  SetPreviousData(PreviousDataKey, 'Modo', ModeToRemember);
end;

// IN7: Inno registra la versión y los datos previos ANTES de ssPostInstall; si después falla la base de datos (o la recuperación, o el
// cambio de versión activa) se vuelven a dejar la versión y el modo de antes en la clave de desinstalación.
procedure KeepPreviousInstallData;
begin
  if PreviousMode >= 0 then
  begin
    if RegWriteStringValue(HKLM, UninstallKey, 'Inno Setup CodeFile: Modo', ModeToRemember) then
      Log('Vuelta atrás: se conserva el modo ' + ModeToRemember);
  end;
  if PreviousVersion <> '' then
  begin
    if RegWriteStringValue(HKLM, UninstallKey, 'DisplayVersion', PreviousVersion) then
      Log('Vuelta atrás: se conserva la versión ' + PreviousVersion);
  end;
end;

// IN7: el paquete termina con un código distinto de cero si algo falló después de copiar (Inno solo llama a esta función si la
// instalación llegó al final; con 0 devuelve su propio código).
function GetCustomSetupExitCode: Integer;
begin
  Result := FailureCode;
end;

procedure MarkFailed(Code: Integer; const Step: String);
begin
  if Code > FailureCode then
    FailureCode := Code;
  Log('Resultado: ERROR ' + Step);
end;

// Texto para JSON (comillas y barras invertidas escapadas) (I11).
function JsonText(const Value: String): String;
begin
  Result := Value;
  StringChangeEx(Result, '\', '\\', True);
  StringChangeEx(Result, '"', '\"', True);
end;

// Origen (esquema://host:puerto, sin ruta ni barra final) de la dirección del equipo principal (I11): antes se cortaba el último
// carácter suponiendo una "/" final y https://192.168.1.10:5443 quedaba como https://192.168.1.10:544.
function OriginOf(const Url: String): String;
var
  Rest: String;
  P: Integer;
begin
  Result := Trim(Url);
  P := Pos('://', Result);
  if P = 0 then
    exit;
  Rest := Copy(Result, P + 3, Length(Result));
  if Pos('/', Rest) > 0 then
    Rest := Copy(Rest, 1, Pos('/', Rest) - 1);
  Result := Copy(Result, 1, P + 2) + Rest;
end;

// La dirección del equipo principal siempre con "/" final (la usa el actualizador como base).
function WithSlash(const Url: String): String;
begin
  Result := Trim(Url);
  if (Result <> '') and (Copy(Result, Length(Result), 1) <> '/') then
    Result := Result + '/';
end;

// Texto entre comillas simples para PowerShell (duplica las comillas simples internas).
function PsText(const Value: String): String;
begin
  Result := Value;
  StringChangeEx(Result, '''', '''''', True);
  Result := '''' + Result + '''';
end;

// PowerShell en línea (sin archivo: también sirve en el desinstalador) para DETENER los servicios de BusinessPost y ESPERAR a que queden
// detenidos (I1, I2, I9): primero el actualizador (puede estar cambiando archivos), luego el servidor, el agente y la base de datos.
// IN2: Stop-Service -Force se quedaba esperando SIN LÍMITE a un servicio trabado en "deteniéndose" (el tiempo de WaitForStatus nunca
// llegaba a contar). Ahora se pide parar sin bloquear ($s.Stop()), se espera con límite y, si vence, se cierra el proceso del servicio
// (PID de Win32_Service) y se marca el fallo.
// IN8: después se cierran los procesos sueltos que corren desde la carpeta de la instalación (actualizador, migrador, "discover", servidor
// o agente lanzados a mano) y que dejarían archivos en uso.
// Escribe "Deteniendo <servicio>" por cada servicio que estaba en marcha (para volver a arrancarlo si se cancela, IN3).
// Código 0 = todos detenidos (o no existen); 1 = alguno no se detuvo a tiempo y se cerró su proceso a la fuerza (nombres en el registro).
function StopServicesCommand(const Seconds: Integer; const AppDir: String): String;
begin
  Result := '$ErrorActionPreference = ''SilentlyContinue''; $fallos = @(); ' +
    'foreach ($n in @(''{#Product}-Updater'', ''{#Product}-Server'', ''{#Product}-TerminalAgent'', ''{#Product}-DB'')) { ' +
    '$s = Get-Service -Name $n; if ($s -and $s.Status -ne ''Stopped'') { Write-Output (''Deteniendo '' + $n); ' +
    'try { $s.Stop() } catch { }; ' +
    'try { $s.WaitForStatus(''Stopped'', [TimeSpan]::FromSeconds(' + IntToStr(Seconds) + ')) } catch { ' +
    '$p = (Get-CimInstance -ClassName Win32_Service | Where-Object { $_.Name -eq $n }).ProcessId; ' +
    'if ($p -gt 0) { Write-Output (''Cerrando a la fuerza '' + $n + '' (proceso '' + $p + '')''); Stop-Process -Id $p -Force }; ' +
    '$fallos += $n } } }; ' +
    '$carpeta = ' + PsText(AddBackslash(AppDir)) + '; ' +
    'Get-Process | Where-Object { $_.Path -and $_.Path.StartsWith($carpeta, [StringComparison]::OrdinalIgnoreCase) -and ' +
    '($_.ProcessName -in @(''Pos.Server.Updater'', ''Pos.Server.Migrator'', ''Pos.Server.Host'', ''Pos.Terminal.Agent'')) } | ' +
    'ForEach-Object { Write-Output (''Cerrando '' + $_.Path); Stop-Process -Id $_.Id -Force }; ' +
    'if ($fallos.Count -gt 0) { Write-Output (''No se detuvieron a tiempo: '' + ($fallos -join '', '')); exit 1 }; exit 0';
end;

// Salida de StopServicesCommand: va al registro y anota los servicios que estaban en marcha.
procedure StopServicesOutput(const S: String; const Error, FirstLine: Boolean);
begin
  Log(S);
  if Pos('Deteniendo ', S) = 1 then
    StoppedServices := StoppedServices + '|' + Trim(Copy(S, Length('Deteniendo ') + 1, Length(S))) + '|';
end;

function StopServices: Boolean;
var
  Code: Integer;
begin
  StoppedServices := '';
  try
    Result := ExecAndLogOutput(ExpandConstant('{sys}\WindowsPowerShell\v1.0\powershell.exe'),
      '-NoProfile -NonInteractive -Command "' + StopServicesCommand(90, ExpandConstant('{app}')) + '"', '', SW_HIDE, ewWaitUntilTerminated, Code,
      @StopServicesOutput) and (Code = 0);
  except
    Log('No se pudo ejecutar PowerShell para detener los servicios: ' + GetExceptionMessage);
    Result := False;
  end;
end;

procedure StartService(const Name: String);
var
  Code: Integer;
begin
  Exec(ExpandConstant('{sys}\sc.exe'), 'start ' + Name, '', SW_HIDE, ewWaitUntilTerminated, Code);
  Log('sc start ' + Name + ': código ' + IntToStr(Code));
end;

// IN3: vuelve a arrancar SOLO los servicios que estaban en marcha antes de detenerlos, en orden: base de datos, agente, servidor y
// actualizador. Sin StartServer no arranca el servidor ni el actualizador (la base quedó para restaurar con soporte, IN4).
procedure RestartStoppedServices(const StartServer: Boolean);
begin
  if StoppedServices = '' then
    exit;
  Log('Vuelta atrás: se vuelven a iniciar los servicios que estaban en marcha (' + StoppedServices + ')');
  if Pos('|{#Product}-DB|', StoppedServices) > 0 then
    StartService('{#Product}-DB');
  if Pos('|{#Product}-TerminalAgent|', StoppedServices) > 0 then
    StartService('{#Product}-TerminalAgent');
  if StartServer and (Pos('|{#Product}-Server|', StoppedServices) > 0) then
    StartService('{#Product}-Server');
  if StartServer and (Pos('|{#Product}-Updater|', StoppedServices) > 0) then
    StartService('{#Product}-Updater');
end;

// IN5: el actualizador ({app}\updater) se sobrescribe al copiar los archivos, antes de migrar. Se guarda una copia para recuperarlo si la
// instalación vuelve atrás. pgsql\ NO se respalda: el paquete trae siempre PostgreSQL 18 y dentro de la misma versión mayor los binarios
// son compatibles con los datos y con la versión anterior de BusinessPost (ver docs/fases/fase-13b-propuesta.md §9.6).
function UpdaterFile: String;
begin
  Result := ExpandConstant('{app}\updater\Pos.Server.Updater.exe');
end;

procedure BackupUpdater;
begin
  if FileExists(UpdaterFile) then
  begin
    if FileCopy(UpdaterFile, UpdaterFile + '.anterior', False) then
      Log('Copia del actualizador anterior: ' + UpdaterFile + '.anterior')
    else
      Log('Advertencia: no se pudo copiar el actualizador anterior.');
  end;
end;

procedure RestoreUpdater;
begin
  if FileExists(UpdaterFile + '.anterior') then
  begin
    if FileCopy(UpdaterFile + '.anterior', UpdaterFile, False) then
      Log('Vuelta atrás: se recuperó el actualizador anterior')
    else
      Log('Advertencia: no se pudo recuperar el actualizador anterior.');
  end;
end;

// Antes de copiar archivos (I1): con los servicios en marcha, pgsql\*, el actualizador y app\<versión>\server están en uso ("archivo en
// uso"; en silencio se cancelaba o quedaban binarios mezclados). Además revisa el cambio de modo de un equipo ya instalado (I5).
function PrepareToInstall(var NeedsRestart: Boolean): String;
begin
  Result := '';
  if (PreviousMode >= 0) and (ModePage.SelectedValueIndex <> PreviousMode) and not ModeChangeConfirmed then
  begin
    Result := 'Este equipo ya está instalado como ' + ModeLabel(PreviousMode) + '. Para cambiarlo a ' + ModeLabel(ModePage.SelectedValueIndex) +
      ' confirme el cambio en el instalador (o elija el mismo modo).';
    Log('Resultado: ERROR Cambio de modo sin confirmar (' + ModeName(PreviousMode) + ' a ' + ModeName(ModePage.SelectedValueIndex) + ')');
    exit;
  end;

  Log('Paso: Deteniendo BusinessPost para actualizar sus archivos…');
  if not StopServices then
  begin
    // IN3: se cancela sin tocar nada: lo que se detuvo vuelve a arrancar (antes el equipo quedaba con BusinessPost apagado).
    RestartStoppedServices(True);
    Result := 'No se pudieron detener los servicios de BusinessPost para actualizar sus archivos. Reinicie el computador e intente de nuevo.';
    Log('Resultado: ERROR Detener los servicios de BusinessPost');
    exit;
  end;
  BackupUpdater;
end;

procedure InitializeWizard;
begin
  // No cambie el orden de las opciones: el código usa SelectedValueIndex (0 = única caja, 1 = equipo principal, 2 = caja adicional).
  ModePage := CreateInputOptionPage(wpWelcome, 'Tipo de instalación', '¿Cómo usará este equipo?',
    'Si el local tiene una sola caja, elija la primera opción. Si tiene varias, instale primero el equipo principal y después cada caja.',
    True, False);
  ModePage.Add('Este equipo es la única caja (Caja Única)');
  ModePage.Add('Este es el equipo principal: guarda los datos y las demás cajas se conectan a él (Multicaja)');
  ModePage.Add('Esta es una caja adicional: se conecta al equipo principal del local (Multicaja)');
  ModePage.SelectedValueIndex := 0;

  RecoveryPage := CreateInputQueryPage(ModePage.ID, 'Recuperar desde un backup (opcional)',
    'Solo si este computador reemplaza a uno dañado',
    'Si es una instalación nueva, deje todo en blanco y pulse Siguiente. Para recuperar, elija el archivo .posbak en la siguiente página ' +
    'y escriba aquí el código de recuperación del propietario (24 caracteres).');
  RecoveryPage.Add('Código de recuperación:', False);
  RecoveryFilePage := CreateInputFilePage(RecoveryPage.ID, 'Archivo del backup', 'Backup a restaurar (opcional)',
    'Deje en blanco para una instalación nueva.');
  RecoveryFilePage.Add('Archivo .posbak:', 'Backups (*.posbak)|*.posbak', '.posbak');

  TerminalPage := CreateInputQueryPage(RecoveryFilePage.ID, 'Equipo principal del local', '¿A qué equipo se conecta esta caja?',
    'Buscamos el equipo principal en la red del local. Si los datos no aparecen solos, cópielos de la administración del equipo ' +
    'principal (Cajas e impresoras). La dirección se ve así: https://192.168.1.10:5443/');
  TerminalPage.Add('Dirección del equipo principal:', False);
  TerminalPage.Add('Huella del certificado (SHA-256):', False);

  LastProgress := -1;
  ModeChangeConfirmed := False;
  PublicNetwork := False;
  // Actualizar o reinstalar: se propone el modo con el que ya está instalado (antes el asistente clásico proponía siempre "única caja"
  // y pasaba MULTI→SINGLE, quitaba el firewall y creaba el agente; en una caja adicional instalaba PostgreSQL y un servidor vacío).
  FailureCode := 0;
  DbReEnabled := False;
  StoppedServices := '';
  PreviousModeSaved := GetPreviousData('Modo', '');
  if not RegQueryStringValue(HKLM, UninstallKey, 'DisplayVersion', PreviousVersion) then
    PreviousVersion := '';
  PreviousMode := DetectPreviousMode;
  if PreviousMode >= 0 then
  begin
    ModePage.SelectedValueIndex := PreviousMode;
    Log('Instalación existente en modo ' + ModeName(PreviousMode));
  end;
  ApplyPremiumParams;
end;

function ShouldSkipPage(PageID: Integer): Boolean;
begin
  Result := False;
  if (PageID = RecoveryPage.ID) or (PageID = RecoveryFilePage.ID) then
    Result := not IsServerMode;
  if PageID = TerminalPage.ID then
    Result := IsServerMode;
end;

// Caja adicional: busca el equipo principal ("servidor") en la LAN con el actualizador ("discover") y propone el primero encontrado.
procedure CurPageChanged(CurPageID: Integer);
var
  ResultCode: Integer;
  Output: AnsiString;
  Tmp, Url, Print: String;
  P: Integer;
begin
  if (CurPageID = TerminalPage.ID) and (TerminalPage.Values[0] = '') then
  begin
    ExtractTemporaryFile('Pos.Server.Updater.exe');
    Tmp := ExpandConstant('{tmp}\servidores.json');
    Exec(ExpandConstant('{cmd}'), '/c ""' + ExpandConstant('{tmp}\Pos.Server.Updater.exe') + '" discover --seconds 3 > "' + Tmp + '""',
      '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
    if (ResultCode = 0) and LoadStringFromFile(Tmp, Output) then
    begin
      P := Pos('"address": "', Output);
      if P > 0 then
      begin
        Url := Copy(Output, P + 12, 64);
        Url := Copy(Url, 1, Pos('"', Url) - 1);
        TerminalPage.Values[0] := 'https://' + Url + ':5443/';
      end;
      P := Pos('"certificateThumbprint": "', Output);
      if P > 0 then
      begin
        Print := Copy(Output, P + 26, 80);
        TerminalPage.Values[1] := Copy(Print, 1, Pos('"', Print) - 1);
      end;
    end;
  end;
end;

function NextButtonClick(CurPageID: Integer): Boolean;
begin
  Result := True;
  if (CurPageID = ModePage.ID) and (PreviousMode >= 0) and (ModePage.SelectedValueIndex <> PreviousMode) then
  begin
    ModeChangeConfirmed := MsgBox('Este equipo ya está instalado como ' + ModeLabel(PreviousMode) + '.' + #13#10#13#10 +
      '¿Cambiarlo a ' + ModeLabel(ModePage.SelectedValueIndex) + '? Se quitarán los servicios y las reglas del modo anterior que ya no se ' +
      'usen. Los datos de la tienda se conservan.', mbConfirmation, MB_YESNO or MB_DEFBUTTON2) = IDYES;
    Result := ModeChangeConfirmed;
    exit;
  end;
  if (CurPageID = TerminalPage.ID) and ((TerminalPage.Values[0] = '') or (TerminalPage.Values[1] = '')) then
  begin
    MsgBox('Escriba la dirección del equipo principal y la huella de su certificado (las muestra la administración del equipo ' +
      'principal, en Cajas e impresoras).', mbError, MB_OK);
    Result := False;
  end;
  if (CurPageID = RecoveryFilePage.ID) and (RecoveryFilePage.Values[0] <> '') and (RecoveryPage.Values[0] = '') then
  begin
    MsgBox('Para recuperar un backup escriba también el código de recuperación.', mbError, MB_OK);
    Result := False;
  end;
end;

// Oculta en el texto del registro el valor de las opciones con secretos (I3): el registro de la instalación se envía a soporte.
function MaskSecrets(const Params: String): String;
var
  P, Q: Integer;
begin
  Result := Params;
  P := Pos('--recovery-code "', Result);
  if P > 0 then
  begin
    Q := P + Length('--recovery-code "');
    while (Q <= Length(Result)) and (Result[Q] <> '"') do
      Q := Q + 1;
    Result := Copy(Result, 1, P + Length('--recovery-code "') - 1) + '***' + Copy(Result, Q, Length(Result));
  end;
end;

function Run(const FileName, Params, Step: String): Boolean;
var
  ResultCode: Integer;
begin
  Log(Step + ': ' + FileName + ' ' + MaskSecrets(Params));
  Result := Exec(FileName, Params, '', SW_HIDE, ewWaitUntilTerminated, ResultCode) and (ResultCode = 0);
  if not Result then
  begin
    Log('Resultado: ERROR ' + Step + ' (código ' + IntToStr(ResultCode) + ')');
    MsgBox(Step + ' falló (código ' + IntToStr(ResultCode) + '). Revise el registro de la instalación y genere un paquete de soporte.', mbError, MB_OK);
  end;
end;

procedure MigratorOutput(const S: String; const Error, FirstLine: Boolean);
begin
  if Trim(S) <> '' then
    Log('Migrador: ' + S);
end;

// El migrador, con su salida en el registro de la instalación (IN4: ahí queda el mensaje de "restaurar con soporte" y la ruta del
// backup). Devuelve su código de salida (-1 si no se pudo ejecutar); no muestra mensajes: los decide quien llama.
function RunMigrator(const FileName, Params, Step: String): Integer;
begin
  Log(Step + ': ' + FileName + ' ' + MaskSecrets(Params));
  try
    if not ExecAndLogOutput(FileName, Params, '', SW_HIDE, ewWaitUntilTerminated, Result, @MigratorOutput) then
      Result := -1;
  except
    Log('No se pudo ejecutar el migrador: ' + GetExceptionMessage);
    Result := -1;
  end;
  if Result <> 0 then
    Log('Resultado: ERROR ' + Step + ' (código ' + IntToStr(Result) + ')');
end;

// Inicio "auto" (no "delayed-auto") para todos: con los programas ya precalentados el arranque al encender el equipo es rápido, y un
// inicio retrasado haría esperar unos 2 minutos a la caja cada mañana. Si el servicio no alcanza a iniciar, la espera del final de la
// instalación lo vuelve a iniciar (WaitForService) y, si se cae después, Windows lo reinicia solo (acciones de recuperación).
function ServiceExists(const Name: String): Boolean;
var
  Code: Integer;
begin
  Result := Exec(ExpandConstant('{sys}\sc.exe'), 'query ' + Name, '', SW_HIDE, ewWaitUntilTerminated, Code) and (Code = 0);
end;

// sc que no detiene la instalación si falla, pero deja el aviso en el registro (antes el resultado se ignoraba).
procedure ScOptional(const Params, What: String);
var
  Code: Integer;
begin
  if not Exec(ExpandConstant('{sys}\sc.exe'), Params, '', SW_HIDE, ewWaitUntilTerminated, Code) or (Code <> 0) then
    Log('Advertencia: no se pudo ' + What + ' (sc ' + Params + ', código ' + IntToStr(Code) + ').');
end;

// I2: si el servicio ya existe se RECONFIGURA (sc config) en vez de borrarlo y crearlo: "sc delete" de un servicio que todavía se está
// deteniendo lo deja "marcado para eliminación" y el "sc create" siguiente fallaba (1072): el equipo quedaba SIN servicio.
// I7: dependencia de la base de datos (el servidor no arranca antes que PostgreSQL), recuperación ante fallos también cuando el proceso
// termina con error (failureflag) y el resultado de cada paso queda en el registro.
procedure CreateService(const Name, Exe, Depends: String);
var
  Binary, Dependency: String;
begin
  Binary := ' start= auto binPath= "\"' + Exe + '\""';
  if Depends <> '' then Dependency := ' depend= ' + Depends else Dependency := '';
  if ServiceExists(Name) then
  begin
    if Depends = '' then Dependency := ' depend= /';
    Run(ExpandConstant('{sys}\sc.exe'), 'config ' + Name + Binary + Dependency, 'Servicio ' + Name);
  end
  else
    Run(ExpandConstant('{sys}\sc.exe'), 'create ' + Name + Binary + Dependency, 'Servicio ' + Name);
  // Recuperación automática ante fallos: reinicia a los 5 s (tres veces), el contador se reinicia cada día.
  ScOptional('failure ' + Name + ' reset= 86400 actions= restart/5000/restart/5000/restart/5000', 'configurar la recuperación de ' + Name);
  ScOptional('failureflag ' + Name + ' 1', 'activar la recuperación ante errores de ' + Name);
end;

// Quita un servicio que el modo elegido ya no usa (cambio de modo, I5). Los servicios ya están detenidos (PrepareToInstall).
procedure RemoveService(const Name: String);
begin
  if ServiceExists(Name) then
  begin
    Log('Cambio de modo: se quita el servicio ' + Name);
    ScOptional('delete ' + Name, 'quitar el servicio ' + Name);
  end;
end;

// Regla del firewall de la LAN (I6): se borra antes por nombre (sin duplicados al reinstalar), solo la subred local y en redes privadas y
// de dominio. Si la red activa es "Pública", Windows bloquea a las cajas: queda un aviso claro.
procedure RemoveFirewallRule;
var
  Code: Integer;
begin
  Exec(ExpandConstant('{sys}\netsh.exe'), 'advfirewall firewall delete rule name="{#Product} LAN"', '', SW_HIDE, ewWaitUntilTerminated, Code);
end;

procedure AddFirewallRules;
var
  Code: Integer;
begin
  RemoveFirewallRule;
  Run(ExpandConstant('{sys}\netsh.exe'), 'advfirewall firewall add rule name="{#Product} LAN" dir=in action=allow protocol=TCP localport=5443 ' +
    'remoteip=localsubnet profile=private,domain', 'Firewall (HTTPS)');
  Run(ExpandConstant('{sys}\netsh.exe'), 'advfirewall firewall add rule name="{#Product} LAN" dir=in action=allow protocol=UDP localport=5444 ' +
    'remoteip=localsubnet profile=private,domain', 'Firewall (descubrimiento)');
  if Exec(ExpandConstant('{sys}\WindowsPowerShell\v1.0\powershell.exe'),
    '-NoProfile -NonInteractive -Command "if (Get-NetConnectionProfile | Where-Object { $_.NetworkCategory -eq ''Public'' }) { exit 1 } else { exit 0 }"',
    '', SW_HIDE, ewWaitUntilTerminated, Code) and (Code = 1) then
  begin
    PublicNetwork := True;
    Log('Aviso: La red de este equipo está marcada como "Pública" y Windows no deja que las cajas se conecten. En Configuración → Red e ' +
      'Internet → (su red) → Propiedades, elija "Red privada".');
  end;
end;

// Vuelta atrás cuando algo falla después de copiar los archivos (I4, I8, IN3-IN7): app\current sigue en la versión anterior; se recupera
// el actualizador anterior (IN5), se deshace lo que se cambió del modo (IN6), se conservan la versión y el modo de antes en el registro
// de Windows (IN7) y se vuelven a iniciar los servicios que estaban en marcha (IN3). Sin StartServer el servidor y el actualizador
// quedan detenidos: la base de datos quedó para restaurar con soporte (IN4) y la versión anterior no debe arrancar sobre ella.
procedure RollBack(const StartServer: Boolean);
begin
  Log('Vuelta atrás: la instalación no terminó; se deja la versión anterior.');
  RestoreUpdater;
  if DbReEnabled then
    ScOptional('config {#Product}-DB start= disabled', 'volver a deshabilitar la base de datos de la caja adicional');
  KeepPreviousInstallData;
  RestartStoppedServices(StartServer);
end;

// Cambio de modo (I5), DESPUÉS de que la base de datos quedó lista (IN6): se quitan los servicios, la regla del firewall y la configuración
// del modo anterior que ya no se usan. Antes iba antes del migrador y, si éste fallaba, el equipo quedaba sin los servicios del modo
// anterior. Los datos de la tienda (ProgramData) se conservan; en una caja adicional la base de datos queda deshabilitada, no borrada.
procedure ApplyModeChange(const Data: String);
begin
  if ModePage.SelectedValueIndex = 1 then
    RemoveService('{#Product}-TerminalAgent');
  if not IsServerMode then
  begin
    RemoveService('{#Product}-Server');
    if ServiceExists('{#Product}-DB') then
      ScOptional('config {#Product}-DB start= disabled', 'deshabilitar la base de datos del modo anterior');
  end
  else if FileExists(Data + '\config\updater.json') then
  begin
    DeleteFile(Data + '\config\updater.json.anterior');
    if RenameFile(Data + '\config\updater.json', Data + '\config\updater.json.anterior') then
      Log('Cambio de modo: updater.json de caja adicional guardado como updater.json.anterior');
  end;
  if not (IsServerMode and IsMulti) then
    RemoveFirewallRule;
end;

// Versión de app\current según app\current.json ('' si no se sabe).
function ActiveVersion(const App: String): String;
var
  Text: AnsiString;
  P: Integer;
begin
  Result := '';
  if not LoadStringFromFile(App + '\app\current.json', Text) then
    exit;
  P := Pos('"version":"', String(Text));
  if P = 0 then
    exit;
  Result := Copy(String(Text), P + Length('"version":"'), 64);
  Result := Copy(Result, 1, Pos('"', Result) - 1);
end;

// Versión activa: unión NTFS app\current → app\<versión> (ADR-0056); los servicios apuntan a app\current.
// IN9: la unión nueva se crea primero aparte (app\current.nueva) y solo entonces se cambia por la anterior. Si algo falla, app\current
// sigue (o vuelve a quedar) en la versión anterior y se devuelve False: no se registran servicios apuntando a una ruta que no existe.
function ActivateVersion(const App, Version: String): Boolean;
var
  Code: Integer;
  Current, Staged, Previous: String;
begin
  Result := False;
  Current := App + '\app\current';
  Staged := App + '\app\current.nueva';
  Previous := ActiveVersion(App);
  Exec(ExpandConstant('{cmd}'), '/c rmdir "' + Staged + '"', '', SW_HIDE, ewWaitUntilTerminated, Code);
  if not Run(ExpandConstant('{cmd}'), '/c mklink /J "' + Staged + '" "' + Version + '"', 'Versión activa') then
    exit;
  Exec(ExpandConstant('{cmd}'), '/c rmdir "' + Current + '"', '', SW_HIDE, ewWaitUntilTerminated, Code);
  if DirExists(Current) then
  begin
    Log('Resultado: ERROR Versión activa (no se pudo quitar ' + Current + ')');
    Exec(ExpandConstant('{cmd}'), '/c rmdir "' + Staged + '"', '', SW_HIDE, ewWaitUntilTerminated, Code);
    exit;
  end;
  if RenameFile(Staged, Current) then
  begin
    SaveStringToFile(App + '\app\current.json', '{"version":"{#AppVersion}"}', False);
    Result := True;
    exit;
  end;
  Log('Resultado: ERROR Versión activa (no se pudo renombrar ' + Staged + ')');
  Exec(ExpandConstant('{cmd}'), '/c rmdir "' + Staged + '"', '', SW_HIDE, ewWaitUntilTerminated, Code);
  if (Previous <> '') and DirExists(App + '\app\' + Previous) then
  begin
    Exec(ExpandConstant('{cmd}'), '/c mklink /J "' + Current + '" "' + App + '\app\' + Previous + '"', '', SW_HIDE, ewWaitUntilTerminated, Code);
    Log('Vuelta atrás: app\current vuelve a ' + Previous + ' (código ' + IntToStr(Code) + ')');
  end;
end;

// ------------------------------------------------------------------------------------------------------------------------------------
// Primer arranque (Fase 13-B, D13B-01 y D13B-02)
// ------------------------------------------------------------------------------------------------------------------------------------

// Muestra el paso en curso en la página "Instalando". Antes quedaba la barra llena y sin texto varios minutos y parecía trabado.
procedure ShowStep(const Text, Detail: String);
begin
  Log('Paso: ' + Text);
  WizardForm.StatusLabel.Caption := Text;
  WizardForm.FilenameLabel.Caption := Detail;
  // Barra en movimiento: estos pasos no tienen un avance medible, pero se ve que el instalador sigue trabajando.
  WizardForm.ProgressGauge.Style := npbstMarquee;
end;

// Ejecuta un ayudante de installer\scripts (extraído a {tmp}) con PowerShell, oculto, y deja su salida en el registro de la instalación.
// Se pasa con -Command e Invoke-Expression (no con -File) para que no lo bloquee la directiva de ejecución de scripts del equipo.
// Mientras espera, el instalador sigue procesando los mensajes de Windows: la ventana se repinta y no queda "No responde".
function RunHelper(const Script, Variables: String; var Code: Integer): Boolean;
var
  Command: String;
begin
  Command := Variables + ' Invoke-Expression ([System.IO.File]::ReadAllText(' + PsText(ExpandConstant('{tmp}\' + Script)) + '))';
  try
    Result := ExecAndLogOutput(ExpandConstant('{sys}\WindowsPowerShell\v1.0\powershell.exe'),
      '-NoProfile -NonInteractive -Command "' + Command + '"', '', SW_HIDE, ewWaitUntilTerminated, Code, nil);
  except
    Log('No se pudo ejecutar ' + Script + ': ' + GetExceptionMessage);
    Result := False;
  end;
  if not Result then
    Code := -1;
end;

// Un programa a precalentar: 'ruta|argumentos' (lo separa precalentar.ps1).
function WarmupItem(const Exe, Args: String): String;
begin
  Result := PsText(Exe + '|' + Args);
end;

// Corre los programas con --precalentar, todos a la vez, con un límite de 15 minutos (igual que el actualizador, D17C-02). Windows
// Defender revisa ahora cada .exe y .dll nuevo sin firma, y no cuando Windows arranca el servicio (allí solo espera 30 s). Mejor
// esfuerzo: si falla o se agota el tiempo, queda en el registro y la instalación sigue.
procedure Warmup(const Items: String);
var
  Code: Integer;
begin
  ShowStep('Revisando los archivos con Windows Defender (la primera vez puede tardar varios minutos)…',
    'Así BusinessPost abre rápido después. No cierre esta ventana.');
  if RunHelper('precalentar.ps1', '$Minutos = 15; $Programas = @(' + Items + ');', Code) then
    Log('Precalentamiento terminado; programas que no alcanzaron: ' + IntToStr(Code))
  else
    Log('No se pudo precalentar; la instalación sigue.');
end;

// Espera hasta unos 5 minutos (15 rondas de 20 s) a que el programa responda en Url. Si encuentra el servicio detenido (por ejemplo,
// Windows se rindió a los 30 s), lo vuelve a iniciar, máximo 3 veces. Devuelve True si respondió. Si PowerShell no se puede usar en
// este equipo no hay cómo comprobarlo: devuelve True para abrir la página como antes.
function WaitForService(const Service, Url: String): Boolean;
var
  Attempt, Restarts, Code: Integer;
  CanRestart: String;
begin
  Result := False;
  Restarts := 0;
  for Attempt := 1 to 15 do
  begin
    if Attempt = 1 then
      ShowStep('Esperando a que BusinessPost responda…', 'Puede tardar hasta 5 minutos.')
    else
      ShowStep('Esperando a que BusinessPost responda…', Format('Lleva unos %d segundos. Puede tardar hasta 5 minutos.', [(Attempt - 1) * 20]));
    if Restarts < 3 then CanRestart := '$true' else CanRestart := '$false';
    if not RunHelper('esperar-servicio.ps1', '$Url = ' + PsText(Url) + '; $Servicio = ' + PsText(Service) + '; $Segundos = 20; ' +
      '$PuedeReiniciar = ' + CanRestart + ';', Code) then
    begin
      Log('No se pudo comprobar si ' + Service + ' responde; se abre la página igual.');
      Result := True;
      exit;
    end;
    case Code of
      0: begin
           Result := True;
           exit;
         end;
      2: Restarts := Restarts + 1;
      3: exit;
    end;
  end;
end;

// Abre la página con el usuario que inició el instalador (no como administrador).
procedure OpenPage(const Url: String);
var
  Code: Integer;
begin
  Log('Abriendo ' + Url);
  if not ShellExecAsOriginalUser('open', Url, '', '', SW_SHOWNORMAL, ewNoWait, Code) then
    ShellExec('open', Url, '', '', SW_SHOWNORMAL, ewNoWait, Code);
end;

procedure CurStepChanged(CurStep: TSetupStep);
var
  App, Data, Version, Migrator, Edition, Items, CodeFile: String;
  Code: Integer;
begin
  if CurStep <> ssPostInstall then
    exit;

  App := ExpandConstant('{app}');
  Data := ExpandConstant('{#DataDir}');
  // Se trabaja con la versión NUEVA por su carpeta (app\<versión>); la unión app\current se cambia recién cuando la base de datos quedó
  // lista (I4): si la actualización de la base falla, app\current sigue en la versión anterior y sus servicios vuelven a arrancar.
  Version := App + '\app\{#AppVersion}';
  ExtractTemporaryFile('precalentar.ps1');
  ExtractTemporaryFile('esperar-servicio.ps1');
  ForceDirectories(Data + '\config');

  // 0. La base de datos de una caja adicional que pasa a equipo principal estaba deshabilitada: se habilita ANTES de migrar (el migrador la
  //    inicia). Si la instalación falla, la vuelta atrás la deja deshabilitada otra vez (IN6). El resto del cambio de modo va después de
  //    que la base de datos quedó lista (ApplyModeChange).
  if IsServerMode and ServiceExists('{#Product}-DB') then
  begin
    ScOptional('config {#Product}-DB start= auto', 'habilitar la base de datos');
    DbReEnabled := PreviousMode = 2;
  end;

  // 1. Precalentar, antes de la base de datos (usa el migrador) y de crear los servicios. El actualizador es un solo .exe y no tiene
  //    --precalentar: basta con abrirlo una vez ("discover" de 1 s busca equipos en la red y termina).
  Items := WarmupItem(App + '\updater\Pos.Server.Updater.exe', 'discover --seconds 1');
  if IsServerMode then
    Items := Items + ', ' + WarmupItem(Version + '\server\Pos.Server.Host.exe', '--precalentar') +
      ', ' + WarmupItem(Version + '\migrator\Pos.Server.Migrator.exe', '--precalentar');
  if ModePage.SelectedValueIndex <> 1 then
    Items := Items + ', ' + WarmupItem(Version + '\agent\Pos.Terminal.Agent.exe', '--precalentar');
  // Ventana propia BusinessPost.exe (D13B-07): así abre rápido la primera vez que el dueño pulsa "Abrir BusinessPost".
  if FileExists(Version + '\desktop\BusinessPost.exe') then
    Items := Items + ', ' + WarmupItem(Version + '\desktop\BusinessPost.exe', '--precalentar');
  Warmup(Items);

  // 2. Base de datos (y recuperación, si se eligió un backup). En una actualización el migrador hace antes un backup PRE_UPDATE y, si
  //    una migración falla, lo restaura él mismo antes de que aquí se vuelva a la versión anterior (I4, IN4). Código 8 del migrador: no
  //    se pudo volver a los datos de antes; el servidor anterior NO se arranca y queda para restaurar con soporte.
  if IsServerMode then
  begin
    ShowStep('Creando la base de datos…', 'Prepara la base de datos de la tienda en este equipo. Puede tardar unos minutos.');
    if IsMulti then Edition := 'MULTI' else Edition := 'SINGLE';
    Migrator := Version + '\migrator\Pos.Server.Migrator.exe';
    Code := RunMigrator(Migrator, 'install --pg-bin "' + App + '\pgsql\bin" --data-root "' + Data + '" --edition ' + Edition +
      ' --license-server "{#LicenseServer}" --update-manifest "{#UpdateManifest}"', 'Base de datos');
    if Code = 8 then
    begin
      MarkFailed(ExitNeedsSupport, 'Restaurar con soporte');
      RollBack(False);
      if not Premium then
        MsgBox('La actualización de la base de datos falló y no se pudo volver automáticamente a los datos de antes.' + #13#10#13#10 +
          'NO use BusinessPost hasta que soporte restaure el backup previo (está en ' + Data + '\backups; el detalle quedó en el registro de ' +
          'la instalación y en ' + Data + '\logs). Genere un paquete de soporte y contacte a soporte.', mbCriticalError, MB_OK);
      WizardForm.ProgressGauge.Style := npbstNormal;
      exit;
    end;
    if Code <> 0 then
    begin
      MarkFailed(ExitDatabaseFailed, 'Base de datos');
      RollBack(True);
      if not Premium then
        MsgBox('No se pudo preparar la base de datos (código ' + IntToStr(Code) + '). BusinessPost sigue con la versión anterior y sus ' +
          'datos de antes.' + #13#10#13#10 + 'Revise el registro de la instalación y genere un paquete de soporte.', mbError, MB_OK);
      WizardForm.ProgressGauge.Style := npbstNormal;
      exit;
    end;
    if RecoveryFilePage.Values[0] <> '' then
    begin
      ShowStep('Recuperando los datos desde el backup…', 'Restaura el backup elegido y revisa que esté completo.');
      // I3: el código va en un archivo temporal que el migrador lee y BORRA; nunca en la línea de comandos ni en el registro.
      CodeFile := ExpandConstant('{tmp}\recuperacion-') + GetDateTimeString('yyyymmddhhnnss', #0, #0) + '.txt';
      SaveStringToFile(CodeFile, RecoveryPage.Values[0], False);
      Code := RunMigrator(Migrator, 'restore --file "' + RecoveryFilePage.Values[0] + '" --recovery-code-file "' + CodeFile + '" --data-root "' +
        Data + '" --yes', 'Recuperación desde el backup');
      DeleteFile(CodeFile);
      if Code <> 0 then
      begin
        // I8: sin los datos del backup no se sigue con una base vacía (antes se abría /instalacion como si fuera una tienda nueva). La
        // restauración no tocó la BD activa: en una reinstalación vuelven a arrancar los servicios que había.
        MarkFailed(ExitDatabaseFailed, 'Recuperación desde el backup');
        RollBack(True);
        if not Premium then
          MsgBox('No se pudo recuperar el backup (revise el archivo y el código de recuperación). BusinessPost NO quedó listo: ' +
            'vuelva a ejecutar el instalador con el backup y el código correctos, o genere un paquete de soporte.', mbError, MB_OK);
        WizardForm.ProgressGauge.Style := npbstNormal;
        exit;
      end;
    end;
  end;

  // Versión activa (IN9): si no se pudo cambiar app\current no se registran servicios apuntando a una ruta que no existe. La base de datos
  // ya quedó en la versión nueva: no se arranca el servidor anterior; volver a ejecutar el instalador termina el cambio.
  if not ActivateVersion(App, Version) then
  begin
    MarkFailed(ExitActivationFailed, 'Versión activa');
    RollBack(not IsServerMode);
    if not Premium then
      MsgBox('No se pudo activar la versión nueva de BusinessPost (app\current). Reinicie el computador y vuelva a ejecutar el instalador; ' +
        'si el problema sigue, genere un paquete de soporte.', mbError, MB_OK);
    WizardForm.ProgressGauge.Style := npbstNormal;
    exit;
  end;

  // Cambio de modo (I5, IN6): recién ahora, con la base de datos lista y la versión nueva activa.
  ApplyModeChange(Data);
  // Ya no hay vuelta atrás: la copia del actualizador anterior (IN5) sobra.
  DeleteFile(UpdaterFile + '.anterior');

  // 3. Servicios de Windows (mantienen BusinessPost funcionando aunque se cierre la ventana).
  ShowStep('Registrando los servicios de BusinessPost…', 'Así BusinessPost se inicia solo cada vez que se enciende el equipo.');
  if IsServerMode then
  begin
    CreateService('{#Product}-Server', App + '\app\current\server\Pos.Server.Host.exe', '{#Product}-DB');
    if IsMulti then
      AddFirewallRules;
  end
  else
  begin
    // Caja: el actualizador se actualiza desde el servidor de la tienda con la huella fijada (D13-09).
    // El agente de impresión acepta la interfaz servida por el servidor (CORS): el ORIGEN exacto (sin ruta ni barra final).
    SaveStringToFile(Data + '\config\agent.json', '{ "Agent": { "AllowedOrigins": [ "http://localhost:5480", "' +
      JsonText(OriginOf(TerminalPage.Values[0])) + '" ] } }', False);
    SaveStringToFile(Data + '\config\updater.json', '{ "Pos": { "Updates": { "Mode": "Terminal", "ServerUrl": "' + JsonText(WithSlash(TerminalPage.Values[0])) +
      '", "ServerCertificateThumbprint": "' + JsonText(Trim(TerminalPage.Values[1])) + '" } } }', False);
  end;

  if ModePage.SelectedValueIndex <> 1 then
    CreateService('{#Product}-TerminalAgent', App + '\app\current\agent\Pos.Terminal.Agent.exe', '');
  CreateService('{#Product}-Updater', App + '\updater\Pos.Server.Updater.exe', '');

  // 4. Iniciar los servicios.
  ShowStep('Iniciando BusinessPost…', '');
  if IsServerMode then
    StartService('{#Product}-Server');
  if ModePage.SelectedValueIndex <> 1 then
    StartService('{#Product}-TerminalAgent');
  StartService('{#Product}-Updater');

  // 5. Esperar a que responda y solo entonces abrir el navegador (antes: "localhost rechazó la conexión").
  if Premium then
  begin
    // Instalador premium: él configura la tienda y abre BusinessPost; aquí solo se deja el resultado en el registro.
    if IsServerMode then
    begin
      if WaitForService('{#Product}-Server', 'http://localhost:5480/health/live') then
        Log('Resultado: OK')
      else
        Log('Resultado: SIN_RESPUESTA');
    end
    else if WaitForService('{#Product}-TerminalAgent', 'http://localhost:5490/status') then
      Log('Resultado: OK')
    else
      Log('Resultado: SIN_RESPUESTA');
  end
  else if IsServerMode then
  begin
    if PublicNetwork then
      MsgBox('La red de este equipo está marcada como "Pública": Windows no deja que las demás cajas se conecten a este equipo principal.' + #13#10#13#10 +
        'En Configuración → Red e Internet → (su red) → Propiedades, elija "Red privada".', mbInformation, MB_OK);
    if WaitForService('{#Product}-Server', 'http://localhost:5480/health/live') then
      OpenPage('http://localhost:5480/instalacion')
    else
      MsgBox('BusinessPost quedó instalado, pero todavía no responde. Windows puede seguir revisando los archivos nuevos.' + #13#10#13#10 +
        'Qué hacer:' + #13#10 +
        '1. Espere unos minutos y abra el acceso "BusinessPost" del escritorio.' + #13#10 +
        '2. Si no abre, reinicie el computador: BusinessPost se inicia solo al encenderlo.' + #13#10 +
        '3. Si aun así no abre, busque en el menú Inicio "BusinessPost" → "Paquete de soporte" y envíe a soporte el archivo que queda ' +
        'en el escritorio.', mbInformation, MB_OK);
  end
  else
  begin
    // Caja adicional: el agente de impresión corre en este equipo; la interfaz la sirve el equipo principal.
    if not WaitForService('{#Product}-TerminalAgent', 'http://localhost:5490/status') then
      MsgBox('La caja quedó instalada, pero el programa de impresión de este equipo todavía no responde.' + #13#10#13#10 +
        'Puede vender igual. Si al cobrar no imprime, reinicie el computador; si sigue sin imprimir, contacte a soporte.',
        mbInformation, MB_OK);
    OpenPage(WithSlash(TerminalPage.Values[0]));
  end;
  WizardForm.ProgressGauge.Style := npbstNormal;
end;

// Desinstalar CONSERVA los datos y los backups (D13-12). Borrarlos exige dos confirmaciones.
function InitializeUninstall: Boolean;
begin
  Result := True;
  DeleteData := False;
  // Desde el desinstalador premium (en silencio): la doble confirmación ya se hizo allí.
  if UninstallSilent then
  begin
    DeleteData := Lowercase(ExpandConstant('{param:BORRARDATOS|no}')) = 'si';
    exit;
  end;
  if MsgBox('¿Desea BORRAR también los datos de la tienda (ventas, inventario, clientes) y los backups de este equipo?' + #13#10 +
    'Si elige No, se conservan en ' + ExpandConstant('{#DataDir}') + ' y una reinstalación los reutiliza.', mbConfirmation, MB_YESNO or MB_DEFBUTTON2) = IDYES then
    if MsgBox('ÚLTIMA CONFIRMACIÓN: se borrarán definitivamente los datos y los backups de este equipo. Haga antes una copia en un disco externo.' + #13#10 +
      '¿Borrar los datos?', mbError, MB_YESNO or MB_DEFBUTTON2) = IDYES then
      DeleteData := True;
end;

// I9: antes de quitar archivos se DETIENEN los servicios y se espera a que queden detenidos (sc stop no esperaba: PostgreSQL seguía
// cerrándose mientras se borraban pgsql y app, y quedaban restos).
procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  Code: Integer;
begin
  if CurUninstallStep = usUninstall then
  begin
    // IN1: la ruta había perdido las barras invertidas (y "\v" se había vuelto un carácter de control): PowerShell no se ejecutaba y los
    // servicios no se detenían antes de borrar. RevisionInstaladorTests revisa que no vuelva a pasar.
    if not Exec(ExpandConstant('{sys}\WindowsPowerShell\v1.0\powershell.exe'),
      '-NoProfile -NonInteractive -Command "' + StopServicesCommand(90, ExpandConstant('{app}')) + '"',
      '', SW_HIDE, ewWaitUntilTerminated, Code) or (Code <> 0) then
      Log('Aviso: algún servicio de BusinessPost no se detuvo a tiempo (código ' + IntToStr(Code) + '); pueden quedar archivos hasta reiniciar.');
  end;
  if (CurUninstallStep = usPostUninstall) and DeleteData then
    DelTree(ExpandConstant('{#DataDir}'), True, True, True);
end;
