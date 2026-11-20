# Renders the Listen app icon to PNGs: a silver disc on a transparent background, with a
# sound wave carved through it (its peaks draw an M). Small sizes get fewer oscillations
# and a thicker line so the wave stays legible.
# Usage (Windows PowerShell 5.1, which ships System.Drawing):
#   powershell -File make-icon.ps1 -OutDir <dir>
# Then `python make-ico.py <dir>` packs listen.ico. The outputs are committed; rerun only to
# change the design.
param([string]$OutDir = $PSScriptRoot)
Add-Type -AssemblyName System.Drawing

function Render([int]$size, [string]$path) {
    $bmp = New-Object System.Drawing.Bitmap($size, $size, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = 'AntiAlias'
    $g.PixelOffsetMode = 'HighQuality'
    $g.Clear([System.Drawing.Color]::Transparent)

    $d = [single]($size * 0.92)
    $x = [single](($size - $d) / 2)
    $cx = [single]($size / 2)
    $cy = [single]($size / 2)

    # Silver: bright upper left, cooler lower right.
    $silver = New-Object System.Drawing.Drawing2D.LinearGradientBrush(
        (New-Object System.Drawing.RectangleF($x, $x, $d, $d)), [System.Drawing.Color]::White,
        [System.Drawing.Color]::FromArgb(255, 160, 160, 166), 60)
    $blend = New-Object System.Drawing.Drawing2D.ColorBlend(3)
    $blend.Colors = @([System.Drawing.Color]::FromArgb(255, 255, 255, 255), [System.Drawing.Color]::FromArgb(255, 228, 228, 232), [System.Drawing.Color]::FromArgb(255, 158, 158, 164))
    $blend.Positions = @([single]0, [single]0.55, [single]1)
    $silver.InterpolationColors = $blend
    $g.FillEllipse($silver, $x, $x, $d, $d)

    # A faint edge so the disc holds its shape on light taskbars.
    $edge = New-Object System.Drawing.Pen([System.Drawing.Color]::FromArgb(56, 0, 0, 0), [single][Math]::Max(1, $size / 96))
    $g.DrawEllipse($edge, $x, $x, $d, $d)

    # The carved wave: a burst whose envelope peaks in the middle.
    $disc = New-Object System.Drawing.Drawing2D.GraphicsPath
    $disc.AddEllipse($x, $x, $d, $d)
    $g.SetClip($disc)
    $cycles = if ($size -le 24) { 5 } elseif ($size -le 48) { 6 } else { 7 }
    $amp = if ($size -le 24) { 0.27 } else { 0.21 }
    $pts = New-Object System.Collections.Generic.List[System.Drawing.PointF]
    for ($i = 0; $i -le 240; $i++) {
        $t = $i / 240.0
        $px = $cx - $d / 2 + $t * $d
        $env = [Math]::Pow([Math]::Sin([Math]::PI * $t), 2.2)
        $py = $cy - [Math]::Sin($t * [Math]::PI * $cycles) * $env * $d * $amp
        $pts.Add((New-Object System.Drawing.PointF([single]$px, [single]$py)))
    }
    $width = [single][Math]::Max($size * 0.05, 1.6)
    $pen = New-Object System.Drawing.Pen([System.Drawing.Color]::FromArgb(255, 14, 14, 15), $width)
    $pen.StartCap = 'Round'; $pen.EndCap = 'Round'; $pen.LineJoin = 'Round'
    $g.DrawLines($pen, $pts.ToArray())
    $g.ResetClip()

    $g.Dispose()
    $bmp.Save($path, [System.Drawing.Imaging.ImageFormat]::Png)
    $bmp.Dispose()
}

foreach ($size in 16, 24, 32, 48, 64, 128, 256, 512) {
    Render $size (Join-Path $OutDir ("icon-{0}.png" -f $size))
}
"icons written to $OutDir"
