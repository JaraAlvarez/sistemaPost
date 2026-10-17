; =====================================================================================================================================
; Instalador descargable de BusinessPost (Fase 13-B, D13B-08): UN solo archivo BusinessPost-Setup-<versión>.exe.
; Se compila con tools/scripts/build-installer.ps1 DESPUÉS del paquete clásico (BusinessPost.iss → BusinessPost-Clasico-<versión>.exe) y
; del instalador premium (src/Installer/Pos.Installer.Setup → BusinessPost-Setup.exe).
;
; Qué hace: pide permisos de administrador UNA vez (Windows muestra un solo aviso), muestra "Preparando el instalador…", saca los dos
; programas a su carpeta temporal y abre el instalador premium (ventana propia con la marca), que ejecuta el paquete clásico en
; silencio. Si el equipo no tiene WebView2 (Windows muy desactualizado), abre el asistente clásico de siempre. No se instala a sí mismo
; (no aparece en "Aplicaciones instaladas"): lo que se instala es BusinessPost, con el paquete clásico.
; =====================================================================================================================================

#ifndef AppVersion
  #define AppVersion "0.1.0"
#endif
#ifndef PremiumExe
  #define PremiumExe "..\artifacts\premium\BusinessPost-Setup.exe"
#endif
#ifndef ClassicSetup
  #define ClassicSetup "..\artifacts\releases\BusinessPost-Clasico-" + AppVersion + ".exe"
#endif

[Setup]
; AppId propio (distinto del de BusinessPost): este programa solo lanza el instalador, no se registra.
AppId={{6C0B5E57-7E3B-4C8C-9A55-7B1F7F0B13B8}
AppName=BusinessPost
AppVersion={#AppVersion}
AppPublisher=BusinessPost
VersionInfoDescription=Instalador de BusinessPost
CreateAppDir=no
Uninstallable=no
PrivilegesRequired=admin
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.19045
DisableWelcomePage=yes
DisableReadyPage=yes
DisableFinishedPage=yes
; Los dos programas ya vienen comprimidos: guardarlos sin volver a comprimir hace que salgan en segundos.
Compression=none
OutputDir=..\artifacts\releases
OutputBaseFilename=BusinessPost-Setup-{#AppVersion}
SetupIconFile=..\src\Desktop\Pos.Desktop\BusinessPost.ico
SetupLogging=no

[Languages]
Name: "es"; MessagesFile: "compiler:Languages\Spanish.isl"

[Files]
Source: "{#PremiumExe}";   DestName: "BusinessPost-Setup.exe";   Flags: dontcopy nocompression
Source: "{#ClassicSetup}"; DestName: "BusinessPost-Paquete.exe"; Flags: dontcopy nocompression

[Code]
const
  SinWebView2 = 3;

// Ventana pequeña con la marca mientras se sacan los archivos (unos segundos; en discos lentos, más).
function MostrarPreparando: TSetupForm;
var
  Texto: TNewStaticText;
begin
  Result := CreateCustomForm(ScaleX(380), ScaleY(110), False, True);
  Result.Caption := 'BusinessPost';
  Result.Color := $007D1511; // #11157D
  Texto := TNewStaticText.Create(Result);
  Texto.Parent := Result;
  Texto.Caption := 'Preparando el instalador de BusinessPost…';
  Texto.Font.Color := $FFFFFF;
  Texto.Font.Size := 11;
  Texto.Left := ScaleX(24);
  Texto.Top := ScaleY(40);
  Result.Show;
  Result.Refresh;
end;

function InitializeSetup: Boolean;
var
  Preparando: TSetupForm;
  Codigo: Integer;
  Premium, Paquete: String;
begin
  // Nunca sigue con el asistente de este programa: todo lo hace el instalador premium (o el clásico).
  Result := False;
  Preparando := MostrarPreparando;
  try
    ExtractTemporaryFile('BusinessPost-Setup.exe');
    ExtractTemporaryFile('BusinessPost-Paquete.exe');
  finally
    Preparando.Free;
  end;

  Premium := ExpandConstant('{tmp}\BusinessPost-Setup.exe');
  Paquete := ExpandConstant('{tmp}\BusinessPost-Paquete.exe');
  // Ya tiene permisos de administrador: el instalador premium y el paquete los heredan sin otro aviso de Windows.
  if not Exec(Premium, '--paquete "' + Paquete + '"', '', SW_SHOWNORMAL, ewWaitUntilTerminated, Codigo) or (Codigo = SinWebView2) then
    // Respaldo: el asistente clásico de siempre.
    Exec(Paquete, '', '', SW_SHOWNORMAL, ewWaitUntilTerminated, Codigo);
end;
