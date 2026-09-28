<#
.SYNOPSIS
    Compila la solución, ejecuta todas las pruebas y verifica la cobertura mínima.
.DESCRIPTION
    Punto de entrada único para desarrolladores y CI. Falla (código de salida distinto de 0) si:
    - la compilación tiene errores o advertencias (TreatWarningsAsErrors),
    - alguna prueba falla,
    - la cobertura de líneas de un ensamblado con umbral queda por debajo del mínimo.
.EXAMPLE
    ./build.ps1
    ./build.ps1 -Configuration Debug -SkipCoverage
#>
[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')]
    [string] $Configuration = 'Release',
    [switch] $SkipTests,
    [switch] $SkipCoverage
)

$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$artifacts = Join-Path $root 'artifacts'
$env:DOTNET_NOLOGO = '1'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'

# Cobertura mínima de líneas por ensamblado de producción (ver docs/fases/fase-01-propuesta.md).
$coverageThresholds = @{
    'Pos.SharedKernel' = 95
}

function Invoke-Step([string] $Title, [scriptblock] $Action) {
    Write-Host ''
    Write-Host "==> $Title" -ForegroundColor Cyan
    & $Action
    if ($LASTEXITCODE -ne 0) {
        Write-Host "FALLÓ: $Title (código $LASTEXITCODE)" -ForegroundColor Red
        exit $LASTEXITCODE
    }
}

Invoke-Step 'Restaurar paquetes' { dotnet restore (Join-Path $root 'Pos.slnx') }
Invoke-Step "Compilar ($Configuration)" { dotnet build (Join-Path $root 'Pos.slnx') -c $Configuration --no-restore }

if ($SkipTests) {
    Write-Host 'Pruebas omitidas (-SkipTests).' -ForegroundColor Yellow
    exit 0
}

if (Test-Path $artifacts) { Remove-Item $artifacts -Recurse -Force }
$testProjects = Get-ChildItem (Join-Path $root 'tests') -Recurse -Filter '*.csproj'

foreach ($project in $testProjects) {
    $resultsDir = Join-Path $artifacts "test-results/$($project.BaseName)"
    $coverageArgs = @()
    if (-not $SkipCoverage) {
        $coverageArgs = @('--coverlet', '--coverlet-output-format', 'cobertura', '--coverlet-include', '[Pos.*]*')
    }

    Invoke-Step "Pruebas: $($project.BaseName)" {
        dotnet test --project $project.FullName -c $Configuration --no-build --results-directory $resultsDir @coverageArgs
    }
}

if ($SkipCoverage) { exit 0 }

# Resumen de cobertura: se toma el mejor valor por ensamblado entre todos los reportes.
$coverage = @{}
Get-ChildItem $artifacts -Recurse -Filter 'coverage.cobertura*.xml' | ForEach-Object {
    [xml] $report = Get-Content $_.FullName
    foreach ($package in $report.coverage.packages.package) {
        $rate = [math]::Round([double]::Parse($package.'line-rate', [Globalization.CultureInfo]::InvariantCulture) * 100, 1)
        if (-not $coverage.ContainsKey($package.name) -or $coverage[$package.name] -lt $rate) {
            $coverage[$package.name] = $rate
        }
    }
}

Write-Host ''
Write-Host '==> Cobertura de líneas' -ForegroundColor Cyan
$failed = $false
foreach ($name in ($coverage.Keys | Where-Object { $_ -notlike '*Tests' } | Sort-Object)) {
    $line = '{0,-35} {1,6}%' -f $name, $coverage[$name]
    if ($coverageThresholds.ContainsKey($name)) {
        $min = $coverageThresholds[$name]
        if ($coverage[$name] -lt $min) {
            Write-Host "$line   < mínimo $min%" -ForegroundColor Red
            $failed = $true
        } else {
            Write-Host "$line   (mínimo $min%)" -ForegroundColor Green
        }
    } else {
        Write-Host $line
    }
}

foreach ($name in $coverageThresholds.Keys) {
    if (-not $coverage.ContainsKey($name)) {
        Write-Host "No se encontró cobertura para $name" -ForegroundColor Red
        $failed = $true
    }
}

if ($failed) { exit 1 }
Write-Host ''
Write-Host 'BUILD OK' -ForegroundColor Green
