<#
.SYNOPSIS
    Pruebas de fallos de la Fase 14 (D14-03) sobre una instalación de PRUEBA: provoca un fallo mientras las cajas venden y comprueba
    que nada quedó a medias.
.DESCRIPTION
    Úselo en una máquina virtual o en Windows Sandbox con el POS instalado (Fase 13) y una tienda preparada con
    `Pos.LoadTest seed`. REQUIERE PowerShell como Administrador. Nunca en una tienda real.

    Escenarios:
      KillServer    Mata el proceso del servidor a mitad de las ventas (como un corte de luz del servicio) y lo vuelve a arrancar.
      KillDatabase  Mata PostgreSQL con 5 cajas vendiendo; el servicio de BD se reinicia solo (recuperación automática).
      Clock         Atrasa el reloj de Windows 2 días: la licencia debe quedar RESTRINGIDA sin cortar la jornada abierta. Luego lo corrige.
      Network       Desactiva el adaptador de red N segundos (ejecútelo en una CAJA Multicaja) y lo reactiva.
      DiskFull      Llena el disco indicado hasta dejar -FreeMB libres, espera y libera (use un disco virtual de prueba).

    Después de cada escenario ejecuta `Pos.Server.Migrator verify-consistency` (ventas completas en inventario y caja, pagos, kardex y
    auditoría) y deja el resultado en artifacts\chaos\<escenario>-<fecha>.txt para el informe de la fase.
.EXAMPLE
    ./tools/scripts/chaos.ps1 -Scenario KillServer -LoadFile C:\pruebas\carga.json
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)] [ValidateSet('KillServer', 'KillDatabase', 'Clock', 'Network', 'DiskFull')] [string] $Scenario,
    [string] $LoadFile = 'carga.json',
    [string] $InstallRoot = "$env:ProgramFiles\PosSupermercado",
    [string] $Product = 'PosSupermercado',
    [int] $Seconds = 30,
    [string] $Drive = 'D:',
    [int] $FreeMB = 200
)

$ErrorActionPreference = 'Stop'
$principal = [Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) { throw 'Ejecute como Administrador.' }

$root = Resolve-Path (Join-Path $PSScriptRoot '..\..')
$migrator = Join-Path $InstallRoot 'app\current\migrator\Pos.Server.Migrator.exe'
$outDir = Join-Path $root 'artifacts\chaos'
New-Item -ItemType Directory -Force $outDir | Out-Null
$log = Join-Path $outDir ("{0}-{1:yyyyMMdd-HHmmss}.txt" -f $Scenario, (Get-Date))

function Write-Log([string] $text) { $line = "[{0:HH:mm:ss}] {1}" -f (Get-Date), $text; Write-Host $line; Add-Content $log $line }

function Start-Load([int] $minutes) {
    # Carga de fondo: las cajas de carga.json venden durante el fallo.
    Start-Process dotnet -ArgumentList @('run', '--project', (Join-Path $root 'tools\Pos.LoadTest'), '-c', 'Release', '--', 'run', '--file', $LoadFile,
        '--minutes', $minutes, '--csv', (Join-Path $outDir "$Scenario-carga.csv")) -PassThru -NoNewWindow -RedirectStandardOutput (Join-Path $outDir "$Scenario-carga.log")
}

function Wait-Healthy([int] $timeoutSeconds = 120) {
    $deadline = (Get-Date).AddSeconds($timeoutSeconds)
    while ((Get-Date) -lt $deadline) {
        try { if ((Invoke-WebRequest 'http://localhost:5480/health/ready' -UseBasicParsing -TimeoutSec 5).StatusCode -eq 200) { return $true } } catch { }
        Start-Sleep 2
    }
    return $false
}

Write-Log "Escenario $Scenario"
switch ($Scenario) {
    'KillServer' {
        $load = Start-Load 3
        Start-Sleep 45
        Write-Log 'Matando el proceso del servidor (sin apagado ordenado)...'
        Get-Process Pos.Server.Host -ErrorAction SilentlyContinue | Stop-Process -Force
        Start-Sleep 5
        Start-Service "$Product-Server"
        Write-Log ("Servidor de vuelta y sano: {0}" -f (Wait-Healthy))
        $load | Wait-Process
    }
    'KillDatabase' {
        $load = Start-Load 3
        Start-Sleep 45
        Write-Log 'Matando PostgreSQL...'
        Get-Process postgres -ErrorAction SilentlyContinue | Stop-Process -Force
        Start-Sleep 10
        if ((Get-Service "$Product-DB").Status -ne 'Running') { Start-Service "$Product-DB" }
        Write-Log ("Base de datos de vuelta; servidor sano: {0}" -f (Wait-Healthy))
        $load | Wait-Process
    }
    'Clock' {
        Write-Log 'Atrasando el reloj 2 días (la licencia debe quedar RESTRICTED sin cortar la jornada abierta)...'
        Set-Date -Adjust ([TimeSpan]::FromDays(-2)) | Out-Null
        Start-Sleep 90
        Write-Log 'Consulte ahora GET /api/v1/license (estado RESTRICTED) y venda en la jornada abierta. Luego se corrige la hora.'
        Read-Host 'Pulse Enter para corregir la hora'
        Set-Date -Adjust ([TimeSpan]::FromDays(2)) | Out-Null
        w32tm /resync | Out-Null
    }
    'Network' {
        $adapter = Get-NetAdapter | Where-Object Status -eq 'Up' | Select-Object -First 1
        Write-Log "Desactivando $($adapter.Name) $Seconds s (la caja debe avisar de inmediato y no perder la venta en curso)..."
        Disable-NetAdapter -Name $adapter.Name -Confirm:$false
        Start-Sleep $Seconds
        Enable-NetAdapter -Name $adapter.Name -Confirm:$false
        Write-Log 'Red restablecida.'
    }
    'DiskFull' {
        $free = (Get-PSDrive $Drive.TrimEnd(':')).Free
        $fill = [math]::Max(0, $free - $FreeMB * 1MB)
        $file = Join-Path "$Drive\" 'chaos-relleno.bin'
        Write-Log ("Llenando {0}: {1:N0} MB (quedan {2} MB)..." -f $Drive, ($fill / 1MB), $FreeMB)
        fsutil file createnew $file $fill | Out-Null
        try {
            $load = Start-Load 2
            $load | Wait-Process
            Write-Log 'Revise la alerta de espacio y que el backup falle con alerta sin detener las ventas.'
        } finally {
            Remove-Item $file -Force
            Write-Log 'Espacio liberado.'
        }
    }
}

Write-Log 'Verificando la consistencia...'
& $migrator verify-consistency 2>&1 | Tee-Object -FilePath $log -Append
$code = $LASTEXITCODE
Write-Log ($(if ($code -eq 0) { 'RESULTADO: todo consistente.' } else { "RESULTADO: INCONSISTENCIAS (código $code)." }))
exit $code
