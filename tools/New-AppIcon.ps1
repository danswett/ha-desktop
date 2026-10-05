# Generates src/HomeAssistant.Desktop/Assets/app.ico.
#
# Small frames are written as uncompressed DIBs and only 128/256 as PNG. A PNG-only
# .ico loads fine through the shell but breaks GDI+ (Icon.ToBitmap throws), and that
# is the path some tooling still takes.
#
# Run with Windows PowerShell 5.1:
#   powershell.exe -NoProfile -ExecutionPolicy Bypass -File tools\New-AppIcon.ps1 <out.ico>

param(
    [Parameter(Mandatory = $true)]
    [string]$OutputPath
)

Add-Type -AssemblyName System.Drawing

$ErrorActionPreference = 'Stop'

$sizes = 16, 20, 24, 32, 40, 48, 64, 128, 256
$pngSizes = 128, 256

function New-IconBitmap {
    param([int]$Size)

    $bmp = New-Object System.Drawing.Bitmap($Size, $Size, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $g.Clear([System.Drawing.Color]::Transparent)

    # Rounded square, vertical gradient.
    $r = [Math]::Max(2, [int]($Size * 0.22))
    $d = $r * 2
    $path = New-Object System.Drawing.Drawing2D.GraphicsPath
    $path.AddArc(0, 0, $d, $d, 180, 90)
    $path.AddArc($Size - $d, 0, $d, $d, 270, 90)
    $path.AddArc($Size - $d, $Size - $d, $d, $d, 0, 90)
    $path.AddArc(0, $Size - $d, $d, $d, 90, 90)
    $path.CloseFigure()

    $rect = New-Object System.Drawing.Rectangle(0, 0, $Size, $Size)
    $top = [System.Drawing.Color]::FromArgb(255, 36, 198, 247)
    $bottom = [System.Drawing.Color]::FromArgb(255, 2, 119, 189)
    $brush = New-Object System.Drawing.Drawing2D.LinearGradientBrush($rect, $top, $bottom, 90.0)
    $g.FillPath($brush, $path)

    # Generic house glyph, white, with the door punched back to the background colour
    # so it still reads as a house at 16px.
    $cx = $Size / 2.0
    $roofTop = $Size * 0.19
    $roofBottom = $Size * 0.52
    $halfRoof = $Size * 0.35
    $white = New-Object System.Drawing.SolidBrush([System.Drawing.Color]::White)

    $roof = New-Object System.Drawing.Drawing2D.GraphicsPath
    $roof.AddPolygon(@(
        (New-Object System.Drawing.PointF([float]$cx, [float]$roofTop)),
        (New-Object System.Drawing.PointF([float]($cx + $halfRoof), [float]$roofBottom)),
        (New-Object System.Drawing.PointF([float]($cx - $halfRoof), [float]$roofBottom))
    ))
    $g.FillPath($white, $roof)

    $bodyW = $Size * 0.46
    $bodyH = $Size * 0.27
    $bodyTop = $roofBottom - $Size * 0.02
    $g.FillRectangle($white, (New-Object System.Drawing.RectangleF(
        [float]($cx - $bodyW / 2), [float]$bodyTop, [float]$bodyW, [float]$bodyH)))

    $doorW = [Math]::Max(2.0, $Size * 0.15)
    $doorH = [Math]::Max(3.0, $Size * 0.17)
    $g.CompositingMode = [System.Drawing.Drawing2D.CompositingMode]::SourceCopy
    $g.FillRectangle(
        (New-Object System.Drawing.SolidBrush($bottom)),
        (New-Object System.Drawing.RectangleF(
            [float]($cx - $doorW / 2), [float]($bodyTop + $bodyH - $doorH), [float]$doorW, [float]$doorH)))

    $g.Dispose()
    $brush.Dispose(); $white.Dispose(); $path.Dispose(); $roof.Dispose()
    return $bmp
}

function ConvertTo-PngBytes {
    param([System.Drawing.Bitmap]$Bitmap)

    $ms = New-Object System.IO.MemoryStream
    $Bitmap.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
    $bytes = $ms.ToArray()
    $ms.Dispose()
    return , $bytes
}

function ConvertTo-DibBytes {
    param([System.Drawing.Bitmap]$Bitmap)

    $w = $Bitmap.Width
    $h = $Bitmap.Height

    $ms = New-Object System.IO.MemoryStream
    $bw = New-Object System.IO.BinaryWriter($ms)

    # BITMAPINFOHEADER. Height is doubled because the DIB holds the colour data and
    # the AND mask stacked together.
    $bw.Write([UInt32]40)
    $bw.Write([Int32]$w)
    $bw.Write([Int32]($h * 2))
    $bw.Write([UInt16]1)
    $bw.Write([UInt16]32)
    $bw.Write([UInt32]0)
    $bw.Write([UInt32]($w * $h * 4))
    $bw.Write([Int32]0); $bw.Write([Int32]0)
    $bw.Write([UInt32]0); $bw.Write([UInt32]0)

    $rect = New-Object System.Drawing.Rectangle(0, 0, $w, $h)
    $data = $Bitmap.LockBits($rect, [System.Drawing.Imaging.ImageLockMode]::ReadOnly,
        [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    try {
        $stride = $data.Stride
        $buffer = New-Object byte[] ($stride * $h)
        [System.Runtime.InteropServices.Marshal]::Copy($data.Scan0, $buffer, 0, $buffer.Length)

        # DIBs are bottom-up.
        for ($y = $h - 1; $y -ge 0; $y--) {
            $bw.Write($buffer, $y * $stride, $w * 4)
        }
    }
    finally {
        $Bitmap.UnlockBits($data)
    }

    # AND mask: zeroed, since the 32bpp alpha channel carries the transparency.
    $maskStride = [int]([Math]::Floor(($w + 31) / 32)) * 4
    $zeros = New-Object byte[] ($maskStride * $h)
    $bw.Write($zeros)

    $bw.Flush()
    $bytes = $ms.ToArray()
    $bw.Dispose(); $ms.Dispose()
    return , $bytes
}

$frames = @{}
foreach ($size in $sizes) {
    $bmp = New-IconBitmap -Size $size
    $frames[$size] = if ($pngSizes -contains $size) {
        ConvertTo-PngBytes -Bitmap $bmp
    }
    else {
        ConvertTo-DibBytes -Bitmap $bmp
    }
    $bmp.Dispose()
}

$out = New-Object System.IO.MemoryStream
$bw = New-Object System.IO.BinaryWriter($out)

# ICONDIR
$bw.Write([UInt16]0)
$bw.Write([UInt16]1)
$bw.Write([UInt16]$sizes.Count)

$offset = 6 + (16 * $sizes.Count)
foreach ($size in $sizes) {
    $bytes = $frames[$size]
    $dim = if ($size -ge 256) { 0 } else { $size }

    # ICONDIRENTRY
    $bw.Write([byte]$dim)
    $bw.Write([byte]$dim)
    $bw.Write([byte]0)
    $bw.Write([byte]0)
    $bw.Write([UInt16]1)
    $bw.Write([UInt16]32)
    $bw.Write([UInt32]$bytes.Length)
    $bw.Write([UInt32]$offset)

    $offset += $bytes.Length
}

foreach ($size in $sizes) {
    $bw.Write($frames[$size])
}

$bw.Flush()
$directory = Split-Path -Parent $OutputPath
if ($directory -and -not (Test-Path $directory)) {
    New-Item -ItemType Directory -Path $directory -Force | Out-Null
}
[System.IO.File]::WriteAllBytes($OutputPath, $out.ToArray())
$bw.Dispose(); $out.Dispose()

Write-Output ("Wrote {0} ({1} frames, {2} bytes)" -f $OutputPath, $sizes.Count, (Get-Item $OutputPath).Length)
