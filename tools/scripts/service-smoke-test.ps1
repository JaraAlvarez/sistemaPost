<#
.SYNOPSIS
    Prueba del servidor como Servicio de Windows: publica, instala, arranca, verifica y desinstala.
.DESCRIPTION
    REQUIERE PowerShell ejecutado como Administrador. Usa un nombre de servicio y una carpeta de datos
    de prueba, y elimina todo al terminar (incluso si la verificación falla).
.EXAMPLE
    # En una consola de PowerShell "Ejecutar como administrador":
    ./tools/scripts/service-smoke-test.ps1
#>
[CmdletBinding()]
param(
    [int] $Port = 5481
)

$ErrorActionPreference = 'Stop'
$principal = [Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw 'Este script debe ejecutarse como Administrador.'
}

$serviceName = 'PosSupermercado-Server-SmokeTest'
$publishDir = Join-Path $PSScriptRoot '..\..\artifacts\publish\server-smoke'
$dataRoot = Join-Path $env:ProgramData 'PosSupermercado-SmokeTest'

& (Join-Path $PSScriptRoot 'publish-server.ps1') -Output $publishDir
$exe = (Resolve-Path (Join-Path $publishDir 'Pos.Server.Host.exe')).Path

# Configuración de la instalación de prueba: puerto y carpeta de datos propios.
New-Item -ItemType Directory -Force (Join-Path $dataRoot 'config') | Out-Null

$passed = $false
try {
    Write-Host "Creando servicio $serviceName..." -ForegroundColor Cyan
    # El servicio lee Pos:DataRoot y el puerto desde argumentos de línea de comandos.
    $binPath = "`"$exe`" --Pos:DataRoot=`"$dataRoot`" --Pos:Server:Port=$Port"
    # New-Service recibe la ruta intacta (sc.exe rompe las comillas anidadas en PowerShell 5.1).
    New-Service -Name $serviceName -BinaryPathName $binPath -StartupType Manual | Out-Null
    Start-Service -Name $serviceName

    $info = $null
    for ($i = 0; $i -lt 40 -and -not $info; $i++) {
        Start-Sleep -Milliseconds 500
        try { $info = Invoke-RestMethod "http://localhost:$Port/api/v1/system/info" } catch { }
    }

    if (-not $info) { throw 'El servicio no respondió en 20 segundos.' }

    $info | Format-List | Out-Host
    if (-not $info.runningAsWindowsService) { throw 'El servidor no detectó que corre como servicio.' }
    if ($info.environment -ne 'Production') { throw "Entorno inesperado: $($info.environment)" }

    $health = Invoke-RestMethod "http://localhost:$Port/health/ready"
    if ($health.status -ne 'Healthy') { throw "Health check: $($health.status)" }

    $logs = Get-ChildItem (Join-Path $dataRoot 'logs') -Filter 'server-*.log' -ErrorAction SilentlyContinue
    if (-not $logs) { throw 'No se generó el archivo de log.' }

    $passed = $true
    Write-Host 'PRUEBA DEL SERVICIO: OK' -ForegroundColor Green
}
finally {
    Stop-Service -Name $serviceName -Force -ErrorAction SilentlyContinue
    Start-Sleep -Seconds 2
    sc.exe delete $serviceName | Out-Null
    Remove-Item $dataRoot -Recurse -Force -ErrorAction SilentlyContinue
    if (-not $passed) { Write-Host 'PRUEBA DEL SERVICIO: FALLÓ' -ForegroundColor Red }
}
