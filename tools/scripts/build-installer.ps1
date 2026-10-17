<#
.SYNOPSIS
    Arma una versión para clientes (Fase 13): publica el servidor, el migrador, el agente de caja y el actualizador; agrega PostgreSQL 18;
    crea el paquete de actualización (ZIP), firma su manifiesto y compila el instalador con Inno Setup.
.DESCRIPTION
    Salida en artifacts\releases:
      BusinessPost-<versión>.zip          paquete de actualización (server, migrator, agent)
      <canal>.json                        manifiesto firmado (si se pasa -SigningKey)
      BusinessPost-Setup-<versión>.exe    instalador DESCARGABLE (si Inno Setup 6 está instalado): un solo archivo con el instalador
                                          premium (ventana propia con la marca, Fase 13-B D13B-08) y el paquete clásico adentro
      BusinessPost-Clasico-<versión>.exe  paquete clásico (asistente de Inno): lo usa el premium en silencio; sirve también solo
                                          (soporte, equipos sin WebView2)
    Suba el ZIP y el manifiesto a la carpeta updates/ del VPS (docs/despliegue-nube.md §16).
.PARAMETER PostgresZip
    ZIP oficial de binarios de PostgreSQL 18 para Windows x64 (https://www.enterprisedb.com/download-postgresql-binaries). Se incluye sin
    pgAdmin, StackBuilder ni documentación.
.PARAMETER CodeSigningThumbprint
    Huella (thumbprint) del certificado de firma de código (Authenticode) instalado en el almacén del usuario o del equipo. Con él se
    firman los .exe y .dll propios y el instalador. Sin firma, Windows Defender retiene cada .exe nuevo mientras lo revisa en la nube:
    el primer arranque después de instalar o actualizar puede tardar varios minutos y superar los 30 s que Windows da a un servicio
    (ver docs/guia-instalacion.md §8). Muy recomendado para clientes.
.PARAMETER TimestampUrl
    Servidor de sello de tiempo RFC 3161 del emisor del certificado (la firma sigue válida cuando el certificado vence).
.EXAMPLE
    ./tools/scripts/build-installer.ps1 -Version 1.0.0 -PostgresZip C:\descargas\postgresql-18.1-1-windows-x64-binaries.zip `
        -LicenseServer https://businesspost.tutiendanueva.com/ -UpdateBaseUrl https://businesspost.tutiendanueva.com/updates/ -SigningKey D:\claves\actualizaciones.pem
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)] [string] $Version,
    [Parameter(Mandatory)] [string] $PostgresZip,
    [string] $LicenseServer = '',
    [string] $UpdateBaseUrl = '',
    [ValidateSet('stable', 'beta', 'internal')] [string] $Channel = 'stable',
    [string] $SigningKey,
    [string] $Notes = '',
    [string] $CodeSigningThumbprint,
    [string] $TimestampUrl = 'http://timestamp.digicert.com'
)

$ErrorActionPreference = 'Stop'
$root = Resolve-Path (Join-Path $PSScriptRoot '..\..')
$package = Join-Path $root 'artifacts\package'
$releases = Join-Path $root 'artifacts\releases'
$app = Join-Path $package "app\$Version"

if ($Version -notmatch '^\d+\.\d+\.\d+$') { throw 'La versión debe ser MAYOR.MENOR.PARCHE (p. ej. 1.0.0).' }
if (-not (Test-Path $PostgresZip)) { throw "No existe $PostgresZip." }
Remove-Item $package -Recurse -Force -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Force $app, $releases | Out-Null

function Publish([string] $project, [string] $output, [switch] $SingleFile, [string[]] $Extra = @()) {
    $arguments = @('publish', (Join-Path $root $project), '-c', 'Release', '-r', 'win-x64', '--self-contained', 'true',
        '-p:PublishReadyToRun=true', "-p:Version=$Version", '-o', $output, '--nologo', '-v', 'q') + $Extra
    if ($SingleFile) { $arguments += @('-p:PublishSingleFile=true', '-p:IncludeNativeLibrariesForSelfExtract=true') }
    & dotnet @arguments
    if ($LASTEXITCODE -ne 0) { throw "Falló la publicación de $project." }
}

Write-Host "Publicando $Version (autocontenido, ReadyToRun)..." -ForegroundColor Cyan
Publish 'src\Server\Pos.Server.Host\Pos.Server.Host.csproj' (Join-Path $app 'server')
Publish 'src\Server\Pos.Server.Migrator\Pos.Server.Migrator.csproj' (Join-Path $app 'migrator')
Publish 'src\Terminal\Pos.Terminal.Agent\Pos.Terminal.Agent.csproj' (Join-Path $app 'agent')
Publish 'src\Server\Pos.Server.Updater\Pos.Server.Updater.csproj' (Join-Path $package 'updater') -SingleFile
# Ventana propia BusinessPost.exe (Fase 13-B, D13B-07): un solo .exe autocontenido; el instalador lo copia a {app}\desktop.
Publish 'src\Desktop\Pos.Desktop\Pos.Desktop.csproj' (Join-Path $package 'desktop') -SingleFile

# Firma de código (Authenticode): los ejecutables propios firmados no quedan retenidos por Windows Defender en el primer arranque.
function Sign([string[]] $files) {
    if (-not $CodeSigningThumbprint -or $files.Count -eq 0) { return }
    $signtool = Get-ChildItem "${env:ProgramFiles(x86)}\Windows Kits\10\bin\*\x64\signtool.exe" -ErrorAction SilentlyContinue |
        Sort-Object FullName -Descending | Select-Object -First 1
    if (-not $signtool) { throw 'No se encontró signtool.exe (instale el Windows SDK) para firmar con -CodeSigningThumbprint.' }
    foreach ($group in ($files | Group-Object { [math]::Floor([array]::IndexOf($files, $_) / 50) })) {
        & $signtool.FullName sign /sha1 $CodeSigningThumbprint /fd SHA256 /tr $TimestampUrl /td SHA256 @($group.Group)
        if ($LASTEXITCODE -ne 0) { throw 'Falló la firma de código.' }
    }
}

if ($CodeSigningThumbprint) {
    Write-Host 'Firmando los ejecutables propios (Authenticode)...' -ForegroundColor Cyan
    $own = Get-ChildItem $app, (Join-Path $package 'updater'), (Join-Path $package 'desktop') -Recurse -Include 'Pos.*.exe', 'Pos.*.dll', 'BusinessPost*.exe' | ForEach-Object FullName
    Sign $own
} else {
    Write-Warning 'Sin -CodeSigningThumbprint: los ejecutables van sin firma de código. El primer arranque tras instalar o actualizar puede tardar varios minutos (docs/guia-instalacion.md §8).'
}

Write-Host 'Agregando PostgreSQL (sin pgAdmin, StackBuilder ni documentación)...' -ForegroundColor Cyan
Expand-Archive $PostgresZip -DestinationPath $package -Force
foreach ($extra in 'pgAdmin 4', 'StackBuilder', 'doc', 'symbols', 'include') {
    Remove-Item (Join-Path $package "pgsql\$extra") -Recurse -Force -ErrorAction SilentlyContinue
}
if (-not (Test-Path (Join-Path $package 'pgsql\bin\initdb.exe'))) { throw 'El ZIP de PostgreSQL no tiene pgsql\bin\initdb.exe.' }

Write-Host 'Paquete de actualización...' -ForegroundColor Cyan
$zip = Join-Path $releases "BusinessPost-$Version.zip"
Remove-Item $zip -Force -ErrorAction SilentlyContinue
Compress-Archive -Path (Join-Path $app '*') -DestinationPath $zip -CompressionLevel Optimal

if ($SigningKey) {
    & dotnet run --project (Join-Path $root 'tools\Pos.Release\Pos.Release.csproj') -c Release -- sign --package $zip --version $Version `
        --channel $Channel --key $SigningKey --package-url (Split-Path $zip -Leaf) --notes $Notes --out (Join-Path $releases "$Channel.json")
    if ($LASTEXITCODE -ne 0) { throw 'Falló la firma del manifiesto.' }
} else {
    Write-Warning 'Sin -SigningKey: no se firmó el manifiesto (las tiendas no instalarán esta versión automáticamente).'
}

$iscc = @("${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe", "$env:ProgramFiles\Inno Setup 6\ISCC.exe", "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe") | Where-Object { Test-Path $_ } | Select-Object -First 1
if ($iscc) {
    $manifest = if ($UpdateBaseUrl) { $UpdateBaseUrl.TrimEnd('/') + "/$Channel.json" } else { '' }
    # 1. Paquete clásico (asistente de Inno de siempre). El instalador premium lo ejecuta en silencio con parámetros.
    $classic = Join-Path $releases "BusinessPost-Clasico-$Version.exe"
    & $iscc "/DAppVersion=$Version" "/DPackageDir=$package" "/DLicenseServer=$LicenseServer" "/DUpdateManifest=$manifest" "/DOutputName=BusinessPost-Clasico-$Version" (Join-Path $root 'installer\BusinessPost.iss')
    if ($LASTEXITCODE -ne 0) { throw 'Falló la compilación del paquete clásico.' }
    Sign @($classic)

    # 2. Instalador premium (Fase 13-B, D13B-08): un solo .exe autocontenido con el servidor de licencias de esta versión.
    Write-Host 'Instalador premium (ventana propia con la marca)...' -ForegroundColor Cyan
    $premium = Join-Path $root 'artifacts\premium'
    Remove-Item $premium -Recurse -Force -ErrorAction SilentlyContinue
    Publish 'src\Installer\Pos.Installer.Setup\Pos.Installer.Setup.csproj' $premium -SingleFile -Extra @("-p:PosLicenseServer=$LicenseServer")
    Sign @(Join-Path $premium 'BusinessPost-Setup.exe')

    # 3. El archivo que se descarga: BusinessPost-Setup-<versión>.exe con los dos adentro (un solo aviso de permisos de Windows).
    & $iscc "/DAppVersion=$Version" "/DPremiumExe=$(Join-Path $premium 'BusinessPost-Setup.exe')" "/DClassicSetup=$classic" (Join-Path $root 'installer\BusinessPost-Premium.iss')
    if ($LASTEXITCODE -ne 0) { throw 'Falló la compilación del instalador descargable.' }
    Sign @(Get-ChildItem $releases -Filter "BusinessPost-Setup-$Version.exe" | ForEach-Object FullName)
} else {
    Write-Warning 'Inno Setup 6 no está instalado: instálelo (winget install JRSoftware.InnoSetup) para generar el .exe del instalador.'
}

Get-ChildItem $releases | Format-Table Name, @{ n = 'MB'; e = { [math]::Round($_.Length / 1MB, 1) } }
