# Generates the app icon (3x3 glowing keys on a dark rounded square) for Android and Windows.
#   mobile\assets\icon\icon.png       1024px legacy launcher icon
#   mobile\assets\icon\icon_fg.png    1024px transparent foreground for the adaptive icon (kept inside the safe zone)
#   windows\StreamDeckHost\app.ico    multi-size .ico (16..256) for the exe, window and tray
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing
$root = Split-Path $PSScriptRoot -Parent

function New-RoundedPath([single]$x, [single]$y, [single]$w, [single]$h, [single]$r) {
    $p = New-Object Drawing.Drawing2D.GraphicsPath
    $d = $r * 2
    $p.AddArc($x, $y, $d, $d, 180, 90); $p.AddArc($x + $w - $d, $y, $d, $d, 270, 90)
    $p.AddArc($x + $w - $d, $y + $h - $d, $d, $d, 0, 90); $p.AddArc($x, $y + $h - $d, $d, $d, 90, 90)
    $p.CloseFigure(); return $p
}
function C([string]$hex, [int]$a = 255) { $n = [Convert]::ToInt32($hex.TrimStart('#'), 16); [Drawing.Color]::FromArgb($a, ($n -shr 16) -band 255, ($n -shr 8) -band 255, $n -band 255) }

function Draw-Icon([int]$size, [bool]$foregroundOnly) {
    $bmp = New-Object Drawing.Bitmap $size, $size, ([Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = 'AntiAlias'; $g.InterpolationMode = 'HighQualityBicubic'; $g.PixelOffsetMode = 'HighQuality'
    $g.Clear([Drawing.Color]::Transparent)
    $s = [single]$size

    if (-not $foregroundOnly) {
        $path = New-RoundedPath 0 0 $s $s ($s * 0.225)
        $bg = New-Object Drawing.Drawing2D.LinearGradientBrush ([Drawing.PointF]::new(0, 0)), ([Drawing.PointF]::new($s, $s)), (C '#15153a'), (C '#05050f')
        $g.FillPath($bg, $path)
        $pen = New-Object Drawing.Pen (New-Object Drawing.Drawing2D.LinearGradientBrush ([Drawing.PointF]::new(0, 0)), ([Drawing.PointF]::new($s, $s)), (C '#7c4dff'), (C '#1e90ff')), ($s * 0.028)
        $inset = $s * 0.014
        $g.DrawPath($pen, (New-RoundedPath $inset $inset ($s - 2 * $inset) ($s - 2 * $inset) ($s * 0.21)))
    }

    # 3x3 keys
    $area = if ($foregroundOnly) { $s * 0.50 } else { $s * 0.60 }
    $gap = $area * 0.085
    $k = ($area - 2 * $gap) / 3
    $x0 = ($s - $area) / 2; $y0 = ($s - $area) / 2
    $cols = @('#1e90ff', '#00e5ff', '#7c4dff', '#d500f9', '#7c4dff', '#1e90ff', '#7c4dff', '#00e676', '#00e5ff')
    for ($i = 0; $i -lt 9; $i++) {
        $cx = $x0 + ($i % 3) * ($k + $gap); $cy = $y0 + [math]::Floor($i / 3) * ($k + $gap)
        $col = $cols[$i]
        # glow
        foreach ($e in 0.22, 0.12) {
            $gp = New-RoundedPath ($cx - $k * $e) ($cy - $k * $e) ($k * (1 + 2 * $e)) ($k * (1 + 2 * $e)) ($k * 0.3)
            $g.FillPath((New-Object Drawing.SolidBrush (C $col 38)), $gp)
        }
        $kp = New-RoundedPath $cx $cy $k $k ($k * 0.24)
        $fill = New-Object Drawing.Drawing2D.LinearGradientBrush ([Drawing.PointF]::new($cx, $cy)), ([Drawing.PointF]::new($cx + $k, $cy + $k)), (C $col), (C '#0a0a22')
        $g.FillPath($fill, $kp)
        $g.DrawPath((New-Object Drawing.Pen (C '#ffffff' 90), ([single]($s * 0.006))), $kp)
        if ($i -eq 4) {   # centre key: play triangle
            $t = [Drawing.PointF[]]@([Drawing.PointF]::new($cx + $k * 0.36, $cy + $k * 0.26), [Drawing.PointF]::new($cx + $k * 0.36, $cy + $k * 0.74), [Drawing.PointF]::new($cx + $k * 0.76, $cy + $k * 0.5))
            $g.FillPolygon((New-Object Drawing.SolidBrush (C '#ffffff')), $t)
        }
    }
    $g.Dispose()
    return $bmp
}

function Save-Png($bmp, $file) { New-Item -ItemType Directory -Force (Split-Path $file) | Out-Null; $bmp.Save($file, [Drawing.Imaging.ImageFormat]::Png) }
function Png-Bytes($bmp) { $ms = New-Object IO.MemoryStream; $bmp.Save($ms, [Drawing.Imaging.ImageFormat]::Png); return $ms.ToArray() }

# Android
$b = Draw-Icon 1024 $false; Save-Png $b "$root\mobile\assets\icon\icon.png"; $b.Dispose()
$b = Draw-Icon 1024 $true;  Save-Png $b "$root\mobile\assets\icon\icon_fg.png"; $b.Dispose()

# Windows .ico (PNG-compressed entries)
$sizes = 16, 24, 32, 48, 64, 128, 256
$images = foreach ($sz in $sizes) { $x = Draw-Icon $sz $false; $bytes = Png-Bytes $x; $x.Dispose(); [pscustomobject]@{ Size = $sz; Bytes = $bytes } }
$ms = New-Object IO.MemoryStream; $w = New-Object IO.BinaryWriter $ms
$w.Write([uint16]0); $w.Write([uint16]1); $w.Write([uint16]$images.Count)
$offset = 6 + 16 * $images.Count
foreach ($im in $images) {
    $dim = if ($im.Size -ge 256) { 0 } else { $im.Size }
    $w.Write([byte]$dim); $w.Write([byte]$dim); $w.Write([byte]0); $w.Write([byte]0)
    $w.Write([uint16]1); $w.Write([uint16]32); $w.Write([uint32]$im.Bytes.Length); $w.Write([uint32]$offset)
    $offset += $im.Bytes.Length
}
foreach ($im in $images) { $w.Write([byte[]]$im.Bytes, 0, $im.Bytes.Length) }
$w.Flush()
[IO.File]::WriteAllBytes("$root\windows\StreamDeckHost\app.ico", $ms.ToArray())
Write-Host "Icons written."
