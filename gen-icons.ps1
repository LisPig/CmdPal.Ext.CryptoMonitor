# Regenerate CryptoMonitor logo assets (indigo gradient + rising chart + gold coin dot).
Add-Type -AssemblyName System.Drawing

$out = Join-Path $PSScriptRoot "CmdPal.Ext.CryptoMonitor\Assets"
if (-not (Test-Path $out)) { throw "assets dir not found: $out" }

function Save-Logo([int]$w, [int]$h, [string]$file) {
    $bmp = New-Object System.Drawing.Bitmap($w, $h)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = 'AntiAlias'
    $rect = New-Object System.Drawing.Rectangle(0, 0, $w, $h)

    # rounded-rect clip
    $path = New-Object System.Drawing.Drawing2D.GraphicsPath
    $r = [Math]::Max(4, [int]($w * 0.18))
    $d = $r * 2
    $path.AddArc(0, 0, $d, $d, 180, 90)
    $path.AddArc($w - $d, 0, $d, $d, 270, 90)
    $path.AddArc($w - $d, $h - $d, $d, $d, 0, 90)
    $path.AddArc(0, $h - $d, $d, $d, 90, 90)
    $path.CloseFigure()
    $g.SetClip($path)

    # vertical gradient: deep indigo -> violet
    $brush = New-Object System.Drawing.Drawing2D.LinearGradientBrush(
        $rect,
        [System.Drawing.Color]::FromArgb(255, 30, 27, 75),
        [System.Drawing.Color]::FromArgb(255, 109, 40, 217),
        90.0)
    $g.FillRectangle($brush, $rect)

    # grid hint lines (very subtle)
    $gridPen = New-Object System.Drawing.Pen([System.Drawing.Color]::FromArgb(40, 255, 255, 255), [Math]::Max(1.0, $w * 0.006))
    for ($i = 1; $i -lt 4; $i++) {
        $y = $h * $i / 4
        $g.DrawLine($gridPen, $w * 0.12, $y, $w * 0.88, $y)
    }

    # rising chart polyline (white, glow-ish by double draw)
    $s = [Math]::Max(2.0, $w * 0.055)
    $pen = New-Object System.Drawing.Pen([System.Drawing.Color]::White, $s)
    $pen.StartCap = 'Round'; $pen.EndCap = 'Round'; $pen.LineJoin = 'Round'
    $pts = @(
        [System.Drawing.PointF]::new([float]($w * 0.14), [float]($h * 0.72)),
        [System.Drawing.PointF]::new([float]($w * 0.32), [float]($h * 0.55)),
        [System.Drawing.PointF]::new([float]($w * 0.50), [float]($h * 0.62)),
        [System.Drawing.PointF]::new([float]($w * 0.66), [float]($h * 0.38)),
        [System.Drawing.PointF]::new([float]($w * 0.78), [float]($h * 0.30))
    )
    $g.DrawLines($pen, $pts)

    # gold coin dot at the trend tip with darker ring
    $cx = $w * 0.84; $cy = $h * 0.26
    $cr = [Math]::Max(3.0, $w * 0.075)
    $ringPen = New-Object System.Drawing.Pen([System.Drawing.Color]::FromArgb(255, 180, 120, 20), [Math]::Max(1.5, $w * 0.02))
    $gold = New-Object System.Drawing.SolidBrush([System.Drawing.Color]::FromArgb(255, 252, 211, 77))
    $g.FillEllipse($gold, $cx - $cr, $cy - $cr, $cr * 2, $cr * 2)
    $g.DrawEllipse($ringPen, $cx - $cr, $cy - $cr, $cr * 2, $cr * 2)

    $g.ResetClip()
    $g.Dispose()
    $bmp.Save($file, [System.Drawing.Imaging.ImageFormat]::Png)
    $bmp.Dispose()
    Write-Host "  $file"
}

Write-Host "generating into $out"
# Square app tile: base + scale-200
Save-Logo 150 150 (Join-Path $out "Square150x150Logo.png")
Save-Logo 300 300 (Join-Path $out "Square150x150Logo.scale-200.png")
# Taskbar/small icon
Save-Logo 44 44 (Join-Path $out "Square44x44Logo.png")
Save-Logo 88 88 (Join-Path $out "Square44x44Logo.scale-200.png")
Save-Logo 24 24 (Join-Path $out "Square44x44Logo.targetsize-24_altform-unplated.png")
# Store/extension icon (used inside the palette)
Save-Logo 50 50 (Join-Path $out "StoreLogo.png")
Save-Logo 100 100 (Join-Path $out "StoreLogo.scale-200.png")
# Wide tile
Save-Logo 310 150 (Join-Path $out "Wide310x150Logo.png")
Save-Logo 620 300 (Join-Path $out "Wide310x150Logo.scale-200.png")
Write-Host "done"
