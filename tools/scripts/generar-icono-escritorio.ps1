<#
.SYNOPSIS
    Genera el ícono de la ventana de BusinessPost (src\Desktop\Pos.Desktop\BusinessPost.ico) a partir del logo "B" de la marca.
.DESCRIPTION
    Dibuja el mismo trazado SVG del logo (docs/diseno/instalador-premium-maqueta.html, viewBox 0 0 32 32) con GDI+: fondo redondeado con
    el degradado de la marca (#2524C8 → #5B3DF5), la "B" blanca y el punto turquesa (#2DD4BF). Guarda un .ico con imágenes PNG de 16, 20,
    24, 32, 40, 48, 64, 128 y 256 px. Solo se corre cuando cambia el logo; el .ico generado se guarda en el repositorio.
.EXAMPLE
    ./tools/scripts/generar-icono-escritorio.ps1
#>
[CmdletBinding()]
param(
    [string] $Salida = ''
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing
if (-not $Salida) { $Salida = Join-Path (Split-Path -Parent $MyInvocation.MyCommand.Path) '..\..\src\Desktop\Pos.Desktop\BusinessPost.ico' }

function Nuevo-Punto([double] $x, [double] $y, [double] $escala) { [System.Drawing.PointF]::new([float]($x * $escala), [float]($y * $escala)) }

# Trazado de la "B" (mismo SVG de la maqueta): M, H, V, C y Z en coordenadas absolutas de 0 a 32.
$trazado = 'M9 7H17C20 7 22 9 22 11.6C22 13.3 21.1 14.7 19.8 15.4C21.8 16.1 23 17.9 23 20.1C23 23 20.8 25 17.8 25H9Z' +
    'M12.6 10.2V14H16.7C17.9 14 18.6 13.2 18.6 12.1C18.6 11 17.9 10.2 16.7 10.2Z' +
    'M12.6 17.1V21.8H17.2C18.6 21.8 19.4 20.9 19.4 19.45C19.4 18 18.6 17.1 17.2 17.1Z'

function Trazado-B([double] $escala) {
    $ruta = [System.Drawing.Drawing2D.GraphicsPath]::new([System.Drawing.Drawing2D.FillMode]::Alternate)
    $tokens = [regex]::Matches($trazado, '[MHVCZ]|-?\d+(\.\d+)?') | ForEach-Object Value
    $i = 0; $x = 0.0; $y = 0.0
    while ($i -lt $tokens.Count) {
        $cmd = $tokens[$i]; $i++
        switch ($cmd) {
            'M' { $ruta.StartFigure(); $x = [double]$tokens[$i]; $y = [double]$tokens[$i + 1]; $i += 2 }
            'H' { $nx = [double]$tokens[$i]; $i++; $ruta.AddLine((Nuevo-Punto $x $y $escala), (Nuevo-Punto $nx $y $escala)); $x = $nx }
            'V' { $ny = [double]$tokens[$i]; $i++; $ruta.AddLine((Nuevo-Punto $x $y $escala), (Nuevo-Punto $x $ny $escala)); $y = $ny }
            'C' {
                $v = 0..5 | ForEach-Object { [double]$tokens[$i + $_] }; $i += 6
                $ruta.AddBezier((Nuevo-Punto $x $y $escala), (Nuevo-Punto $v[0] $v[1] $escala), (Nuevo-Punto $v[2] $v[3] $escala), (Nuevo-Punto $v[4] $v[5] $escala))
                $x = $v[4]; $y = $v[5]
            }
            'Z' { $ruta.CloseFigure() }
        }
    }
    return $ruta
}

function Dibujar([int] $tam) {
    $bmp = [System.Drawing.Bitmap]::new($tam, $tam, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $g.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
    $g.Clear([System.Drawing.Color]::Transparent)
    $escala = $tam / 32.0

    # Fondo: cuadrado redondeado (rx = 8 de 32) con el degradado de la marca.
    $radio = 8 * $escala; $d = 2 * $radio
    $fondo = [System.Drawing.Drawing2D.GraphicsPath]::new()
    $fondo.AddArc(0, 0, $d, $d, 180, 90); $fondo.AddArc($tam - $d, 0, $d, $d, 270, 90)
    $fondo.AddArc($tam - $d, $tam - $d, $d, $d, 0, 90); $fondo.AddArc(0, $tam - $d, $d, $d, 90, 90); $fondo.CloseFigure()
    $degradado = [System.Drawing.Drawing2D.LinearGradientBrush]::new([System.Drawing.PointF]::new(0, 0), [System.Drawing.PointF]::new($tam, $tam),
        [System.Drawing.ColorTranslator]::FromHtml('#2524C8'), [System.Drawing.ColorTranslator]::FromHtml('#5B3DF5'))
    $g.FillPath($degradado, $fondo)

    $g.FillPath([System.Drawing.Brushes]::White, (Trazado-B $escala))
    $turquesa = [System.Drawing.SolidBrush]::new([System.Drawing.ColorTranslator]::FromHtml('#2DD4BF'))
    $r = 2.6 * $escala
    $g.FillEllipse($turquesa, [float](26 * $escala - $r), [float](26 * $escala - $r), [float](2 * $r), [float](2 * $r))
    $g.Dispose()
    $ms = [System.IO.MemoryStream]::new()
    $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
    $bmp.Dispose()
    return , $ms.ToArray()
}

$tamanos = 16, 20, 24, 32, 40, 48, 64, 128, 256
$imagenes = foreach ($t in $tamanos) { , (Dibujar $t) }

# Formato ICO: encabezado (6 bytes), una entrada de 16 bytes por imagen y luego los PNG.
$salidaStream = [System.IO.MemoryStream]::new()
$w = [System.IO.BinaryWriter]::new($salidaStream)
$w.Write([uint16]0); $w.Write([uint16]1); $w.Write([uint16]$tamanos.Count)
$offset = 6 + 16 * $tamanos.Count
for ($k = 0; $k -lt $tamanos.Count; $k++) {
    $t = $tamanos[$k]; $png = $imagenes[$k]
    $w.Write([byte]($(if ($t -ge 256) { 0 } else { $t }))); $w.Write([byte]($(if ($t -ge 256) { 0 } else { $t })))
    $w.Write([byte]0); $w.Write([byte]0); $w.Write([uint16]1); $w.Write([uint16]32)
    $w.Write([uint32]$png.Length); $w.Write([uint32]$offset)
    $offset += $png.Length
}
foreach ($png in $imagenes) { $w.Write($png) }
$w.Flush()
$destino = [System.IO.Path]::GetFullPath($Salida)
New-Item -ItemType Directory -Force (Split-Path $destino) | Out-Null
[System.IO.File]::WriteAllBytes($destino, $salidaStream.ToArray())
Write-Host "Ícono generado: $destino ($([math]::Round($salidaStream.Length / 1KB, 1)) KB)"
