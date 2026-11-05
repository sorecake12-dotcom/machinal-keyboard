Add-Type -AssemblyName System.Drawing

$src = [System.Drawing.Bitmap]::FromFile('d:\mechanical-keyboard-vsc-master\logo.jpg')
$sizes = @(16, 32, 48, 64, 128, 256)
$pngStreams = @()

foreach ($s in $sizes) {
    $bmp = New-Object System.Drawing.Bitmap $s, $s
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
    $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::HighQuality
    $g.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
    $g.DrawImage($src, 0, 0, $s, $s)
    $g.Dispose()
    $ms = New-Object System.IO.MemoryStream
    $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
    $bmp.Dispose()
    $pngStreams += ,$ms.ToArray()
}
$src.Dispose()

$fs = [System.IO.File]::Create('d:\mechanical-keyboard-vsc-master\app_icon.ico')
$bw = New-Object System.IO.BinaryWriter $fs

# Header: 2 bytes reserved, 2 bytes type (1=icon), 2 bytes count
$bw.Write([byte]0); $bw.Write([byte]0)
$bw.Write([int16]1)
$bw.Write([int16]$sizes.Count)

$offset = 6 + (16 * $sizes.Count)
for ($i = 0; $i -lt $sizes.Count; $i++) {
    $w = if ($sizes[$i] -eq 256) { [byte]0 } else { [byte]$sizes[$i] }
    $h = $w
    $len = $pngStreams[$i].Length
    $bw.Write($w)
    $bw.Write($h)
    $bw.Write([byte]0) # Color palette count (0 for >= 256)
    $bw.Write([byte]0) # Reserved
    $bw.Write([int16]1) # Planes
    $bw.Write([int16]32) # Bits per pixel
    $bw.Write([int32]$len) # Image size in bytes
    $bw.Write([int32]$offset) # Offset
    $offset += $len
}

# Image Data
for ($i = 0; $i -lt $sizes.Count; $i++) {
    $bw.Write($pngStreams[$i])
}

$bw.Close()
$fs.Close()

Write-Output "app_icon.ico built successfully. Size: $((Get-Item 'd:\mechanical-keyboard-vsc-master\app_icon.ico').Length) bytes"
