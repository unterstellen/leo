<#
.SYNOPSIS
  Renders the LEO logo (assets/leo.svg) into src/LEO.App/Assets: leo.ico (16-256 px) + PNGs.

.DESCRIPTION
  Reads the logo straight from assets/leo.svg and draws it with GDI+, so no external tools are needed:
  the visible circle (radial gradient) and the dark band path (linear gradient). The SVG's hidden layers
  (display: none) are ignored. The path parser handles what the logo uses: M/m, C/c, L/l, H/h, V/v, Z.
  Re-run after changing the logo:  powershell -File tools\make-icon.ps1
#>
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

$root = Split-Path $PSScriptRoot -Parent
$svgPath = Join-Path $root 'assets\leo.svg'
$outDir = Join-Path $root 'src\LEO.App\Assets'
New-Item -ItemType Directory -Force $outDir | Out-Null

[xml]$svg = Get-Content $svgPath -Raw
$ns = New-Object System.Xml.XmlNamespaceManager $svg.NameTable
$ns.AddNamespace('s', 'http://www.w3.org/2000/svg')
$inv = [System.Globalization.CultureInfo]::InvariantCulture
function F([string]$v) { [double]::Parse($v, $inv) }
function C([string]$hex) {
    $h = $hex.Trim().TrimStart('#')
    if ($h.Length -eq 3) { $h = -join ($h.ToCharArray() | ForEach-Object { "$_$_" }) } # "#000" → "000000"
    if ($h -notmatch '^[0-9a-fA-F]{6}$') { # named colours ("gray", "white", …)
        $n = [System.Drawing.Color]::FromName($hex.Trim())
        if (-not $n.IsKnownColor) { throw "Unsupported colour '$hex' in assets/leo.svg" }
        return [System.Drawing.Color]::FromArgb(255, $n.R, $n.G, $n.B)
    }
    [System.Drawing.Color]::FromArgb(255, [Convert]::ToInt32($h.Substring(0, 2), 16), [Convert]::ToInt32($h.Substring(2, 2), 16), [Convert]::ToInt32($h.Substring(4, 2), 16))
}
function Stops($gradient) { @($gradient.SelectNodes('s:stop', $ns) | ForEach-Object { C $_.'stop-color' }) }

# gradientTransform: a list of translate / scale / rotate, applied right to left (SVG order).
function Get-Matrix([string]$transform) {
    $m = New-Object System.Drawing.Drawing2D.Matrix
    foreach ($t in [regex]::Matches($transform, '(\w+)\(([^)]*)\)')) {
        $a = @($t.Groups[2].Value -split '[\s,]+' | Where-Object { $_ } | ForEach-Object { [float](F $_) })
        switch ($t.Groups[1].Value) {
            'translate' { $m.Translate($a[0], $(if ($a.Count -gt 1) { $a[1] } else { 0 })) }
            'scale' { $m.Scale($a[0], $(if ($a.Count -gt 1) { $a[1] } else { $a[0] })) }
            'rotate' { $m.Rotate($a[0]) }
        }
    }
    $m
}
function Map($matrix, [double]$x, [double]$y) {
    $pts = [System.Drawing.PointF[]]@((New-Object System.Drawing.PointF ([float]$x), ([float]$y)))
    $matrix.TransformPoints($pts)
    $pts[0]
}

# SVG path data → GraphicsPath (absolute and relative M, C, L, H, V, Z).
function Get-PathFromSvg([string]$d) {
    $p = New-Object System.Drawing.Drawing2D.GraphicsPath
    $tokens = [regex]::Matches($d, '[MmCcLlHhVvZz]|-?(?:\d+\.?\d*|\.\d+)(?:[eE][-+]?\d+)?') | ForEach-Object { $_.Value }
    $i = 0; $cmd = ''; $x = 0.0; $y = 0.0; $sx = 0.0; $sy = 0.0
    function Next { $script:i++; F $tokens[$script:i - 1] }
    $script:i = 0
    while ($script:i -lt $tokens.Count) {
        if ($tokens[$script:i] -match '^[A-Za-z]$') { $cmd = $tokens[$script:i]; $script:i++ }
        $rel = $cmd -cmatch '^[a-z]$'
        switch -CaseSensitive ($cmd.ToUpperInvariant()) {
            'M' {
                $nx = Next; $ny = Next
                if ($rel) { $nx += $x; $ny += $y }
                $p.StartFigure(); $x = $nx; $y = $ny; $sx = $x; $sy = $y
                $cmd = if ($rel) { 'l' } else { 'L' } # further pairs are line-tos
            }
            'L' {
                $nx = Next; $ny = Next
                if ($rel) { $nx += $x; $ny += $y }
                $p.AddLine([float]$x, [float]$y, [float]$nx, [float]$ny); $x = $nx; $y = $ny
            }
            'H' { $nx = Next; if ($rel) { $nx += $x }; $p.AddLine([float]$x, [float]$y, [float]$nx, [float]$y); $x = $nx }
            'V' { $ny = Next; if ($rel) { $ny += $y }; $p.AddLine([float]$x, [float]$y, [float]$x, [float]$ny); $y = $ny }
            'C' {
                $c = @(); for ($k = 0; $k -lt 6; $k++) { $c += Next }
                if ($rel) { $c[0] += $x; $c[1] += $y; $c[2] += $x; $c[3] += $y; $c[4] += $x; $c[5] += $y }
                $p.AddBezier([float]$x, [float]$y, [float]$c[0], [float]$c[1], [float]$c[2], [float]$c[3], [float]$c[4], [float]$c[5])
                $x = $c[4]; $y = $c[5]
            }
            'Z' { $p.CloseFigure(); $x = $sx; $y = $sy }
            default { throw "Unsupported path command '$cmd' in assets/leo.svg" }
        }
    }
    $p
}

# The visible logo: Layer_1's circle (radial gradient) + band path (linear gradient); hidden layers skipped.
$layer = $svg.SelectSingleNode("//s:g[@id='Layer_1']", $ns)
$circle = $layer.SelectSingleNode('.//s:circle', $ns)
$bandNode = $layer.SelectNodes('.//s:path', $ns) | Where-Object { $_.class -ne 'st2' } | Select-Object -First 1
$radial = $svg.SelectSingleNode("//s:radialGradient", $ns)
$linear = $svg.SelectSingleNode("//s:linearGradient", $ns)
$viewBox = @($svg.svg.viewBox -split '\s+' | ForEach-Object { F $_ })
$box = $viewBox[2]

function Render([int]$size) {
    $bmp = New-Object System.Drawing.Bitmap $size, $size, ([System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = 'AntiAlias'
    $g.PixelOffsetMode = 'HighQuality'
    $g.Clear([System.Drawing.Color]::Transparent)
    $g.ScaleTransform([float]($size / $box), [float]($size / $box))

    # Circle, filled with the radial gradient (centre/radius mapped through its gradientTransform).
    $cx = F $circle.cx; $cy = F $circle.cy; $r = F $circle.r
    $face = New-Object System.Drawing.Drawing2D.GraphicsPath
    $face.AddEllipse([float]($cx - $r), [float]($cy - $r), [float](2 * $r), [float](2 * $r))
    $rm = Get-Matrix $radial.gradientTransform
    $gc = Map $rm (F $radial.cx) (F $radial.cy)
    $edge = Map $rm ((F $radial.cx) + (F $radial.r)) (F $radial.cy)
    $gr = [Math]::Sqrt([Math]::Pow($edge.X - $gc.X, 2) + [Math]::Pow($edge.Y - $gc.Y, 2))
    $gradPath = New-Object System.Drawing.Drawing2D.GraphicsPath
    $gradPath.AddEllipse([float]($gc.X - $gr), [float]($gc.Y - $gr), [float](2 * $gr), [float](2 * $gr))
    $rs = Stops $radial
    $brush = New-Object System.Drawing.Drawing2D.PathGradientBrush $gradPath
    $brush.CenterPoint = $gc
    $brush.CenterColor = $rs[0]
    $brush.SurroundColors = @($rs[-1])
    $g.FillPath($brush, $face)

    # Band, filled with the linear gradient (end points mapped through its gradientTransform).
    $band = Get-PathFromSvg $bandNode.d
    $lm = Get-Matrix $linear.gradientTransform
    $p1 = Map $lm (F $linear.x1) (F $linear.y1)
    $p2 = Map $lm (F $linear.x2) (F $linear.y2)
    $ls = Stops $linear
    $lin = New-Object System.Drawing.Drawing2D.LinearGradientBrush $p1, $p2, $ls[0], $ls[-1]
    $lin.WrapMode = 'TileFlipXY'
    $g.FillPath($lin, $band)

    $g.Dispose()
    $bmp
}

# Classic 32-bit BGRA DIB icon entry (BITMAPINFOHEADER + bottom-up pixels + empty AND mask).
# Small sizes must be DIBs: many icon readers (incl. System.Drawing) mis-read PNG frames below 256 px.
function ToDib([System.Drawing.Bitmap]$bmp) {
    $sz = $bmp.Width
    $ms = New-Object System.IO.MemoryStream
    $bw = New-Object System.IO.BinaryWriter $ms
    $bw.Write([uint32]40); $bw.Write([int32]$sz); $bw.Write([int32]($sz * 2))
    $bw.Write([uint16]1); $bw.Write([uint16]32); $bw.Write([uint32]0)
    $bw.Write([uint32]($sz * $sz * 4)); $bw.Write([int32]0); $bw.Write([int32]0)
    $bw.Write([uint32]0); $bw.Write([uint32]0)
    for ($y = $sz - 1; $y -ge 0; $y--) {
        for ($x = 0; $x -lt $sz; $x++) {
            $c = $bmp.GetPixel($x, $y)
            $bw.Write([byte]$c.B); $bw.Write([byte]$c.G); $bw.Write([byte]$c.R); $bw.Write([byte]$c.A)
        }
    }
    $maskRow = [int]([Math]::Ceiling($sz / 32.0) * 4)
    $bw.Write((New-Object byte[] ($maskRow * $sz)))
    $bw.Flush()
    ,([byte[]]$ms.ToArray())
}

$sizes = 16, 20, 24, 32, 40, 48, 64, 96, 128, 256
$frames = @()
foreach ($sz in $sizes) {
    $bmp = Render $sz
    if ($sz -ge 256) {
        $ms = New-Object System.IO.MemoryStream
        $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
        $bytes = $ms.ToArray()
    }
    else {
        [byte[]]$bytes = ToDib $bmp
    }
    $frames += , @{ Size = $sz; Bytes = $bytes }
    if ($sz -in 32, 64, 256) { $bmp.Save((Join-Path $outDir "leo-$sz.png"), [System.Drawing.Imaging.ImageFormat]::Png) }
    $bmp.Dispose()
}

# Pack frames into a .ico (DIB entries < 256 px, PNG entry for 256 px).
$icoPath = Join-Path $outDir 'leo.ico'
$fs = [System.IO.File]::Create($icoPath)
$w = New-Object System.IO.BinaryWriter $fs
$w.Write([uint16]0); $w.Write([uint16]1); $w.Write([uint16]$frames.Count)
$offset = 6 + 16 * $frames.Count
foreach ($p in $frames) {
    $dim = if ($p.Size -ge 256) { 0 } else { $p.Size }
    $w.Write([byte]$dim); $w.Write([byte]$dim); $w.Write([byte]0); $w.Write([byte]0)
    $w.Write([uint16]1); $w.Write([uint16]32)
    $w.Write([uint32]$p.Bytes.Length); $w.Write([uint32]$offset)
    $offset += $p.Bytes.Length
}
foreach ($p in $frames) { $w.Write([byte[]]$p.Bytes) }
$w.Dispose()

Write-Host "Wrote $icoPath and PNGs to $outDir"
