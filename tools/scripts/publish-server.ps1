<#
.SYNOPSIS
    Publica el servidor autocontenido para Windows x64 (no requiere .NET instalado en el equipo del cliente).
.EXAMPLE
    ./tools/scripts/publish-server.ps1
#>
[CmdletBinding()]
param(
    [string] $Output
)

$ErrorActionPreference = 'Stop'
if (-not $Output) { $Output = Join-Path $PSScriptRoot '..\..\artifacts\publish\server' }
$project = Join-Path $PSScriptRoot '..\..\src\Server\Pos.Server.Host\Pos.Server.Host.csproj'

dotnet publish $project -c Release -r win-x64 --self-contained true -o $Output
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

Write-Host "Publicado en: $((Resolve-Path $Output).Path)" -ForegroundColor Green
