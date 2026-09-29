<#
.SYNOPSIS
    Prepara la base de datos de DESARROLLO: levanta PostgreSQL 18 en Docker (puerto 5488), crea la BD y los roles,
    y aplica las migraciones. Es idempotente: se puede ejecutar cuantas veces se quiera.
.DESCRIPTION
    Las contraseñas de este script son solo de desarrollo (coinciden con appsettings.Development.json).
    En producción las genera el instalador y se guardan protegidas con DPAPI.
.EXAMPLE
    ./tools/scripts/dev-db.ps1
    ./tools/scripts/dev-db.ps1 -Reset     # borra el volumen y empieza de cero
#>
[CmdletBinding()]
param(
    [switch] $Reset
)

$ErrorActionPreference = 'Stop'
$root = Resolve-Path (Join-Path $PSScriptRoot '..\..')
$compose = Join-Path $root 'docker-compose.dev.yml'
$migrator = Join-Path $root 'src\Server\Pos.Server.Migrator\Pos.Server.Migrator.csproj'

if ($Reset) {
    docker compose -f $compose down -v
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
}

docker compose -f $compose up -d --wait
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

$superuser = 'Host=127.0.0.1;Port=5488;Username=postgres;Password=pos-dev-superuser;Database=postgres'
dotnet run --project $migrator -- create-database --superuser $superuser --database pos `
    --migrator-password 'pos-dev-migrator' --app-password 'pos-dev-app-password' --backup-password 'pos-dev-backup'
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

$env:POS_MIGRATOR_CONNECTION = 'Host=127.0.0.1;Port=5488;Username=pos_migrator;Password=pos-dev-migrator;Database=pos'
dotnet run --project $migrator -- migrate
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

Write-Host 'Base de datos de desarrollo lista en 127.0.0.1:5488 (BD pos).' -ForegroundColor Green
