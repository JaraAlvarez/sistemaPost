; =====================================================================================================================================
; Instalador de PosSupermercado (Fase 13, docs/fases/fase-13-propuesta.md, ADR-0055 y ADR-0056).
; Se compila con tools/scripts/build-installer.ps1 (Inno Setup 6): no lo compile a mano, el script prepara artifacts\package.
;
; Modos: Todo en uno (Caja Única) · Servidor (Multicaja) · Caja (Multicaja).
; Deja: C:\Program Files\PosSupermercado\app\<versión>\{server,migrator,agent}, app\current (unión NTFS a la versión activa),
;       updater\ y pgsql\ (PostgreSQL 18 empaquetado). Datos, backups y configuración en C:\ProgramData\PosSupermercado (se CONSERVAN al
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
#define Product "PosSupermercado"
#define DataDir "{commonappdata}\PosSupermercado"

[Setup]
AppId={{6F2B3C9E-8C3A-4E7B-9E1D-2C5A7B0F4D11}
AppName=POS Supermercado
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
OutputBaseFilename=PosSupermercado-Setup-{#AppVersion}
UninstallDisplayName=POS Supermercado
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

[Icons]
Name: "{commondesktop}\POS Supermercado"; Filename: "{code:StartUrl}"; IconFilename: "{app}\updater\Pos.Server.Updater.exe"
Name: "{commonprograms}\POS Supermercado\POS Supermercado"; Filename: "{code:StartUrl}"
Name: "{commonprograms}\POS Supermercado\Paquete de soporte"; Filename: "{app}\app\current\migrator\Pos.Server.Migrator.exe"; Parameters: "support-bundle"; Check: IsServerMode

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

procedure InitializeWizard;
begin
  ModePage := CreateInputOptionPage(wpWelcome, 'Tipo de instalación', '¿Cómo se usará este computador?',
    'Elija una opción. Si la tienda tiene una sola caja, elija "Todo en uno".', True, False);
  ModePage.Add('Todo en uno (Caja Única): este computador guarda los datos y es la caja');
  ModePage.Add('Servidor (Multicaja): este computador guarda los datos; las cajas se conectan a él por la red');
  ModePage.Add('Caja (Multicaja): este computador es una caja; los datos están en el servidor de la tienda');
  ModePage.SelectedValueIndex := 0;

  RecoveryPage := CreateInputQueryPage(ModePage.ID, 'Recuperar desde un backup (opcional)',
    'Solo si este computador reemplaza a uno dañado',
    'Si es una instalación nueva, deje todo en blanco y pulse Siguiente. Para recuperar, elija el archivo .posbak en la siguiente página ' +
    'y escriba aquí el código de recuperación del propietario (24 caracteres).');
  RecoveryPage.Add('Código de recuperación:', False);
  RecoveryFilePage := CreateInputFilePage(RecoveryPage.ID, 'Archivo del backup', 'Backup a restaurar (opcional)',
    'Deje en blanco para una instalación nueva.');
  RecoveryFilePage.Add('Archivo .posbak:', 'Backups (*.posbak)|*.posbak', '.posbak');

  TerminalPage := CreateInputQueryPage(RecoveryFilePage.ID, 'Servidor de la tienda', 'Dirección del servidor Multicaja',
    'Se buscó el servidor en la red. Si no aparece, escriba la dirección que muestra la administración del servidor ' +
    '(por ejemplo https://192.168.1.10:5443/) y la huella del certificado para verificarlo.');
  TerminalPage.Add('Dirección del servidor:', False);
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

// Caja: busca el servidor en la LAN con el actualizador ("discover") y propone el primero encontrado.
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
    MsgBox('Escriba la dirección del servidor y la huella de su certificado (las muestra la administración del servidor).', mbError, MB_OK);
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

procedure CurStepChanged(CurStep: TSetupStep);
var
  App, Data, Migrator, Edition: String;
  Code: Integer;
begin
  if CurStep <> ssPostInstall then
    exit;

  App := ExpandConstant('{app}');
  Data := ExpandConstant('{#DataDir}');
  // Versión activa: unión NTFS app\current → app\<versión> (ADR-0056); los servicios apuntan a app\current.
  Exec(ExpandConstant('{cmd}'), '/c rmdir "' + App + '\app\current"', '', SW_HIDE, ewWaitUntilTerminated, Code);
  Run(ExpandConstant('{cmd}'), '/c mklink /J "' + App + '\app\current" "' + App + '\app\{#AppVersion}"', 'Versión activa');
  SaveStringToFile(App + '\app\current.json', '{"version":"{#AppVersion}"}', False);
  ForceDirectories(Data + '\config');

  if IsServerMode then
  begin
    if IsMulti then Edition := 'MULTI' else Edition := 'SINGLE';
    Migrator := App + '\app\current\migrator\Pos.Server.Migrator.exe';
    if not Run(Migrator, 'install --pg-bin "' + App + '\pgsql\bin" --data-root "' + Data + '" --edition ' + Edition +
      ' --license-server "{#LicenseServer}" --update-manifest "{#UpdateManifest}"', 'Base de datos') then
      exit;
    if RecoveryFilePage.Values[0] <> '' then
      Run(Migrator, 'restore --file "' + RecoveryFilePage.Values[0] + '" --recovery-code "' + RecoveryPage.Values[0] + '" --data-root "' + Data + '" --yes',
        'Recuperación desde el backup');
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
    SaveStringToFile(Data + '\config\updater.json', '{ "Pos": { "Updates": { "Mode": "Terminal", "ServerUrl": "' + TerminalPage.Values[0] +
      '", "ServerCertificateThumbprint": "' + TerminalPage.Values[1] + '" } } }', False);
  end;

  if ModePage.SelectedValueIndex <> 1 then
    CreateService('{#Product}-TerminalAgent', App + '\app\current\agent\Pos.Terminal.Agent.exe', '');
  CreateService('{#Product}-Updater', App + '\updater\Pos.Server.Updater.exe', '');

  Exec(ExpandConstant('{sys}\sc.exe'), 'start {#Product}-Server', '', SW_HIDE, ewWaitUntilTerminated, Code);
  Exec(ExpandConstant('{sys}\sc.exe'), 'start {#Product}-TerminalAgent', '', SW_HIDE, ewWaitUntilTerminated, Code);
  Exec(ExpandConstant('{sys}\sc.exe'), 'start {#Product}-Updater', '', SW_HIDE, ewWaitUntilTerminated, Code);

  if IsServerMode then
    ShellExec('open', 'http://localhost:5480/instalacion', '', '', SW_SHOWNORMAL, ewNoWait, Code);
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
