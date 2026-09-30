<#
.SYNOPSIS
    Arma una versión para clientes (Fase 13): publica el servidor, el migrador, el agente de caja y el actualizador; agrega PostgreSQL 18;
    crea el paquete de actualización (ZIP), firma su manifiesto y compila el instalador con Inno Setup.
.DESCRIPTION
    Salida en artifacts\releases:
      BusinessPost-<versión>.zip          paquete de actualización (server, migrator, agent)
      <canal>.json                        manifiesto firmado (si se pasa -SigningKey)
      BusinessPost-Setup-<versión>.exe    instalador (si Inno Setup 6 está instalado)
    Suba el ZIP y el manifiesto a la carpeta updates/ del VPS (docs/despliegue-nube.md §16).
.PARAMETER PostgresZip
    ZIP oficial de binarios de PostgreSQL 18 para Windows x64 (https://www.enterprisedb.com/download-postgresql-binaries). Se incluye sin
    pgAdmin, StackBuilder ni documentación.
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
    [string] $Notes = ''
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

function Publish([string] $project, [string] $output, [switch] $SingleFile) {
    $arguments = @('publish', (Join-Path $root $project), '-c', 'Release', '-r', 'win-x64', '--self-contained', 'true',
        '-p:PublishReadyToRun=true', "-p:Version=$Version", '-o', $output, '--nologo', '-v', 'q')
    if ($SingleFile) { $arguments += @('-p:PublishSingleFile=true', '-p:IncludeNativeLibrariesForSelfExtract=true') }
    & dotnet @arguments
    if ($LASTEXITCODE -ne 0) { throw "Falló la publicación de $project." }
}

Write-Host "Publicando $Version (autocontenido, ReadyToRun)..." -ForegroundColor Cyan
Publish 'src\Server\Pos.Server.Host\Pos.Server.Host.csproj' (Join-Path $app 'server')
Publish 'src\Server\Pos.Server.Migrator\Pos.Server.Migrator.csproj' (Join-Path $app 'migrator')
Publish 'src\Terminal\Pos.Terminal.Agent\Pos.Terminal.Agent.csproj' (Join-Path $app 'agent')
Publish 'src\Server\Pos.Server.Updater\Pos.Server.Updater.csproj' (Join-Path $package 'updater') -SingleFile

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
    & $iscc "/DAppVersion=$Version" "/DPackageDir=$package" "/DLicenseServer=$LicenseServer" "/DUpdateManifest=$manifest" (Join-Path $root 'installer\BusinessPost.iss')
    if ($LASTEXITCODE -ne 0) { throw 'Falló la compilación del instalador.' }
} else {
    Write-Warning 'Inno Setup 6 no está instalado: instálelo (winget install JRSoftware.InnoSetup) para generar el .exe del instalador.'
}

Get-ChildItem $releases | Format-Table Name, @{ n = 'MB'; e = { [math]::Round($_.Length / 1MB, 1) } }
