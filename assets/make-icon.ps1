# Genera assets\scanner.ico (lector de codigo de barras de mano con su laser) en varios tamanos.
# Solo hace falta volver a ejecutarlo si se quiere cambiar el diseno del icono.
Add-Type -AssemblyName System.Drawing
$ErrorActionPreference = 'Stop'

function New-RoundRect([float]$x, [float]$y, [float]$w, [float]$h, [float]$r) {
    $p = New-Object System.Drawing.Drawing2D.GraphicsPath
    $d = [Math]::Max(0.5, $r * 2)
    $p.AddArc($x, $y, $d, $d, 180, 90)
    $p.AddArc($x + $w - $d, $y, $d, $d, 270, 90)
    $p.AddArc($x + $w - $d, $y + $h - $d, $d, $d, 0, 90)
    $p.AddArc($x, $y + $h - $d, $d, $d, 90, 90)
    $p.CloseFigure()
    return $p
}

function New-Poly([float]$s, [float[]]$coords) {
    $pts = for ($i = 0; $i -lt $coords.Count; $i += 2) {
        New-Object System.Drawing.PointF ($coords[$i] * $s), ($coords[$i + 1] * $s)
    }
    $p = New-Object System.Drawing.Drawing2D.GraphicsPath
    $p.AddPolygon([System.Drawing.PointF[]]$pts)
    return $p
}

function Brush($a, $r, $g, $b) { New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb($a, $r, $g, $b)) }

function Draw-Scanner([int]$size) {
    $bmp = New-Object System.Drawing.Bitmap $size, $size, ([System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = 'AntiAlias'
    $g.PixelOffsetMode = 'HighQuality'
    $g.Clear([System.Drawing.Color]::Transparent)
    $s = $size / 64.0   # diseno sobre una cuadricula de 64x64
    $small = $size -lt 32

    # Etiqueta con codigo de barras (abajo a la izquierda)
    $label = New-RoundRect (2*$s) (38*$s) (30*$s) (24*$s) (3*$s)
    $g.FillPath((Brush 255 248 250 252), $label)
    # Borde para que la etiqueta se distinga tambien en la barra de tareas clara
    $g.DrawPath((New-Object System.Drawing.Pen ([System.Drawing.Color]::FromArgb(255, 100, 116, 139)), ([Math]::Max(1, 1.5*$s))), $label)
    $barBrush = Brush 255 15 23 42
    $bars = if ($small) { @(@(7,4), @(14,3), @(20,5)) } else { @(@(6,2), @(10,4), @(16,2), @(20,3), @(25,2)) }
    foreach ($b in $bars) { $g.FillRectangle($barBrush, $b[0]*$s, 42*$s, $b[1]*$s, 16*$s) }

    # Haz del laser desde la ventana del lector hasta la etiqueta
    $beam = New-Poly $s @(16,16, 16,24, 30,42, 3,42)
    $g.FillPath((Brush 110 244 63 94), $beam)
    $laserPen = New-Object System.Drawing.Pen ([System.Drawing.Color]::FromArgb(255, 244, 63, 94)), ([Math]::Max(1, 2.5*$s))
    $g.DrawLine($laserPen, 3*$s, 44*$s, 31*$s, 44*$s)

    # Cuerpo del lector (indigo): cabeza + mango inclinado
    $rect = New-Object System.Drawing.RectangleF (14*$s), (6*$s), (48*$s), (56*$s)
    $grad = New-Object System.Drawing.Drawing2D.LinearGradientBrush $rect, ([System.Drawing.Color]::FromArgb(129, 140, 248)), ([System.Drawing.Color]::FromArgb(79, 70, 229)), 90
    $handle = New-Poly $s @(38,24, 54,24, 60,58, 56,62, 48,62)
    $g.FillPath($grad, $handle)
    $head = New-RoundRect (14*$s) (8*$s) (48*$s) (22*$s) (7*$s)
    $g.FillPath($grad, $head)

    # Gatillo
    if (-not $small) {
        $trigger = New-Poly $s @(34,30, 40,30, 40,38, 36,36)
        $g.FillPath((Brush 255 67 56 202), $trigger)
    }

    # Ventana roja del lector (frente)
    $window = New-RoundRect (14*$s) (13*$s) (5*$s) (12*$s) (2*$s)
    $g.FillPath((Brush 255 244 63 94), $window)

    # Brillo superior
    if (-not $small) {
        $shine = New-RoundRect (24*$s) (12*$s) (30*$s) (3*$s) (1.5*$s)
        $g.FillPath((Brush 70 255 255 255), $shine)
    }

    $g.Dispose()
    return $bmp
}

$sizes = 16, 20, 24, 32, 40, 48, 64, 256
$images = foreach ($size in $sizes) {
    $bmp = Draw-Scanner $size
    $ms = New-Object System.IO.MemoryStream
    $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
    $bmp.Dispose()
    , $ms.ToArray()
}

# Formato ICO con imagenes PNG (Windows Vista en adelante)
$out = Join-Path $PSScriptRoot 'scanner.ico'
$fs = [System.IO.File]::Create($out)
$w = New-Object System.IO.BinaryWriter $fs
$w.Write([UInt16]0); $w.Write([UInt16]1); $w.Write([UInt16]$sizes.Count)
$offset = 6 + 16 * $sizes.Count
for ($i = 0; $i -lt $sizes.Count; $i++) {
    $dim = if ($sizes[$i] -ge 256) { 0 } else { $sizes[$i] }
    $w.Write([byte]$dim); $w.Write([byte]$dim); $w.Write([byte]0); $w.Write([byte]0)
    $w.Write([UInt16]1); $w.Write([UInt16]32)
    $w.Write([UInt32]$images[$i].Length); $w.Write([UInt32]$offset)
    $offset += $images[$i].Length
}
foreach ($img in $images) { $w.Write($img) }
$w.Close()
Write-Host "Icono generado: $out"

# Vista previa sobre fondo oscuro y claro (como en la barra de tareas)
$preview = New-Object System.Drawing.Bitmap 440, 300
$pg = [System.Drawing.Graphics]::FromImage($preview)
$pg.Clear([System.Drawing.Color]::FromArgb(30, 41, 59))
$pg.FillRectangle((Brush 255 243 243 243), 0, 150, 440, 150)
foreach ($y in 10, 160) {
    $x = 10
    foreach ($size in 16, 24, 32, 48) {
        $b = Draw-Scanner $size
        $pg.DrawImage($b, $x, $y, $size, $size)
        $x += $size + 14
        $b.Dispose()
    }
}
$big = Draw-Scanner 128
$pg.DrawImage($big, 290, 10, 128, 128)
$big.Dispose()
$pg.Dispose()
$preview.Save((Join-Path $PSScriptRoot 'scanner-preview.png'), [System.Drawing.Imaging.ImageFormat]::Png)
