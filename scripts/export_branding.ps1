Add-Type -AssemblyName System.Drawing

$destDir = "dist-branding/winflow"
New-Item -ItemType Directory -Force -Path $destDir | Out-Null
New-Item -ItemType Directory -Force -Path "$destDir/png" | Out-Null

function Create-FluentIconBitmap([int]$size) {
    $bmp = New-Object System.Drawing.Bitmap($size, $size, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $g.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
    $g.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality

    $scale = [float]($size / 256.0)

    # 1. Subtle acoustic bloom
    $bloomPath = New-Object System.Drawing.Drawing2D.GraphicsPath
    $bx = [float](128.0 * $scale - 84.0 * $scale)
    $by = [float](132.0 * $scale - 46.0 * $scale)
    $bw = [float](168.0 * $scale)
    $bh = [float](92.0 * $scale)
    $bloomPath.AddEllipse($bx, $by, $bw, $bh)
    $pbg = New-Object System.Drawing.Drawing2D.PathGradientBrush($bloomPath)
    $pbg.CenterColor = [System.Drawing.Color]::FromArgb(90, 0, 120, 212)
    $pbg.SurroundColors = @([System.Drawing.Color]::FromArgb(0, 0, 0, 0))
    $g.FillEllipse($pbg, $bx, $by, $bw, $bh)
    $pbg.Dispose()
    $bloomPath.Dispose()

    # Helper function for rounded rect
    function Get-RoundedRect([float]$x, [float]$y, [float]$w, [float]$h, [float]$r) {
        $path = New-Object System.Drawing.Drawing2D.GraphicsPath
        $d = [float]($r * 2.0)
        $path.AddArc($x, $y, $d, $d, 180.0, 90.0)
        $path.AddArc([float]($x + $w - $d), $y, $d, $d, 270.0, 90.0)
        $path.AddArc([float]($x + $w - $d), [float]($y + $h - $d), $d, $d, 0.0, 90.0)
        $path.AddArc($x, [float]($y + $h - $d), $d, $d, 90.0, 90.0)
        $path.CloseFigure()
        return $path
    }

    # 2. Base Squircle Plate (28 to 228 -> 200x200, rx=46)
    $platePath = Get-RoundedRect (28.0 * $scale) (28.0 * $scale) (200.0 * $scale) (200.0 * $scale) (46.0 * $scale)
    $pt1 = New-Object System.Drawing.PointF(0.0, 0.0)
    $pt2 = New-Object System.Drawing.PointF([float]$size, [float]$size)
    $plateBrush = New-Object System.Drawing.Drawing2D.LinearGradientBrush(
        $pt1,
        $pt2,
        [System.Drawing.Color]::FromArgb(242, 44, 51, 64),
        [System.Drawing.Color]::FromArgb(250, 18, 22, 29)
    )
    $g.FillPath($plateBrush, $platePath)
    $plateBrush.Dispose()

    # 3. Specular Rim Light
    $rimPen = New-Object System.Drawing.Pen([System.Drawing.Color]::FromArgb(150, 255, 255, 255), [Math]::Max(1.0, 1.5 * $scale))
    $g.DrawPath($rimPen, $platePath)
    $rimPen.Dispose()
    $platePath.Dispose()

    # 4. Subsurface Soft Acoustic Trench (50 to 206 -> 156x24, rx=12)
    $trenchPath = Get-RoundedRect (50.0 * $scale) (116.0 * $scale) (156.0 * $scale) (24.0 * $scale) (12.0 * $scale)
    $trenchBrush = New-Object System.Drawing.SolidBrush([System.Drawing.Color]::FromArgb(90, 0, 0, 0))
    $g.FillPath($trenchBrush, $trenchPath)
    $trenchBrush.Dispose()
    $trenchPen = New-Object System.Drawing.Pen([System.Drawing.Color]::FromArgb(40, 255, 255, 255), [Math]::Max(0.5, 1.0 * $scale))
    $g.DrawPath($trenchPen, $trenchPath)
    $trenchPen.Dispose()
    $trenchPath.Dispose()

    # 5. 7 Symmetric HUD Soundwave Bars
    $heights = @(22.0, 42.0, 64.0, 88.0, 64.0, 42.0, 22.0)
    $xPositions = @(62.0, 82.0, 102.0, 122.0, 142.0, 162.0, 182.0)
    $barW = [float](12.0 * $scale)
    $barR = [float](6.0 * $scale)

    for ($i = 0; $i -lt 7; $i++) {
        $h = [float]($heights[$i] * $scale)
        $x = [float]($xPositions[$i] * $scale)
        $y = [float]((128.0 * $scale) - ($h / 2.0))
        $r = [Math]::Min($barR, [float]($h / 2.0))

        $barPath = Get-RoundedRect $x $y $barW $h $r

        $bpt1 = New-Object System.Drawing.PointF($x, $y)
        $bpt2 = New-Object System.Drawing.PointF($x, [float]($y + $h))

        if ($i -eq 3) {
            # Center Peak bar
            $barBrush = New-Object System.Drawing.Drawing2D.LinearGradientBrush(
                $bpt1,
                $bpt2,
                [System.Drawing.Color]::FromArgb(255, 255, 255, 255),
                [System.Drawing.Color]::FromArgb(210, 110, 167, 230)
            )
            $g.FillPath($barBrush, $barPath)
            $barBrush.Dispose()

            $barStroke = New-Object System.Drawing.Pen([System.Drawing.Color]::FromArgb(230, 255, 255, 255), [Math]::Max(0.8, 1.0 * $scale))
            $g.DrawPath($barStroke, $barPath)
            $barStroke.Dispose()
        } else {
            $barBrush = New-Object System.Drawing.Drawing2D.LinearGradientBrush(
                $bpt1,
                $bpt2,
                [System.Drawing.Color]::FromArgb(250, 255, 255, 255),
                [System.Drawing.Color]::FromArgb(180, 168, 208, 248)
            )
            $g.FillPath($barBrush, $barPath)
            $barBrush.Dispose()

            $barStroke = New-Object System.Drawing.Pen([System.Drawing.Color]::FromArgb(130, 255, 255, 255), [Math]::Max(0.5, 0.8 * $scale))
            $g.DrawPath($barStroke, $barPath)
            $barStroke.Dispose()
        }

        $barPath.Dispose()
    }

    # 6. Bottom curved glass sheen
    $sheenPen = New-Object System.Drawing.Pen([System.Drawing.Color]::FromArgb(70, 255, 255, 255), [Math]::Max(1.0, 1.2 * $scale))
    $g.DrawArc($sheenPen, [float](46.0 * $scale), [float](140.0 * $scale), [float](164.0 * $scale), [float](60.0 * $scale), 30.0, 120.0)
    $sheenPen.Dispose()

    $g.Dispose()
    return $bmp
}

$sizes = @(256, 128, 64, 48, 32, 24, 16)
$pngBytesList = @()

foreach ($s in $sizes) {
    $bmp = Create-FluentIconBitmap $s
    $ms = New-Object System.IO.MemoryStream
    $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
    $bytes = $ms.ToArray()
    $ms.Dispose()
    $bmp.Dispose()

    $pngBytesList += [PSCustomObject]@{ Size = $s; Bytes = $bytes }

    # Save to png folder
    [System.IO.File]::WriteAllBytes("$destDir/png/winflow-$s.png", $bytes)
    if ($s -eq 256) {
        [System.IO.File]::WriteAllBytes("$destDir/logo.png", $bytes)
    }
}

# Write multi-resolution .ico file into destDir
$icoStream = [System.IO.File]::Create("$destDir/app.ico")
$bw = New-Object System.IO.BinaryWriter($icoStream)

# Header
$bw.Write([int16]0) # Reserved
$bw.Write([int16]1) # Type 1 = ICO
$bw.Write([int16]$pngBytesList.Count)

$offset = 6 + (16 * $pngBytesList.Count)

foreach ($item in $pngBytesList) {
    $s = if ($item.Size -eq 256) { 0 } else { $item.Size }
    $bw.Write([byte]$s)
    $bw.Write([byte]$s)
    $bw.Write([byte]0) # Palette colors
    $bw.Write([byte]0) # Reserved
    $bw.Write([int16]1) # Color planes
    $bw.Write([int16]32) # Bits per pixel
    $bw.Write([int32]$item.Bytes.Length) # Size
    $bw.Write([int32]$offset) # Offset
    $offset += $item.Bytes.Length
}

foreach ($item in $pngBytesList) {
    $bw.Write($item.Bytes)
}

$bw.Dispose()
$icoStream.Dispose()

# Copy master SVG
Copy-Item "Assets/logo.svg" -Destination "$destDir/logo.svg"

Write-Host "Branding assets exported successfully to $destDir!"
