# Генерирует app.ico с адаптивным знаком: «DPI» для крупных размеров,
# «D» для мелких (трей 16–32 px). Запускать из PowerShell:
#   powershell -ExecutionPolicy Bypass -File tools\make-icon.ps1

Add-Type -AssemblyName System.Drawing

$root = Split-Path -Parent $PSScriptRoot
$icoPath = Join-Path $root 'app.ico'
$previewDir = Join-Path $env:TEMP 'zapret-icon-preview'
New-Item -ItemType Directory -Force $previewDir | Out-Null

function New-MarkPath([string]$mark, [System.Drawing.Font]$font, [int]$size) {
    $glyphs = New-Object 'System.Collections.Generic.List[System.Drawing.Drawing2D.GraphicsPath]'
    $widths = New-Object 'System.Collections.Generic.List[float]'
    $gap = if ($mark.Length -gt 1) { $size * 0.018 } else { 0 }

    foreach ($ch in $mark.ToCharArray()) {
        $glyph = New-Object System.Drawing.Drawing2D.GraphicsPath
        $glyph.AddString(
            [string]$ch,
            $font.FontFamily,
            [int]$font.Style,
            $font.Size,
            [System.Drawing.PointF]::Empty,
            [System.Drawing.StringFormat]::GenericTypographic)
        $widths.Add($glyph.GetBounds().Width)
        $glyphs.Add($glyph)
    }

    $total = ($widths | Measure-Object -Sum).Sum + $gap * ($mark.Length - 1)
    $cursor = ($size - $total) / 2
    $union = [System.Drawing.RectangleF]::Empty

    for ($i = 0; $i -lt $glyphs.Count; $i++) {
        $b = $glyphs[$i].GetBounds()
        $move = New-Object System.Drawing.Drawing2D.Matrix
        $move.Translate([float]($cursor - $b.X), 0)
        $glyphs[$i].Transform($move)

        $moved = $glyphs[$i].GetBounds()
        $union = if ($union.IsEmpty) { $moved } else { [System.Drawing.RectangleF]::Union($union, $moved) }
        $cursor += $widths[$i] + $gap
    }

    $center = New-Object System.Drawing.Drawing2D.Matrix
    $center.Translate(0, [float](($size - $union.Height) / 2 - $union.Y))

    $result = New-Object System.Drawing.Drawing2D.GraphicsPath
    foreach ($glyph in $glyphs) {
        $glyph.Transform($center)
        $result.AddPath($glyph, $false)
        $glyph.Dispose()
    }

    return $result
}

function New-IconPng([int]$size) {
    $bmp = New-Object System.Drawing.Bitmap($size, $size)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $g.TextRenderingHint = [System.Drawing.Text.TextRenderingHint]::AntiAliasGridFit
    $g.Clear([System.Drawing.Color]::Transparent)

    $bg = New-Object System.Drawing.SolidBrush([System.Drawing.Color]::FromArgb(235, 7, 4, 10))
    $g.FillEllipse($bg, 1, 1, $size - 2, $size - 2)

    $neon = [System.Drawing.Color]::FromArgb(255, 255, 45, 111)
    $ring = New-Object System.Drawing.Pen($neon, [float][Math]::Max(1.6, $size / 18))
    $g.DrawEllipse($ring, 2.5, 2.5, $size - 5, $size - 5)

    $mark = if ($size -ge 48) { 'DPI' } else { 'D' }
    $em = if ($size -ge 48) { $size * 0.34 } else { $size * 0.55 }
    $font = New-Object System.Drawing.Font('Bahnschrift', [float]$em, [System.Drawing.FontStyle]::Bold, [System.Drawing.GraphicsUnit]::Pixel)
    $path = New-MarkPath $mark $font $size
    $brush = New-Object System.Drawing.SolidBrush($neon)

    if ($size -ge 48) {
        $cyan = New-Object System.Drawing.SolidBrush([System.Drawing.Color]::FromArgb(150, 0, 229, 255))
        $ghost = $path.Clone()
        $ghostMatrix = New-Object System.Drawing.Drawing2D.Matrix
        $ghostMatrix.Translate([float](-$size * 0.014), [float]($size * 0.006))
        $ghost.Transform($ghostMatrix)
        $g.FillPath($cyan, $ghost)

        $thickness = [float][Math]::Max(1, $size / 96)
        $cyanLine = New-Object System.Drawing.Pen([System.Drawing.Color]::FromArgb(180, 0, 229, 255), $thickness)
        $pinkLine = New-Object System.Drawing.Pen([System.Drawing.Color]::FromArgb(160, 255, 45, 111), $thickness)

        $g.DrawLine($cyanLine, [float]($size * 0.18), [float]($size * 0.30), [float]($size * 0.42), [float]($size * 0.30))
        $g.DrawLine($cyanLine, [float]($size * 0.52), [float]($size * 0.30), [float]($size * 0.80), [float]($size * 0.30))
        $g.DrawLine($pinkLine, [float]($size * 0.22), [float]($size * 0.71), [float]($size * 0.40), [float]($size * 0.71))
        $g.DrawLine($pinkLine, [float]($size * 0.58), [float]($size * 0.71), [float]($size * 0.76), [float]($size * 0.71))

        $pixel = New-Object System.Drawing.SolidBrush([System.Drawing.Color]::FromArgb(190, 0, 229, 255))
        $g.FillRectangle($pixel, [float]($size * 0.70), [float]($size * 0.56), [float]($size * 0.05), [float]($size * 0.015))
    }

    $g.FillPath($brush, $path)
    $g.Dispose()

    $stream = New-Object System.IO.MemoryStream
    $bmp.Save($stream, [System.Drawing.Imaging.ImageFormat]::Png)
    $bmp.Dispose()
    return $stream.ToArray()
}

$sizes = @(16, 32, 48, 256)
$images = New-Object 'System.Collections.Generic.List[byte[]]'

foreach ($size in $sizes) {
    [byte[]]$png = New-IconPng $size
    $images.Add($png)
    [System.IO.File]::WriteAllBytes((Join-Path $previewDir "$size.png"), $png)
}

$stream = New-Object System.IO.MemoryStream
$writer = New-Object System.IO.BinaryWriter($stream)
$writer.Write([UInt16]0)
$writer.Write([UInt16]1)
$writer.Write([UInt16]$images.Count)

$offset = 6 + 16 * $images.Count
for ($i = 0; $i -lt $images.Count; $i++) {
    $size = $sizes[$i]
    $png = $images[$i]
    $dimension = if ($size -ge 256) { 0 } else { $size }

    $writer.Write([Byte]$dimension)
    $writer.Write([Byte]$dimension)
    $writer.Write([Byte]0)
    $writer.Write([Byte]0)
    $writer.Write([UInt16]1)
    $writer.Write([UInt16]32)
    $writer.Write([UInt32]$png.Length)
    $writer.Write([UInt32]$offset)
    $offset += $png.Length
}

foreach ($png in $images) {
    $writer.Write($png)
}

$writer.Flush()
[System.IO.File]::WriteAllBytes($icoPath, $stream.ToArray())

Write-Output ("ICO: {0} ({1} bytes)" -f $icoPath, (Get-Item $icoPath).Length)
Write-Output ("Preview: {0}" -f $previewDir)
