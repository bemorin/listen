# Renders the Listen app icon (dark glass tile, waveform in the signature gradient) to PNGs.
# Usage (Windows PowerShell 5.1, which ships System.Drawing):
#   powershell -File make-icon.ps1 -OutDir <dir>
# Then `python make-ico.py <dir>` packs listen.ico. The outputs are committed; rerun only to
# change the design.
param([string]$OutDir = $PSScriptRoot)
Add-Type -AssemblyName System.Drawing

function New-RoundedPath([single]$x, [single]$y, [single]$w, [single]$h, [single]$r) {
    $p = New-Object System.Drawing.Drawing2D.GraphicsPath
    $d = $r * 2
    $p.AddArc($x, $y, $d, $d, 180, 90)
    $p.AddArc($x + $w - $d, $y, $d, $d, 270, 90)
    $p.AddArc($x + $w - $d, $y + $h - $d, $d, $d, 0, 90)
    $p.AddArc($x, $y + $h - $d, $d, $d, 90, 90)
    $p.CloseFigure()
    return $p
}

function Render([int]$size, [string]$path) {
    $bmp = New-Object System.Drawing.Bitmap($size, $size, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = 'AntiAlias'
    $g.PixelOffsetMode = 'HighQuality'
    $g.Clear([System.Drawing.Color]::Transparent)

    $m = [single]($size * 0.06)
    $s = [single]($size - 2 * $m)
    $tile = New-RoundedPath $m $m $s $s ([single]($s * 0.24))

    # Dark glass tile: a soft vertical gradient, a highlight on the upper edge.
    $rect = New-Object System.Drawing.RectangleF($m, $m, $s, $s)
    $bg = New-Object System.Drawing.Drawing2D.LinearGradientBrush($rect,
        [System.Drawing.Color]::FromArgb(255, 44, 44, 50), [System.Drawing.Color]::FromArgb(255, 14, 14, 17), 90)
    $g.FillPath($bg, $tile)

    # Inner glow behind the waveform.
    $glow = New-Object System.Drawing.Drawing2D.GraphicsPath
    $gs = $s * 0.78
    $glow.AddEllipse([single]($size / 2 - $gs / 2), [single]($size / 2 - $gs / 2), [single]$gs, [single]$gs)
    $pgb = New-Object System.Drawing.Drawing2D.PathGradientBrush($glow)
    $pgb.CenterColor = [System.Drawing.Color]::FromArgb(70, 150, 140, 255)
    $pgb.SurroundColors = @([System.Drawing.Color]::FromArgb(0, 150, 140, 255))
    $g.SetClip($tile)
    $g.FillPath($pgb, $glow)
    $g.ResetClip()

    $edge = New-Object System.Drawing.Pen([System.Drawing.Color]::FromArgb(46, 255, 255, 255), [single][Math]::Max(1, $size / 128))
    $g.DrawPath($edge, $tile)

    # Waveform: five rounded bars in the periwinkle / lavender / aqua gradient.
    $heights = @(0.36, 0.68, 1.0, 0.58, 0.32)
    $barW = [single]($s * 0.085)
    $gap = [single]($s * 0.06)
    $total = 5 * $barW + 4 * $gap
    $x0 = [single]($size / 2 - $total / 2)
    $maxH = [single]($s * 0.5)
    $grad = New-Object System.Drawing.Drawing2D.LinearGradientBrush(
        (New-Object System.Drawing.RectangleF($x0, [single]($size / 2 - $maxH / 2), $total, $maxH)),
        [System.Drawing.Color]::FromArgb(255, 142, 155, 255), [System.Drawing.Color]::FromArgb(255, 127, 221, 230), 45)
    $blend = New-Object System.Drawing.Drawing2D.ColorBlend(3)
    $blend.Colors = @([System.Drawing.Color]::FromArgb(255, 142, 155, 255), [System.Drawing.Color]::FromArgb(255, 197, 155, 255), [System.Drawing.Color]::FromArgb(255, 127, 221, 230))
    $blend.Positions = @([single]0, [single]0.5, [single]1)
    $grad.InterpolationColors = $blend
    for ($i = 0; $i -lt 5; $i++) {
        $h = [single]($maxH * $heights[$i])
        $bx = [single]($x0 + $i * ($barW + $gap))
        $by = [single]($size / 2 - $h / 2)
        $bar = New-RoundedPath $bx $by $barW $h ([single]($barW / 2))
        $g.FillPath($grad, $bar)
    }
    $g.Dispose()
    $bmp.Save($path, [System.Drawing.Imaging.ImageFormat]::Png)
    $bmp.Dispose()
}

foreach ($size in 16, 24, 32, 48, 64, 128, 256, 512) {
    Render $size (Join-Path $OutDir ("icon-{0}.png" -f $size))
}
"icons written to $OutDir"
