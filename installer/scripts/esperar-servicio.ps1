# Espera a que BusinessPost responda (Fase 13-B, D13B-02). Lo ejecuta installer\BusinessPost.iss varias veces seguidas (una "ronda"
# por llamada) para poder mostrar el avance en la pantalla del instalador entre rondas.
#
# El instalador define antes de ejecutar este archivo:
#   $Url             dirección a revisar (http://localhost:5480/health/live en el equipo principal, http://localhost:5490/status en la caja)
#   $Servicio        servicio de Windows que debe estar en ejecución (BusinessPost-Server o BusinessPost-TerminalAgent)
#   $Segundos        duración máxima de esta ronda
#   $PuedeReiniciar  $true si, al encontrarlo detenido, se puede volver a iniciar el servicio en esta ronda
# Código de salida: 0 = responde · 1 = todavía no · 2 = estaba detenido y se volvió a iniciar · 3 = el servicio no existe.
# Los mensajes van sin tildes porque quedan en el registro de la instalación.

$ErrorActionPreference = 'Continue'
$fin = (Get-Date).AddSeconds($Segundos)
$reiniciado = $false

do {
    try {
        $respuesta = Invoke-WebRequest -Uri $Url -UseBasicParsing -TimeoutSec 5
        if ($respuesta.StatusCode -eq 200) {
            Write-Output "Esperar: $Url responde."
            exit 0
        }
    } catch { }

    $estado = Get-Service -Name $Servicio -ErrorAction SilentlyContinue
    if ($null -eq $estado) {
        Write-Output "Esperar: no existe el servicio $Servicio."
        exit 3
    }
    if ($estado.Status -eq 'Stopped' -and $PuedeReiniciar -and -not $reiniciado) {
        # Típico del primer arranque sin firma de código: Windows se rindió a los 30 s mientras Defender revisaba los archivos.
        Write-Output "Esperar: el servicio $Servicio esta detenido; se inicia de nuevo."
        & "$env:SystemRoot\System32\sc.exe" start $Servicio | Out-Null
        $reiniciado = $true
    }
    Start-Sleep -Seconds 2
} while ((Get-Date) -lt $fin)

if ($reiniciado) { exit 2 }
exit 1
