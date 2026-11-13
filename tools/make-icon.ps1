# Rebuilds assets/icon.ico from the same geometry documented in assets/icon.svg.
# Uses only in-box System.Drawing, so no extra tooling is required on the machine.
param(
    [string]$OutFile,
    [string]$PreviewDir
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

$root = Split-Path -Parent $PSScriptRoot
if (-not $OutFile) { $OutFile = Join-Path $root 'assets\icon.ico' }

$sizes = @(16, 20, 24, 32, 40, 48, 64, 128, 256)

# Palette, taken from src/UI/MainWindow.cs.
$cOk = [System.Drawing.Color]::FromArgb(0x3F, 0xB9, 0x50)
$cWarn = [System.Drawing.Color]::FromArgb(0xD2, 0x99, 0x22)
$cCrit = [System.Drawing.Color]::FromArgb(0xF8, 0x51, 0x49)
$cTileTop = [System.Drawing.Color]::FromArgb(0x1A, 0x1F, 0x27)
$cTileBottom = [System.Drawing.Color]::FromArgb(0x10, 0x13, 0x18)
$cTrack = [System.Drawing.Color]::FromArgb(19, 255, 255, 255)

function New-RoundedPath([single]$x, [single]$y, [single]$w, [single]$h, [single]$r) {
    $path = New-Object System.Drawing.Drawing2D.GraphicsPath
    $d = $r * 2
    if ($d -gt $w) { $d = $w }
    if ($d -gt $h) { $d = $h }
    $path.AddArc($x, $y, $d, $d, 180, 90)
    $path.AddArc($x + $w - $d, $y, $d, $d, 270, 90)
    $path.AddArc($x + $w - $d, $y + $h - $d, $d, $d, 0, 90)
    $path.AddArc($x, $y + $h - $d, $d, $d, 90, 90)
    $path.CloseFigure()
    return $path
}

function Get-SurfaceColor([double]$t, [double]$lift) {
    $shade = 0.05 * (1 - $t) + $lift
    $r = $cTileTop.R + ($cTileBottom.R - $cTileTop.R) * $t
    $gr = $cTileTop.G + ($cTileBottom.G - $cTileTop.G) * $t
    $bl = $cTileTop.B + ($cTileBottom.B - $cTileTop.B) * $t
    $r = $r + (255 - $r) * $shade
    $gr = $gr + (255 - $gr) * $shade
    $bl = $bl + (255 - $bl) * $shade
    return [System.Drawing.Color]::FromArgb(
        [int][math]::Round($r), [int][math]::Round($gr), [int][math]::Round($bl))
}

function Draw-Icon([int]$size) {
    $bmp = New-Object System.Drawing.Bitmap($size, $size, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $g.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
    $g.CompositingQuality = [System.Drawing.Drawing2D.CompositingQuality]::HighQuality
    $g.Clear([System.Drawing.Color]::Transparent)

    $compact = $size -lt 32

    if ($compact) {
        # Small sizes drop the border: only the silhouette survives.
        $pad = 0.0
        $radius = [single]($size * 0.22)
        $barW = [single]($size * 0.69)
        $barH = [single]([math]::Max(2, [math]::Round($size / 8)))
    }
    else {
        $s = $size / 256.0
        $pad = [single](12 * $s)
        $radius = [single](54 * $s)
        $barW = [single](144 * $s)
        # Optical scaling: a 16px icon needs proportionally heavier bars than a 256px one.
        $barH = [single][math]::Max(2, [math]::Round(2 * [math]::Pow($size / 16.0, 0.75)))
    }
    $gap = [single][math]::Max(2, [math]::Round($barH * 1.2))

    $tileW = $size - (2 * $pad)
    $tilePath = New-RoundedPath $pad $pad $tileW $tileW $radius

    # The surface is baked per scanline: GDI+ banding on alpha gradients and
    # broken alpha on semi-transparent pens make the obvious approaches unreliable.
    $rows = [int][math]::Round($tileW)
    $fillBrush = New-Object System.Drawing.SolidBrush([System.Drawing.Color]::Black)
    $state = $g.Save()
    $g.SetClip($tilePath)
    $rimLift = 0.0
    if (-not $compact) { $rimLift = 0.10 }
    for ($i = 0; $i -lt $rows; $i++) {
        $t = $i / [math]::Max(1, $rows - 1)
        $fillBrush.Color = Get-SurfaceColor $t $rimLift
        $g.FillRectangle($fillBrush, [single]$pad, [single]($pad + $i), [single]$tileW, 1.5)
    }
    $g.Restore($state)

    if (-not $compact) {
        # Opaque rim pass, then the inner surface on top: same visual as a 10% white border.
        $inset = [single][math]::Max(1, 2 * $size / 256.0)
        $innerPath = New-RoundedPath ($pad + $inset) ($pad + $inset) ($tileW - 2 * $inset) ($tileW - 2 * $inset) ($radius - $inset)
        $state = $g.Save()
        $g.SetClip($innerPath)
        $innerRows = [int][math]::Ceiling($tileW - 2 * $inset)
        for ($i = 0; $i -lt $innerRows; $i++) {
            $y = $pad + $inset + $i
            $fillBrush.Color = Get-SurfaceColor (($y - $pad) / $tileW) 0.0
            $g.FillRectangle($fillBrush, [single]$pad, [single]$y, [single]$tileW, 1.5)
        }
        $g.Restore($state)
        $innerPath.Dispose()
    }
    $fillBrush.Dispose()

    # Three usage windows, centred, filled by severity.
    $total = 3 * $barH + 2 * $gap
    $barX = ($size - $barW) / 2.0
    $barY = ($size - $total) / 2.0
    $fills = @(0.44, 0.70, 0.90)
    $colors = @($cOk, $cWarn, $cCrit)

    $trackBrush = New-Object System.Drawing.SolidBrush($cTrack)
    for ($i = 0; $i -lt 3; $i++) {
        $y = [single]($barY + $i * ($barH + $gap))
        $track = New-RoundedPath $barX $y $barW $barH ($barH / 2.0)
        $g.FillPath($trackBrush, $track)
        $track.Dispose()

        $fillW = [single][math]::Max($barH, [math]::Round($barW * $fills[$i]))
        $fillBrush = New-Object System.Drawing.SolidBrush($colors[$i])
        $fill = New-RoundedPath $barX $y $fillW $barH ($barH / 2.0)
        $g.FillPath($fillBrush, $fill)
        $fill.Dispose()
        $fillBrush.Dispose()
    }
    $trackBrush.Dispose()

    $tilePath.Dispose()
    $g.Dispose()
    return $bmp
}

function Get-BitmapRows([System.Drawing.Bitmap]$bmp) {
    $rect = New-Object System.Drawing.Rectangle(0, 0, $bmp.Width, $bmp.Height)
    $data = $bmp.LockBits($rect, [System.Drawing.Imaging.ImageLockMode]::ReadOnly, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $rowBytes = $bmp.Width * 4
    $rows = New-Object 'System.Collections.Generic.List[byte[]]'
    try {
        for ($y = 0; $y -lt $bmp.Height; $y++) {
            $row = New-Object byte[] $rowBytes
            $src = [System.IntPtr]::Add($data.Scan0, $y * $data.Stride)
            [System.Runtime.InteropServices.Marshal]::Copy($src, $row, 0, $rowBytes)
            $rows.Add($row)
        }
    }
    finally { $bmp.UnlockBits($data) }
    return , $rows
}

function Get-DibFrame([System.Drawing.Bitmap]$bmp) {
    $w = $bmp.Width
    $h = $bmp.Height
    $rows = Get-BitmapRows $bmp
    $ms = New-Object System.IO.MemoryStream
    $bw = New-Object System.IO.BinaryWriter($ms)
    $bw.Write([uint32]40)
    $bw.Write([int32]$w)
    $bw.Write([int32]($h * 2))
    $bw.Write([uint16]1)
    $bw.Write([uint16]32)
    $bw.Write([uint32]0)
    $bw.Write([int32]($w * $h * 4))
    $bw.Write([int32]0)
    $bw.Write([int32]0)
    $bw.Write([uint32]0)
    $bw.Write([uint32]0)
    for ($y = $h - 1; $y -ge 0; $y--) { $bw.Write($rows[$y]) }
    $andRow = New-Object byte[] ([math]::Ceiling($w / 32.0) * 4)
    for ($y = 0; $y -lt $h; $y++) { $bw.Write($andRow) }
    $bw.Flush()
    $bytes = $ms.ToArray()
    $bw.Dispose()
    $ms.Dispose()
    return , $bytes
}

$frames = @()
foreach ($size in $sizes) {
    $bmp = Draw-Icon $size
    if ($PreviewDir) {
        New-Item -ItemType Directory -Force -Path $PreviewDir | Out-Null
        $bmp.Save((Join-Path $PreviewDir ("icon-{0}.png" -f $size)), [System.Drawing.Imaging.ImageFormat]::Png)
    }
    if ($size -ge 128) {
        # Large frames are stored as PNG: a 256px DIB alone costs 256 KB.
        $ms = New-Object System.IO.MemoryStream
        $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
        $frames += , @{ Size = $size; Data = $ms.ToArray() }
        $ms.Dispose()
    }
    else {
        $frames += , @{ Size = $size; Data = (Get-DibFrame $bmp) }
    }
    $bmp.Dispose()
}

# ICO container: header + one entry per frame + frame payloads.
$out = New-Object System.IO.MemoryStream
$writer = New-Object System.IO.BinaryWriter($out)
$writer.Write([uint16]0)
$writer.Write([uint16]1)
$writer.Write([uint16]$frames.Count)
$offset = 6 + 16 * $frames.Count
foreach ($frame in $frames) {
    $dim = if ($frame.Size -ge 256) { 0 } else { $frame.Size }
    $writer.Write([byte]$dim)
    $writer.Write([byte]$dim)
    $writer.Write([byte]0)
    $writer.Write([byte]0)
    $writer.Write([uint16]1)
    $writer.Write([uint16]32)
    $writer.Write([uint32]$frame.Data.Length)
    $writer.Write([uint32]$offset)
    $offset += $frame.Data.Length
}
foreach ($frame in $frames) { $writer.Write($frame.Data) }
$writer.Flush()

New-Item -ItemType Directory -Force -Path (Split-Path -Parent $OutFile) | Out-Null
[System.IO.File]::WriteAllBytes($OutFile, $out.ToArray())
$writer.Dispose()
$out.Dispose()

$kb = [math]::Round((Get-Item $OutFile).Length / 1KB, 1)
Write-Host "Wrote $OutFile ($kb KB, $($frames.Count) sizes)" -ForegroundColor Green
