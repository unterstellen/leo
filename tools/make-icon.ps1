<#
.SYNOPSIS
  Renders the LEO mark (assets/leo.svg design) into src/LEO.App/Assets: leo.ico (16-256 px) + PNGs.

.DESCRIPTION
  Draws with GDI+ so no external tools are needed. Small sizes (<= 24 px) use a simplified,
  bolder variant (no arms/highlights, bigger lenses) so it stays legible in the tray.
  Re-run after changing the design:  powershell -File tools\make-icon.ps1
#>
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

$root = Split-Path $PSScriptRoot -Parent
$outDir = Join-Path $root 'src\LEO.App\Assets'
New-Item -ItemType Directory -Force $outDir | Out-Null

function C([string]$hex, [int]$a = 255) {
    $h = $hex.TrimStart('#')
    [System.Drawing.Color]::FromArgb($a, [Convert]::ToInt32($h.Substring(0, 2), 16),
        [Convert]::ToInt32($h.Substring(2, 2), 16), [Convert]::ToInt32($h.Substring(4, 2), 16))
}

function RoundRect([float]$x, [float]$y, [float]$w, [float]$h, [float]$r) {
    $p = New-Object System.Drawing.Drawing2D.GraphicsPath
    $d = 2 * $r
    $p.AddArc($x, $y, $d, $d, 180, 90)
    $p.AddArc($x + $w - $d, $y, $d, $d, 270, 90)
    $p.AddArc($x + $w - $d, $y + $h - $d, $d, $d, 0, 90)
    $p.AddArc($x, $y + $h - $d, $d, $d, 90, 90)
    $p.CloseFigure()
    $p
}

function Render([int]$size) {
    $bmp = New-Object System.Drawing.Bitmap $size, $size, ([System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = 'AntiAlias'
    $g.PixelOffsetMode = 'HighQuality'
    $g.Clear([System.Drawing.Color]::Transparent)
    $s = $size / 256.0
    $g.ScaleTransform($s, $s)
    $small = $size -le 24
    $frame = C '#1E2128'

    # Face
    $inset = if ($small) { 6 } else { 8 }
    $faceRect = New-Object System.Drawing.RectangleF $inset, $inset, (256 - 2 * $inset), (256 - 2 * $inset)
    $facePath = New-Object System.Drawing.Drawing2D.GraphicsPath
    $facePath.AddEllipse($faceRect)
    $grad = New-Object System.Drawing.Drawing2D.PathGradientBrush $facePath
    $grad.CenterPoint = New-Object System.Drawing.PointF 102, 82
    $grad.CenterColor = C '#FFE36E'
    $grad.SurroundColors = @(C '#F2A60C')
    $blend = New-Object System.Drawing.Drawing2D.ColorBlend 3
    $blend.Colors = @((C '#F2A60C'), (C '#FFC83D'), (C '#FFE36E'))
    $blend.Positions = @(0.0, 0.45, 1.0)
    $grad.InterpolationColors = $blend
    $g.FillPath($grad, $facePath)
    $edge = New-Object System.Drawing.Pen (C '#D08A00'), ($(if ($small) { 12 } else { 6 }))
    $g.DrawEllipse($edge, $faceRect)

    if ($small) {
        # Bold glasses: two big dark lenses + thick bridge.
        $lw = 22
        $pen = New-Object System.Drawing.Pen $frame, $lw
        $pen.StartCap = 'Round'; $pen.EndCap = 'Round'
        $g.DrawLine($pen, 112, 112, 144, 112)
        $lensBrush = New-Object System.Drawing.SolidBrush (C '#1E2128')
        foreach ($x in 34, 140) {
            $lens = RoundRect $x 80 82 70 28
            $g.FillPath($lensBrush, $lens)
        }
    }
    else {
        $pen = New-Object System.Drawing.Pen $frame, 12
        $pen.StartCap = 'Round'; $pen.EndCap = 'Round'
        # temples
        $g.DrawLine($pen, 30, 104, 54, 100)
        $g.DrawLine($pen, 226, 104, 202, 100)
        # bridge
        $bridge = New-Object System.Drawing.Drawing2D.GraphicsPath
        $bridge.AddBezier(112, 106, 122, 98, 134, 98, 144, 106)
        $g.DrawPath($pen, $bridge)
        # lenses
        $lensBrush = New-Object System.Drawing.Drawing2D.LinearGradientBrush (New-Object System.Drawing.PointF 0, 88), (New-Object System.Drawing.PointF 0, 144), (C '#3A3F4B'), (C '#14161B')
        $rim = New-Object System.Drawing.Pen $frame, 10
        foreach ($x in 50, 140) {
            $lens = RoundRect $x 88 66 56 22
            $g.FillPath($lensBrush, $lens)
            $g.DrawPath($rim, $lens)
        }
        # highlights
        $hi = New-Object System.Drawing.Pen (C '#FFFFFF' 140), 7
        $hi.StartCap = 'Round'; $hi.EndCap = 'Round'
        foreach ($dx in 0, 90) {
            $h = New-Object System.Drawing.Drawing2D.GraphicsPath
            $h.AddBezier((64 + $dx), 104, (67 + $dx), 99, (74 + $dx), 96, (82 + $dx), 96)
            $g.DrawPath($hi, $h)
        }
    }

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
$pngs = @()
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
    $pngs += , @{ Size = $sz; Bytes = $bytes }
    if ($sz -in 32, 64, 256) { $bmp.Save((Join-Path $outDir "leo-$sz.png"), [System.Drawing.Imaging.ImageFormat]::Png) }
    $bmp.Dispose()
}

# Pack frames into a .ico (DIB entries < 256 px, PNG entry for 256 px).
$icoPath = Join-Path $outDir 'leo.ico'
$fs = [System.IO.File]::Create($icoPath)
$w = New-Object System.IO.BinaryWriter $fs
$w.Write([uint16]0); $w.Write([uint16]1); $w.Write([uint16]$pngs.Count)
$offset = 6 + 16 * $pngs.Count
foreach ($p in $pngs) {
    $dim = if ($p.Size -ge 256) { 0 } else { $p.Size }
    $w.Write([byte]$dim); $w.Write([byte]$dim); $w.Write([byte]0); $w.Write([byte]0)
    $w.Write([uint16]1); $w.Write([uint16]32)
    $w.Write([uint32]$p.Bytes.Length); $w.Write([uint32]$offset)
    $offset += $p.Bytes.Length
}
foreach ($p in $pngs) { $w.Write([byte[]]$p.Bytes) }
$w.Dispose()

Write-Host "Wrote $icoPath and PNGs to $outDir"
