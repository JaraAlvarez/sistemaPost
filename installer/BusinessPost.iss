; =====================================================================================================================================
; Instalador de BusinessPost (Fase 13, docs/fases/fase-13-propuesta.md, ADR-0055 y ADR-0056).
; Se compila con tools/scripts/build-installer.ps1 (Inno Setup 6): no lo compile a mano, el script prepara artifacts\package.
;
; Modos (nombres claros, Fase 13-B §6.2): "Este equipo es la única caja" (Caja Única) · "Este es el equipo principal" (Multicaja;
;       internamente "servidor") · "Esta es una caja adicional" (Multicaja; internamente "caja").
; Primer arranque (Fase 13-B, D13B-01 y D13B-02): precalienta los programas (Windows Defender) antes de crear los servicios, muestra el
;       paso en curso y espera a que BusinessPost responda antes de abrir el navegador.
; Deja: C:\Program Files\BusinessPost\app\<versión>\{server,migrator,agent}, app\current (unión NTFS a la versión activa),
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
OutputDir=..\artifacts\releases
OutputBaseFilename=BusinessPost-Setup-{#AppVersion}
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
; Ayudantes del primer arranque (Fase 13-B): no se instalan; se usan desde la carpeta temporal durante la instalación.
Source: "scripts\precalentar.ps1";      Flags: dontcopy
Source: "scripts\esperar-servicio.ps1"; Flags: dontcopy

[Icons]
; La interfaz se abre en Edge en modo aplicación (Fase 15, D15-03): sin barra de direcciones ni pestañas, como un programa.
Name: "{commondesktop}\BusinessPost"; Filename: "{code:EdgePath}"; Parameters: "--app={code:StartUrl}"; IconFilename: "{app}\updater\Pos.Server.Updater.exe"
Name: "{commonprograms}\BusinessPost\BusinessPost"; Filename: "{code:EdgePath}"; Parameters: "--app={code:StartUrl}"
Name: "{commonprograms}\BusinessPost\Paquete de soporte"; Filename: "{app}\app\current\migrator\Pos.Server.Migrator.exe"; Parameters: "support-bundle"; Check: IsServerMode

[UninstallRun]
Filename: "{sys}\sc.exe"; Parameters: "stop {#Product}-Updater"; Flags: runhidden; RunOnceId: "StopUpdater"
Filename: "{sys}\sc.exe"; Parameters: "stop {#Product}-TerminalAgent"; Flags: runhidden; RunOnceId: "StopAgent"
Filename: "{sys}\sc.exe"; Parameters: "stop {#Product}-Server"; Flags: runhidden; RunOnceId: "StopServer"
Filename: "{sys}\sc.exe"; Parameters: "delete {#Product}-Updater"; Flags: runhidden; RunOnceId: "DelUpdater"
Filename: "{sys}\sc.exe"; Parameters: "delete {#Product}-TerminalAgent"; Flags: runhidden; RunOnceId: "DelAgent"
Filename: "{sys}\sc.exe"; Parameters: "delete {#Product}-Server"; Flags: runhidden; RunOnceId: "DelServer"
Filename: "{sys}\sc.exe"; Parameters: "stop {#Product}-DB"; Flags: runhidden; RunOnceId: "StopDb"
Filename: "{app}\pgsql\bin\pg_ctl.exe"; Parameters: "unregister -N {#Product}-DB"; Flags: runhidden skipifdoesntexist; RunOnceId: "DelDb"
Filename: "{sys}\netsh.exe"; Parameters: "advfirewall firewall delete rule name=""{#Product} LAN"""; Flags: runhidden; RunOnceId: "DelFirewall"
Filename: "{cmd}"; Parameters: "/c rmdir ""{app}\app\current"""; Flags: runhidden; RunOnceId: "DelJunction"

[Code]
var
  ModePage: TInputOptionWizardPage;
  RecoveryPage: TInputQueryWizardPage;
  RecoveryFilePage: TInputFileWizardPage;
  TerminalPage: TInputQueryWizardPage;
  DeleteData: Boolean;

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

function Run(const FileName, Params, Step: String): Boolean;
var
  ResultCode: Integer;
begin
  Log(Step + ': ' + FileName + ' ' + Params);
  Result := Exec(FileName, Params, '', SW_HIDE, ewWaitUntilTerminated, ResultCode) and (ResultCode = 0);
  if not Result then
    MsgBox(Step + ' falló (código ' + IntToStr(ResultCode) + '). Revise el registro de la instalación y genere un paquete de soporte.', mbError, MB_OK);
end;

// Inicio "auto" (no "delayed-auto") para todos: con los programas ya precalentados el arranque al encender el equipo es rápido, y un
// inicio retrasado haría esperar unos 2 minutos a la caja cada mañana. Si el servicio no alcanza a iniciar, la espera del final de la
// instalación lo vuelve a iniciar (WaitForService) y, si se cae después, Windows lo reinicia solo (acciones de recuperación).
procedure CreateService(const Name, Exe, Params: String);
var
  ResultCode: Integer;
begin
  Exec(ExpandConstant('{sys}\sc.exe'), 'stop ' + Name, '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
  Exec(ExpandConstant('{sys}\sc.exe'), 'delete ' + Name, '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
  Run(ExpandConstant('{sys}\sc.exe'), 'create ' + Name + ' start= auto binPath= "\"' + Exe + '\"' + Params + '"', 'Servicio ' + Name);
  // Recuperación automática ante fallos: reinicia a los 5 s (tres veces), el contador se reinicia cada día.
  Exec(ExpandConstant('{sys}\sc.exe'), 'failure ' + Name + ' reset= 86400 actions= restart/5000/restart/5000/restart/5000', '', SW_HIDE,
    ewWaitUntilTerminated, ResultCode);
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

// Texto entre comillas simples para PowerShell (duplica las comillas simples internas).
function PsText(const Value: String): String;
begin
  Result := Value;
  StringChangeEx(Result, '''', '''''', True);
  Result := '''' + Result + '''';
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

procedure StartService(const Name: String);
var
  Code: Integer;
begin
  Exec(ExpandConstant('{sys}\sc.exe'), 'start ' + Name, '', SW_HIDE, ewWaitUntilTerminated, Code);
  Log('sc start ' + Name + ': código ' + IntToStr(Code));
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
  App, Data, Migrator, Edition, Items: String;
  Code: Integer;
begin
  if CurStep <> ssPostInstall then
    exit;

  App := ExpandConstant('{app}');
  Data := ExpandConstant('{#DataDir}');
  ExtractTemporaryFile('precalentar.ps1');
  ExtractTemporaryFile('esperar-servicio.ps1');
  // Versión activa: unión NTFS app\current → app\<versión> (ADR-0056); los servicios apuntan a app\current.
  Exec(ExpandConstant('{cmd}'), '/c rmdir "' + App + '\app\current"', '', SW_HIDE, ewWaitUntilTerminated, Code);
  Run(ExpandConstant('{cmd}'), '/c mklink /J "' + App + '\app\current" "' + App + '\app\{#AppVersion}"', 'Versión activa');
  SaveStringToFile(App + '\app\current.json', '{"version":"{#AppVersion}"}', False);
  ForceDirectories(Data + '\config');

  // 1. Precalentar, antes de la base de datos (usa el migrador) y de crear los servicios. El actualizador es un solo .exe y no tiene
  //    --precalentar: basta con abrirlo una vez ("discover" de 1 s busca equipos en la red y termina).
  Items := WarmupItem(App + '\updater\Pos.Server.Updater.exe', 'discover --seconds 1');
  if IsServerMode then
    Items := Items + ', ' + WarmupItem(App + '\app\current\server\Pos.Server.Host.exe', '--precalentar') +
      ', ' + WarmupItem(App + '\app\current\migrator\Pos.Server.Migrator.exe', '--precalentar');
  if ModePage.SelectedValueIndex <> 1 then
    Items := Items + ', ' + WarmupItem(App + '\app\current\agent\Pos.Terminal.Agent.exe', '--precalentar');
  Warmup(Items);

  // 2. Base de datos (y recuperación, si se eligió un backup).
  if IsServerMode then
  begin
    ShowStep('Creando la base de datos…', 'Prepara la base de datos de la tienda en este equipo. Puede tardar unos minutos.');
    if IsMulti then Edition := 'MULTI' else Edition := 'SINGLE';
    Migrator := App + '\app\current\migrator\Pos.Server.Migrator.exe';
    if not Run(Migrator, 'install --pg-bin "' + App + '\pgsql\bin" --data-root "' + Data + '" --edition ' + Edition +
      ' --license-server "{#LicenseServer}" --update-manifest "{#UpdateManifest}"', 'Base de datos') then
      exit;
    if RecoveryFilePage.Values[0] <> '' then
    begin
      ShowStep('Recuperando los datos desde el backup…', 'Restaura el backup elegido y revisa que esté completo.');
      Run(Migrator, 'restore --file "' + RecoveryFilePage.Values[0] + '" --recovery-code "' + RecoveryPage.Values[0] + '" --data-root "' + Data + '" --yes',
        'Recuperación desde el backup');
    end;
  end;

  // 3. Servicios de Windows (mantienen BusinessPost funcionando aunque se cierre la ventana).
  ShowStep('Registrando los servicios de BusinessPost…', 'Así BusinessPost se inicia solo cada vez que se enciende el equipo.');
  if IsServerMode then
  begin
    CreateService('{#Product}-Server', App + '\app\current\server\Pos.Server.Host.exe', '');
    if IsMulti then
    begin
      Run(ExpandConstant('{sys}\netsh.exe'), 'advfirewall firewall add rule name="{#Product} LAN" dir=in action=allow protocol=TCP localport=5443 profile=private',
        'Firewall (HTTPS)');
      Run(ExpandConstant('{sys}\netsh.exe'), 'advfirewall firewall add rule name="{#Product} LAN" dir=in action=allow protocol=UDP localport=5444 profile=private',
        'Firewall (descubrimiento)');
    end;
  end
  else
  begin
    // Caja: el actualizador se actualiza desde el servidor de la tienda con la huella fijada (D13-09).
    // El agente de impresión acepta la interfaz servida por el servidor (CORS).
    SaveStringToFile(Data + '\config\agent.json', '{ "Agent": { "AllowedOrigins": [ "http://localhost:5480", "' +
      Copy(TerminalPage.Values[0], 1, Length(TerminalPage.Values[0]) - 1) + '" ] } }', False);
    SaveStringToFile(Data + '\config\updater.json', '{ "Pos": { "Updates": { "Mode": "Terminal", "ServerUrl": "' + TerminalPage.Values[0] +
      '", "ServerCertificateThumbprint": "' + TerminalPage.Values[1] + '" } } }', False);
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
  if IsServerMode then
  begin
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
    OpenPage(TerminalPage.Values[0]);
  end;
  WizardForm.ProgressGauge.Style := npbstNormal;
end;

// Desinstalar CONSERVA los datos y los backups (D13-12). Borrarlos exige dos confirmaciones.
function InitializeUninstall: Boolean;
begin
  Result := True;
  DeleteData := False;
  if MsgBox('¿Desea BORRAR también los datos de la tienda (ventas, inventario, clientes) y los backups de este equipo?' + #13#10 +
    'Si elige No, se conservan en ' + ExpandConstant('{#DataDir}') + ' y una reinstalación los reutiliza.', mbConfirmation, MB_YESNO or MB_DEFBUTTON2) = IDYES then
    if MsgBox('ÚLTIMA CONFIRMACIÓN: se borrarán definitivamente los datos y los backups de este equipo. Haga antes una copia en un disco externo.' + #13#10 +
      '¿Borrar los datos?', mbError, MB_YESNO or MB_DEFBUTTON2) = IDYES then
      DeleteData := True;
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
begin
  if (CurUninstallStep = usPostUninstall) and DeleteData then
    DelTree(ExpandConstant('{#DataDir}'), True, True, True);
end;
