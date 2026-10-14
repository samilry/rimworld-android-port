# Cuts a rectangle out of a screenshot and enlarges it. usage: crop.ps1 <in> <out> <x> <y> <w> <h> [scale]
param([string]$in, [string]$out, [int]$x, [int]$y, [int]$w, [int]$h, [double]$scale = 1)
Add-Type -AssemblyName System.Drawing
$src = [System.Drawing.Image]::FromFile((Resolve-Path $in))
$dw = [int]($w * $scale); $dh = [int]($h * $scale)
$bmp = New-Object System.Drawing.Bitmap $dw, $dh
$g = [System.Drawing.Graphics]::FromImage($bmp)
$g.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::NearestNeighbor
$g.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::Half
$g.DrawImage($src, (New-Object System.Drawing.Rectangle 0, 0, $dw, $dh), (New-Object System.Drawing.Rectangle $x, $y, $w, $h), [System.Drawing.GraphicsUnit]::Pixel)
$bmp.Save($out, [System.Drawing.Imaging.ImageFormat]::Png)
$g.Dispose(); $bmp.Dispose(); $src.Dispose()
