param(
    [string]$Source = "docs/screenshots/_neon_home.png",
    [string]$MaskSource = "docs/screenshots/_mask_src.png",
    [string]$Out = "docs/neon-art/template-2132x1492.png",
    [string]$MaskOut = "docs/neon-art/mask-2132x1492.png"
)

Add-Type -AssemblyName System.Drawing

$root = Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Path)

function Resolve-UnderRoot([string]$p) {
    if ([System.IO.Path]::IsPathRooted($p)) { return $p }
    return (Join-Path $root $p)
}

$srcPath = Resolve-UnderRoot $Source
$maskSrcPath = Resolve-UnderRoot $MaskSource
$outPath = Resolve-UnderRoot $Out
$maskPath = Resolve-UnderRoot $MaskOut

if (-not (Test-Path $srcPath)) { Write-Output "SOURCE_MISSING=$srcPath"; exit 1 }
if (-not (Test-Path $maskSrcPath)) { Write-Output "MASKSORUCE_MISSING=$maskSrcPath"; exit 1 }
$dir = Split-Path -Parent $outPath
if (-not (Test-Path $dir)) { New-Item -ItemType Directory -Path $dir -Force | Out-Null }

# 窗口 1280x820：内容区从 (1+212, 1+46) 开始，尺寸 1066x746
$cropX = 213; $cropY = 47; $cropW = 1066; $cropH = 746
$scale = 2

$src = [System.Drawing.Bitmap]::FromFile($srcPath)
$maskSrc = [System.Drawing.Bitmap]::FromFile($maskSrcPath)
if ($src.Width -ne 1280 -or $src.Height -ne 820) {
    Write-Output ("UNEXPECTED_SIZE={0}x{1}（需 1280x820 的整窗截图）" -f $src.Width, $src.Height)
}

# ---------- 1. 采样识别"卡片覆盖区" ----------
# 卡片面是 #2A1042（42,16,66），95% 不透明地盖在插画上，颜色稳定；文字/描边属于少数点，用邻域投票抹平
$step = 4
$cols = [int][Math]::Floor($cropW / $step)
$rows = [int][Math]::Floor($cropH / $step)
$hit = New-Object 'bool[,]' $cols, $rows

for ($cy = 0; $cy -lt $rows; $cy++) {
    for ($cx = 0; $cx -lt $cols; $cx++) {
        $px = $cropX + $cx * $step
        $py = $cropY + $cy * $step
        $c = $maskSrc.GetPixel($px, $py)
        # 掩码截图里卡片面被临时染成纯洋红，只认它 —— 用正常配色做色差判定会把深紫天空误判成卡片
        if ($c.R -ge 240 -and $c.G -le 30 -and $c.B -ge 240) { $hit[$cx, $cy] = $true }
    }
}

# 3x3 闭运算：把文字造成的空洞补上，避免遮罩出现麻点
$closed = New-Object 'bool[,]' $cols, $rows
for ($cy = 0; $cy -lt $rows; $cy++) {
    for ($cx = 0; $cx -lt $cols; $cx++) {
        if (-not $hit[$cx, $cy]) { continue }
        $closed[$cx, $cy] = $true
    }
}
$filled = New-Object 'bool[,]' $cols, $rows
for ($cy = 0; $cy -lt $rows; $cy++) {
    for ($cx = 0; $cx -lt $cols; $cx++) {
        if (-not $closed[$cx, $cy]) { continue }
        for ($dy = -2; $dy -le 2; $dy++) {
            for ($dx = -2; $dx -le 2; $dx++) {
                $nx = $cx + $dx; $ny = $cy + $dy
                if ($nx -ge 0 -and $nx -lt $cols -and $ny -ge 0 -and $ny -lt $rows) { $filled[$nx, $ny] = $true }
            }
        }
    }
}

$covered = ($filled | Where-Object { $_ } | Measure-Object).Count
$total = $cols * $rows
$pct = [Math]::Round(100.0 * $covered / $total, 1)

# ---------- 2. 输出模板（原图 2 倍 + 遮挡半透明黑 + 安全边距） ----------
$w = $cropW * $scale; $h = $cropH * $scale
$bmp = New-Object System.Drawing.Bitmap($w, $h)
$g = [System.Drawing.Graphics]::FromImage($bmp)
$g.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
$g.Clear([System.Drawing.Color]::FromArgb(255, 20, 8, 34))
$g.DrawImage($src, (New-Object System.Drawing.Rectangle(0, 0, $w, $h)),
    (New-Object System.Drawing.Rectangle($cropX, $cropY, $cropW, $cropH)),
    [System.Drawing.GraphicsUnit]::Pixel)

$overlay = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(190, 8, 2, 14))
for ($cy = 0; $cy -lt $rows; $cy++) {
    for ($cx = 0; $cx -lt $cols; $cx++) {
        if (-not $filled[$cx, $cy]) { continue }
        $g.FillRectangle($overlay, $cx * $step * $scale, $cy * $step * $scale, $step * $scale, $step * $scale)
    }
}
$overlay.Dispose()

# 安全边距（34/26/34/24 → 2 倍）
$padPen = New-Object System.Drawing.Pen ([System.Drawing.Color]::FromArgb(220, 79, 240, 200)), 3
$padPen.DashStyle = [System.Drawing.Drawing2D.DashStyle]::Dash
$g.DrawRectangle($padPen, 34 * $scale, 26 * $scale, ($cropW - 68) * $scale, ($cropH - 50) * $scale)
$padPen.Dispose()

# 文案
$fontBig = New-Object System.Drawing.Font("Microsoft YaHei UI", 34, [System.Drawing.FontStyle]::Bold)
$fontMid = New-Object System.Drawing.Font("Microsoft YaHei UI", 24)
$fontSml = New-Object System.Drawing.Font("Microsoft YaHei UI", 20)
$white = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(235, 255, 233, 245))
$muted = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(210, 217, 184, 232))
$dark = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(200, 10, 4, 18))

$g.DrawString("暗色区域 = 会被卡片遮住（占 $pct%）", $fontBig, $white, 60, 46)
$g.DrawString("请在亮色区域安排主体：落日、棕榈、天际线", $fontMid, $muted, 60, 100)
$g.DrawString("画布 2132 × 1492（内容区 1066×746 的 2 倍）", $fontSml, $muted, 60, 140)
$g.DrawString("青色虚线 = 页面内外边距（34 / 26 / 34 / 24）", $fontSml, $muted, 60, 172)

$g.DrawString("可见区", $fontMid, $white, 60, 250)
$g.DrawString("可见区", $fontMid, $white, ($w - 200), ($h - 260))
$g.DrawString("遮挡区", $fontMid, $muted, ($w / 2 - 60), ($h / 2))

$bmp.Save($outPath, [System.Drawing.Imaging.ImageFormat]::Png)
$g.Dispose(); $bmp.Dispose()

# ---------- 3. 输出黑白遮罩（白=可见，黑=遮挡） ----------
$mask = New-Object System.Drawing.Bitmap($w, $h)
$mg = [System.Drawing.Graphics]::FromImage($mask)
$mg.Clear([System.Drawing.Color]::White)
$blackBrush = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::Black)
for ($cy = 0; $cy -lt $rows; $cy++) {
    for ($cx = 0; $cx -lt $cols; $cx++) {
        if (-not $filled[$cx, $cy]) { continue }
        $mg.FillRectangle($blackBrush, $cx * $step * $scale, $cy * $step * $scale, $step * $scale, $step * $scale)
    }
}
$blackBrush.Dispose(); $mg.Dispose()
$mask.Save($maskPath, [System.Drawing.Imaging.ImageFormat]::Png)
$mask.Dispose()

$src.Dispose()
$maskSrc.Dispose()

Write-Output ("COVERED=" + $pct + "%")
Write-Output ("TEMPLATE=" + $outPath)
Write-Output ("MASK=" + $maskPath)
