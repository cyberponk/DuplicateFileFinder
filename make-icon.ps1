<#
.SYNOPSIS
    Draws the Duplicate File Finder icon (two overlapping documents with an "=" sign)
    and saves it as DuplicateFileFinder.ico next to this script. build.cmd embeds it.
#>
Add-Type -AssemblyName System.Drawing

$out = Join-Path $PSScriptRoot 'DuplicateFileFinder.ico'
$sizes = 16, 20, 24, 32, 40, 48, 64, 256

function New-Page([float]$x, [float]$y, [float]$w, [float]$h, [float]$fold) {
    $p = New-Object Drawing.Drawing2D.GraphicsPath
    $p.AddPolygon([Drawing.PointF[]]@(
        (New-Object Drawing.PointF $x, $y), (New-Object Drawing.PointF ($x + $w - $fold), $y),
        (New-Object Drawing.PointF ($x + $w), ($y + $fold)), (New-Object Drawing.PointF ($x + $w), ($y + $h)),
        (New-Object Drawing.PointF $x, ($y + $h))))
    $p
}

function Draw-Icon([int]$s) {
    $bmp = New-Object Drawing.Bitmap $s, $s, ([Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = 'AntiAlias'
    $g.Clear([Drawing.Color]::Transparent)
    $u = $s / 32.0                                  # drawn on a 32x32 grid, scaled
    $line = [Math]::Max(1.0, 1.6 * $u)
    $blue = [Drawing.Color]::FromArgb(30, 90, 168)

    # Back document (light blue), front document (white), both with a folded corner
    $back = New-Page (3 * $u) (2 * $u) (17 * $u) (21 * $u) (5 * $u)
    $g.FillPath((New-Object Drawing.SolidBrush ([Drawing.Color]::FromArgb(158, 197, 240))), $back)
    $g.DrawPath((New-Object Drawing.Pen $blue, $line), $back)
    $front = New-Page (11 * $u) (8 * $u) (18 * $u) (22 * $u) (6 * $u)
    $g.FillPath([Drawing.Brushes]::White, $front)
    $g.DrawPath((New-Object Drawing.Pen $blue, $line), $front)
    $fold = New-Object Drawing.Drawing2D.GraphicsPath
    $fold.AddPolygon([Drawing.PointF[]]@(
        (New-Object Drawing.PointF (23 * $u), (8 * $u)), (New-Object Drawing.PointF (23 * $u), (14 * $u)),
        (New-Object Drawing.PointF (29 * $u), (14 * $u))))
    $g.FillPath((New-Object Drawing.SolidBrush ([Drawing.Color]::FromArgb(205, 225, 248))), $fold)
    $g.DrawPath((New-Object Drawing.Pen $blue, $line), $fold)

    # "=" sign in orange: the two files are identical
    $orange = New-Object Drawing.SolidBrush ([Drawing.Color]::FromArgb(242, 140, 40))
    $barH = [Math]::Max(2.0, 3.0 * $u)
    $g.FillRectangle($orange, [float](14 * $u), [float](16.5 * $u), [float](12 * $u), [float]$barH)
    $g.FillRectangle($orange, [float](14 * $u), [float](16.5 * $u + $barH + [Math]::Max(1.5, 2.5 * $u)), [float](12 * $u), [float]$barH)
    $g.Dispose()
    $bmp
}

# 32-bit DIB image as stored in an .ico (bottom-up rows, then an all-zero AND mask)
function Get-DibBytes($bmp) {
    $s = $bmp.Width
    $ms = New-Object IO.MemoryStream
    $w = New-Object IO.BinaryWriter $ms
    $w.Write([int]40); $w.Write([int]$s); $w.Write([int]($s * 2)); $w.Write([int16]1); $w.Write([int16]32)
    $w.Write([int]0); $w.Write([int]0); $w.Write([int]0); $w.Write([int]0); $w.Write([int]0); $w.Write([int]0)
    $data = $bmp.LockBits((New-Object Drawing.Rectangle 0, 0, $s, $s), 'ReadOnly', ([Drawing.Imaging.PixelFormat]::Format32bppArgb))
    $row = New-Object byte[] ($s * 4)
    for ($y = $s - 1; $y -ge 0; $y--) {
        [Runtime.InteropServices.Marshal]::Copy([IntPtr]($data.Scan0.ToInt64() + $y * $data.Stride), $row, 0, $row.Length)
        $w.Write($row)
    }
    $bmp.UnlockBits($data)
    $w.Write((New-Object byte[] ([Math]::Ceiling($s / 32.0) * 4 * $s)))
    $w.Flush()
    # The comma keeps PowerShell from unrolling the array into single bytes
    , $ms.ToArray()
}

# 256x256 is stored as PNG (as Windows does); the smaller sizes as DIBs
$images = New-Object 'System.Collections.Generic.List[byte[]]'
foreach ($s in $sizes) {
    $bmp = Draw-Icon $s
    if ($s -ge 256) {
        $ms = New-Object IO.MemoryStream; $bmp.Save($ms, [Drawing.Imaging.ImageFormat]::Png); $images.Add($ms.ToArray())
    } else {
        $images.Add([byte[]](Get-DibBytes $bmp))
    }
    $bmp.Dispose()
}

$ms = New-Object IO.MemoryStream
$w = New-Object IO.BinaryWriter $ms
$w.Write([int16]0); $w.Write([int16]1); $w.Write([int16]$sizes.Count)
$offset = 6 + 16 * $sizes.Count
for ($i = 0; $i -lt $sizes.Count; $i++) {
    $s = $sizes[$i]
    $w.Write([byte]($s % 256)); $w.Write([byte]($s % 256)); $w.Write([byte]0); $w.Write([byte]0)
    $w.Write([int16]1); $w.Write([int16]32); $w.Write([int]$images[$i].Length); $w.Write([int]$offset)
    $offset += $images[$i].Length
}
foreach ($img in $images) { $w.Write([byte[]]$img) }
$w.Flush()
[IO.File]::WriteAllBytes($out, $ms.ToArray())
"Saved $out ($($sizes -join ', ') px)"
