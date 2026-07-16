<#
.SYNOPSIS
  Generates the Glacier application icon (multi-size .ico) from the source PNG.

.DESCRIPTION
  One-off/build helper. Reads src/Ged.App/Assets/AppIcon.png (the committed
  source-of-truth glacier "G" logo), letterbox-pads it square if needed, resizes
  it to 256/64/48/32/16 px, PNG-encodes each size, and packs them into a single
  Windows .ico (PNG-compressed entries, valid on Vista+). Uses System.Drawing
  (Windows PowerShell) so it needs no build dependency. Re-run after changing the
  source PNG, then commit the regenerated .ico.

  Usage:  powershell -ExecutionPolicy Bypass -File tools\make-icon.ps1
#>

Add-Type -AssemblyName System.Drawing

$root    = Split-Path -Parent $PSScriptRoot
$srcPng  = Join-Path $root 'src\Ged.App\Assets\AppIcon.png'
$outIco  = Join-Path $root 'src\Ged.App\Assets\AppIcon.ico'
$sizes   = 256, 64, 48, 32, 16

if (-not (Test-Path $srcPng)) { throw "source PNG not found: $srcPng" }

$src = [System.Drawing.Image]::FromFile($srcPng)
Write-Host ("source: {0}x{1} ({2})" -f $src.Width, $src.Height, $srcPng)

# Square canvas (transparent letterbox) so non-square art is not stretched.
$side = [Math]::Max($src.Width, $src.Height)
$square = New-Object System.Drawing.Bitmap $side, $side
$sg = [System.Drawing.Graphics]::FromImage($square)
$sg.Clear([System.Drawing.Color]::Transparent)
$sg.DrawImage($src, [int](($side - $src.Width) / 2), [int](($side - $src.Height) / 2), $src.Width, $src.Height)
$sg.Dispose()

$pngs = @()
foreach ($sz in $sizes) {
    $bmp = New-Object System.Drawing.Bitmap $sz, $sz
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.InterpolationMode  = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
    $g.SmoothingMode      = [System.Drawing.Drawing2D.SmoothingMode]::HighQuality
    $g.PixelOffsetMode    = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
    $g.Clear([System.Drawing.Color]::Transparent)
    $g.DrawImage($square, 0, 0, $sz, $sz)
    $g.Dispose()

    $ms = New-Object System.IO.MemoryStream
    $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
    $pngs += ,@{ Size = $sz; Bytes = $ms.ToArray() }
    $bmp.Dispose(); $ms.Dispose()
    Write-Host ("  {0}x{0} -> {1} bytes" -f $sz, $pngs[-1].Bytes.Length)
}
$square.Dispose(); $src.Dispose()

# Pack the .ico: ICONDIR header + one ICONDIRENTRY per image + the PNG payloads.
$fs = New-Object System.IO.MemoryStream
$bw = New-Object System.IO.BinaryWriter $fs
$bw.Write([UInt16]0)                 # reserved
$bw.Write([UInt16]1)                 # type = icon
$bw.Write([UInt16]$pngs.Count)       # image count

$offset = 6 + (16 * $pngs.Count)     # payloads start after all directory entries
foreach ($p in $pngs) {
    $dim = if ($p.Size -ge 256) { 0 } else { $p.Size }  # 0 encodes 256
    $bw.Write([Byte]$dim)            # width
    $bw.Write([Byte]$dim)            # height
    $bw.Write([Byte]0)              # palette count
    $bw.Write([Byte]0)              # reserved
    $bw.Write([UInt16]1)            # color planes
    $bw.Write([UInt16]32)          # bits per pixel
    $bw.Write([UInt32]$p.Bytes.Length)
    $bw.Write([UInt32]$offset)
    $offset += $p.Bytes.Length
}
foreach ($p in $pngs) { $bw.Write($p.Bytes) }

$bw.Flush()
[System.IO.File]::WriteAllBytes($outIco, $fs.ToArray())
$bw.Dispose(); $fs.Dispose()
Write-Host ("wrote: {0} ({1} bytes, {2} sizes)" -f $outIco, (Get-Item $outIco).Length, $pngs.Count)
