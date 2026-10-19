# Generates SpaceView\Assets\SpaceView.ico with classic BITMAPINFOHEADER DIB entries
# for 16, 20, 24, 32, 48, 64 and PNG entries for 128, 256 so Windows Explorer,
# Start Menu, Taskbar, and System Tray all render the icon at every DPI.

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

$sizes = @(16, 20, 24, 32, 48, 64, 128, 256)
$repoRoot = Split-Path -Parent $PSScriptRoot
$outPath = Join-Path $repoRoot 'SpaceView\Assets\SpaceView.ico'

function New-SpaceViewBitmap {
    param([int]$Size)

    $bmp = New-Object System.Drawing.Bitmap -ArgumentList $Size, $Size, ([System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $g.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
    $g.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
    $g.Clear([System.Drawing.Color]::Transparent)

    $s = $Size / 32.0

    # Outer dark slate circle
    $bgBrush = New-Object System.Drawing.SolidBrush([System.Drawing.Color]::FromArgb(255, 15, 23, 42))
    $g.FillEllipse($bgBrush, [float](1.0 * $s), [float](1.0 * $s), [float](30.0 * $s), [float](30.0 * $s))

    # Usage pie segments (sky cyan + emerald)
    $cyanBrush = New-Object System.Drawing.SolidBrush([System.Drawing.Color]::FromArgb(255, 56, 189, 248))
    $g.FillPie($cyanBrush, [float](3.0 * $s), [float](3.0 * $s), [float](26.0 * $s), [float](26.0 * $s), -90, 235)

    $emeraldBrush = New-Object System.Drawing.SolidBrush([System.Drawing.Color]::FromArgb(255, 52, 211, 153))
    $g.FillPie($emeraldBrush, [float](3.0 * $s), [float](3.0 * $s), [float](26.0 * $s), [float](26.0 * $s), 145, 125)

    # Inner cutout for donut gauge look
    $centerBrush = New-Object System.Drawing.SolidBrush([System.Drawing.Color]::FromArgb(255, 15, 23, 42))
    $g.FillEllipse($centerBrush, [float](10.0 * $s), [float](10.0 * $s), [float](12.0 * $s), [float](12.0 * $s))

    # Outer ring border
    $stroke = [Math]::Max(1.0, 1.5 * $s)
    $borderPen = New-Object System.Drawing.Pen([System.Drawing.Color]::FromArgb(255, 148, 163, 184), [float]$stroke)
    $g.DrawEllipse($borderPen, [float](1.5 * $s), [float](1.5 * $s), [float](29.0 * $s), [float](29.0 * $s))

    $borderPen.Dispose()
    $centerBrush.Dispose()
    $emeraldBrush.Dispose()
    $cyanBrush.Dispose()
    $bgBrush.Dispose()
    $g.Dispose()
    return $bmp
}

function Get-DibBytes {
    param([System.Drawing.Bitmap]$Bitmap)

    $w = $Bitmap.Width
    $h = $Bitmap.Height
    $rect = New-Object System.Drawing.Rectangle -ArgumentList 0, 0, $w, $h
    $data = $Bitmap.LockBits($rect, [System.Drawing.Imaging.ImageLockMode]::ReadOnly, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $stride = $data.Stride
    $pixels = New-Object byte[] ($stride * $h)
    [System.Runtime.InteropServices.Marshal]::Copy($data.Scan0, $pixels, 0, $pixels.Length)
    $Bitmap.UnlockBits($data)

    $ms = New-Object System.IO.MemoryStream
    $bw = New-Object System.IO.BinaryWriter -ArgumentList $ms

    # BITMAPINFOHEADER (height doubled for XOR + AND masks)
    $bw.Write([int]40)
    $bw.Write([int]$w)
    $bw.Write([int]($h * 2))
    $bw.Write([int16]1)
    $bw.Write([int16]32)
    $bw.Write([int]0)
    $bw.Write([int]($w * $h * 4))
    $bw.Write([int]0); $bw.Write([int]0); $bw.Write([int]0); $bw.Write([int]0)

    # XOR bitmap: bottom-up BGRA rows
    for ($y = $h - 1; $y -ge 0; $y--) {
        $bw.Write($pixels, $y * $stride, $w * 4)
    }

    # AND mask: rows padded to 4-byte boundary
    $maskRow = [int]([Math]::Floor(($w + 31) / 32) * 4)
    $zeros = New-Object byte[] ($maskRow * $h)
    $bw.Write($zeros, 0, $zeros.Length)

    $bw.Flush()
    $bytes = $ms.ToArray()
    $bw.Dispose()
    $ms.Dispose()
    return ,$bytes
}

function Get-PngBytes {
    param([System.Drawing.Bitmap]$Bitmap)
    $ms = New-Object System.IO.MemoryStream
    $Bitmap.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
    $bytes = $ms.ToArray()
    $ms.Dispose()
    return ,$bytes
}

$images = @()
foreach ($size in $sizes) {
    $bmp = New-SpaceViewBitmap -Size $size
    if ($size -ge 128) {
        $bytes = Get-PngBytes -Bitmap $bmp
    } else {
        $bytes = Get-DibBytes -Bitmap $bmp
    }
    $images += [pscustomobject]@{ Size = $size; Bytes = $bytes }
    $bmp.Dispose()
}

$icoStream = New-Object System.IO.MemoryStream
$w2 = New-Object System.IO.BinaryWriter -ArgumentList $icoStream
$w2.Write([int16]0)
$w2.Write([int16]1)
$w2.Write([int16]$images.Count)

$offset = 6 + (16 * $images.Count)
foreach ($img in $images) {
    $dim = if ($img.Size -ge 256) { 0 } else { $img.Size }
    $w2.Write([byte]$dim)
    $w2.Write([byte]$dim)
    $w2.Write([byte]0)
    $w2.Write([byte]0)
    $w2.Write([int16]1)
    $w2.Write([int16]32)
    $w2.Write([int]$img.Bytes.Length)
    $w2.Write([int]$offset)
    $offset += $img.Bytes.Length
}

foreach ($img in $images) {
    $w2.Write($img.Bytes, 0, $img.Bytes.Length)
}
$w2.Flush()

$dir = Split-Path -Parent $outPath
if (-not (Test-Path $dir)) {
    New-Item -ItemType Directory -Path $dir -Force | Out-Null
}
[System.IO.File]::WriteAllBytes($outPath, $icoStream.ToArray())
$w2.Dispose()
$icoStream.Dispose()

Write-Host ("Generated {0} ({1} bytes, {2} icon sizes)" -f $outPath, (Get-Item $outPath).Length, $images.Count) -ForegroundColor Green
