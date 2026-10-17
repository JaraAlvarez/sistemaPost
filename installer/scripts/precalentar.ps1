# Precalentamiento al instalar (Fase 13-B, D13B-01). Lo ejecuta installer\BusinessPost.iss antes de crear los servicios.
#
# Windows Defender revisa cada .exe y .dll nuevo sin firma la primera vez que se carga (en la 1.0.1 medimos 356 s para el servidor).
# Si esa revisión ocurre al arrancar el servicio, Windows se rinde a los 30 s y el servicio queda detenido. Aquí se corre cada programa
# con --precalentar (carga sus archivos y termina, sin abrir puertos ni tocar la base de datos), todos a la vez y con un límite de tiempo.
#
# El instalador define antes de ejecutar este archivo:
#   $Programas  lista de 'ruta del exe|argumentos'
#   $Minutos    tiempo máximo para todos (15)
# Código de salida: cantidad de programas que no terminaron a tiempo (0 = todo bien). Es de mejor esfuerzo: el instalador sigue igual.
# Los mensajes van sin tildes porque quedan en el registro de la instalación.

$ErrorActionPreference = 'Continue'
$inicio = Get-Date
$limite = $inicio.AddMinutes($Minutos)
$procesos = @()

foreach ($programa in $Programas) {
    $exe, $argumentos = $programa -split '\|', 2
    if (-not (Test-Path -LiteralPath $exe)) {
        Write-Output "Precalentar: no existe $exe"
        continue
    }
    try {
        $proceso = Start-Process -FilePath $exe -ArgumentList $argumentos -WorkingDirectory (Split-Path -Parent $exe) -WindowStyle Hidden -PassThru
        $null = $proceso.Handle # Conserva el manejador para poder leer el código de salida.
        $procesos += [pscustomobject]@{ Nombre = (Split-Path -Leaf $exe); Proceso = $proceso }
    } catch {
        Write-Output "Precalentar: no se pudo iniciar ${exe}: $($_.Exception.Message)"
    }
}

$atrasados = 0
foreach ($item in $procesos) {
    $resto = [int][math]::Max(1000, ($limite - (Get-Date)).TotalMilliseconds)
    if ($item.Proceso.WaitForExit($resto)) {
        Write-Output ("Precalentar: {0} listo a los {1:0} s (codigo {2})." -f $item.Nombre, ((Get-Date) - $inicio).TotalSeconds, $item.Proceso.ExitCode)
    } else {
        try { $item.Proceso.Kill() } catch { }
        $atrasados++
        Write-Output ("Precalentar: {0} no termino en {1} minutos; se detuvo y la instalacion sigue." -f $item.Nombre, $Minutos)
    }
}

exit $atrasados
