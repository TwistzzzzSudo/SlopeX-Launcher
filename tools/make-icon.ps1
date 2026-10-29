param(
    [string]$Out = "I:\SlopeX Launcher\ico\SlopeX.ico",
    [string]$PreviewPng = ""
)

# Generates the SlopeX mark as a multi-resolution .ico from vector geometry.
# Geometry is the same one used in the title bar:
#   20x20 box, corner radius 5 (25%), polyline (4,13.5) (8,9) (10.5,11) (15.5,5),
#   stroke 1.6 (8% of 20), round caps and joins, near-black plate + white line.
# Keeping it parametric means the ico matches the in-app mark exactly.

Add-Type -AssemblyName System.Drawing

$sizes = @(16, 20, 24, 32, 40, 48, 64, 128, 256)
$plate = [System.Drawing.Color]::FromArgb(26, 26, 24)   # #1A1A18
$line = [System.Drawing.Color]::White
$ss = 4                                                  # supersampling factor

function New-MarkBitmap([int]$size) {
    $big = New-Object System.Drawing.Bitmap (($size * $ss)), ($size * $ss), ([System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($big)
    $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $g.Clear([System.Drawing.Color]::Transparent)

    $s = $big.Width
    $r = $s * 0.25
    $d = $r * 2

    $path = New-Object System.Drawing.Drawing2D.GraphicsPath
    $path.AddArc(0, 0, $d, $d, 180, 90)
    $path.AddArc($s - $d, 0, $d, $d, 270, 90)
    $path.AddArc($s - $d, $s - $d, $d, $d, 0, 90)
    $path.AddArc(0, $s - $d, $d, $d, 90, 90)
    $path.CloseFigure()

    $brush = New-Object System.Drawing.SolidBrush $plate
    $g.FillPath($brush, $path)
    $brush.Dispose()
    $path.Dispose()

    $pts = @(
        (New-Object System.Drawing.PointF (($s * 0.20)), ($s * 0.675)),
        (New-Object System.Drawing.PointF (($s * 0.40)), ($s * 0.450)),
        (New-Object System.Drawing.PointF (($s * 0.525), ($s * 0.550))),
        (New-Object System.Drawing.PointF (($s * 0.775), ($s * 0.250)))
    )

    # 8% of the box, but never thinner than a visible hairline after downscale
    $w = [Math]::Max($s * 0.08, 5.0)
    $pen = New-Object System.Drawing.Pen $line, $w
    $pen.StartCap = [System.Drawing.Drawing2D.LineCap]::Round
    $pen.EndCap = [System.Drawing.Drawing2D.LineCap]::Round
    $pen.LineJoin = [System.Drawing.Drawing2D.LineJoin]::Round
    $g.DrawLines($pen, $pts)
    $pen.Dispose()
    $g.Dispose()

    $out = New-Object System.Drawing.Bitmap $size, $size, ([System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g2 = [System.Drawing.Graphics]::FromImage($out)
    $g2.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
    $g2.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
    $g2.Clear([System.Drawing.Color]::Transparent)
    $g2.DrawImage($big, 0, 0, $size, $size)
    $g2.Dispose()
    $big.Dispose()

    return $out
}

# --- render every size as PNG (Windows Vista+ accepts PNG frames in ICO) ---
$frames = @()
foreach ($size in $sizes) {
    $bmp = New-MarkBitmap $size
    $ms = New-Object System.IO.MemoryStream
    $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
    $frames += , @($size, $ms.ToArray())
    if ($size -eq 256 -and $PreviewPng -ne "") {
        $bmp.Save($PreviewPng, [System.Drawing.Imaging.ImageFormat]::Png)
        Write-Output ("PREVIEW=" + $PreviewPng)
    }
    $bmp.Dispose()
    $ms.Dispose()
}

# --- pack ICO ---
$dir = [System.IO.Path]::GetDirectoryName($Out)
if ($dir -and -not (Test-Path $dir)) { New-Item -ItemType Directory -Path $dir -Force | Out-Null }

$fs = [System.IO.File]::Create($Out)
$bw = New-Object System.IO.BinaryWriter $fs
$bw.Write([UInt16]0)                    # reserved
$bw.Write([UInt16]1)                    # type: icon
$bw.Write([UInt16]$frames.Count)        # image count

$offset = 6 + 16 * $frames.Count
foreach ($f in $frames) {
    $size = [int]$f[0]
    $data = [byte[]]$f[1]
    $dim = if ($size -ge 256) { 0 } else { $size }   # 0 means 256
    $bw.Write([Byte]$dim)               # width
    $bw.Write([Byte]$dim)               # height
    $bw.Write([Byte]0)                  # palette count
    $bw.Write([Byte]0)                  # reserved
    $bw.Write([UInt16]1)                # color planes
    $bw.Write([UInt16]32)               # bits per pixel
    $bw.Write([UInt32]$data.Length)
    $bw.Write([UInt32]$offset)
    $offset += $data.Length
}
foreach ($f in $frames) { $bw.Write([byte[]]$f[1]) }
$bw.Flush()
$bw.Close()
$fs.Close()

$info = Get-Item $Out
Write-Output ("ICO=" + $info.FullName)
Write-Output ("SIZES=" + ($sizes -join ","))
Write-Output ("BYTES=" + $info.Length)
