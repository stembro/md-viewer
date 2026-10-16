[CmdletBinding()]
param(
    [string] $OutputPath = (Join-Path $PSScriptRoot 'app.ico'),
    [string] $PreviewPath
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

# Matches app.svg and MainWindow.Icon. Render at 4x for clean small-size antialiasing.
# ICO PNG entries are supported on Windows Vista and later, including Windows 10/11.
$sizes = @(16, 32, 48, 256)
$images = [System.Collections.Generic.List[byte[]]]::new()
$green = [System.Drawing.SolidBrush]::new([System.Drawing.ColorTranslator]::FromHtml('#376a54'))
$white = [System.Drawing.SolidBrush]::new([System.Drawing.Color]::White)

function New-Polygon([double[][]] $Coordinates) {
    $points = [System.Drawing.PointF[]]::new($Coordinates.Length)
    for ($pointIndex = 0; $pointIndex -lt $Coordinates.Length; $pointIndex++) {
        $points[$pointIndex] = [System.Drawing.PointF]::new($Coordinates[$pointIndex][0], $Coordinates[$pointIndex][1])
    }
    return ,$points
}

try {
    $letter = New-Polygon @(@(6,10), @(9,10), @(13,15), @(17,10), @(20,10), @(20,23), @(17,23), @(17,15), @(13,20), @(9,15), @(9,23), @(6,23))
    $arrow = New-Polygon @(@(23,10), @(26,10), @(26,18), @(29,18), @(24.5,23), @(20,18), @(23,18))

    foreach ($size in $sizes) {
        $large = [System.Drawing.Bitmap]::new($size * 4, $size * 4, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
        $graphics = [System.Drawing.Graphics]::FromImage($large)
        $shape = [System.Drawing.Drawing2D.GraphicsPath]::new()
        $small = [System.Drawing.Bitmap]::new($size, $size, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
        $resize = [System.Drawing.Graphics]::FromImage($small)
        $png = [System.IO.MemoryStream]::new()
        try {
            $graphics.Clear([System.Drawing.Color]::Transparent)
            $graphics.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
            $graphics.ScaleTransform($size * 4.0 / 32.0, $size * 4.0 / 32.0)
            $shape.AddArc(0, 0, 8, 8, 180, 90)
            $shape.AddArc(24, 0, 8, 8, 270, 90)
            $shape.AddArc(24, 24, 8, 8, 0, 90)
            $shape.AddArc(0, 24, 8, 8, 90, 90)
            $shape.CloseFigure()
            $graphics.FillPath($green, $shape)
            $graphics.FillPolygon($white, $letter)
            $graphics.FillPolygon($white, $arrow)

            $resize.CompositingMode = [System.Drawing.Drawing2D.CompositingMode]::SourceCopy
            $resize.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
            $resize.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
            $resize.DrawImage($large, [System.Drawing.Rectangle]::new(0, 0, $size, $size))
            $small.Save($png, [System.Drawing.Imaging.ImageFormat]::Png)
            $images.Add($png.ToArray())
            if ($size -eq 256 -and $PreviewPath) {
                [System.IO.File]::WriteAllBytes($PreviewPath, $png.ToArray())
            }
        }
        finally {
            $png.Dispose()
            $resize.Dispose()
            $small.Dispose()
            $shape.Dispose()
            $graphics.Dispose()
            $large.Dispose()
        }
    }

    $buffer = [System.IO.MemoryStream]::new()
    $writer = [System.IO.BinaryWriter]::new($buffer)
    try {
        $writer.Write([uint16] 0)
        $writer.Write([uint16] 1)
        $writer.Write([uint16] $sizes.Length)
        $offset = 6 + 16 * $sizes.Length
        for ($imageIndex = 0; $imageIndex -lt $sizes.Length; $imageIndex++) {
            $dimension = if ($sizes[$imageIndex] -eq 256) { 0 } else { $sizes[$imageIndex] }
            $writer.Write([byte] $dimension)
            $writer.Write([byte] $dimension)
            $writer.Write([byte] 0)
            $writer.Write([byte] 0)
            $writer.Write([uint16] 1)
            $writer.Write([uint16] 32)
            $writer.Write([uint32] $images[$imageIndex].Length)
            $writer.Write([uint32] $offset)
            $offset += $images[$imageIndex].Length
        }
        foreach ($image in $images) { $writer.Write($image) }
        $writer.Flush()
        [System.IO.File]::WriteAllBytes($OutputPath, $buffer.ToArray())
    }
    finally {
        $writer.Dispose()
        $buffer.Dispose()
    }
}
finally {
    $green.Dispose()
    $white.Dispose()
}

Write-Host "Created $OutputPath with 16, 32, 48, and 256 pixel images."
