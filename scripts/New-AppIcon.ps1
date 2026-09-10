<#
.SYNOPSIS
    生成应用图标资源：以 Assets/app-icon.png 为唯一数据源，输出 ICO、包徽标及其全部限定符变体。

.DESCRIPTION
    用 Windows PowerShell 5.1 自带的 GDI+（System.Drawing）位图管线离线缩放，零外部依赖；
    产物为 PNG-in-ICO 格式（Windows Vista 及以上原生支持）。

    缩放策略：逐级减半 + HighQualityBicubic，最后一级绘制到目标画布。
    关键约束：不能用 WPF 的 RenderTargetBitmap 直接大幅缩小——1000px 一次性缩到 32px 属于
    严重欠采样（插值只采到极少数源像素），边缘几乎不产生抗锯齿（实测半透明像素仅 ~0.8%，
    正常应 >10%），缩到任务栏小尺寸必然出现锯齿。逐级减半使每级缩放比接近 2 倍，等效面积平均。

    输出项：
    - app.ico：资源管理器、文件属性、标题栏等场景使用。
    - Square44x44Logo：任务栏/开始菜单小图标，输出 scale 与 targetsize 两套限定符。
      targetsize 覆盖 16~256 共 14 档，且三套主题并存（默认 / altform-unplated 深色 /
      altform-lightunplated 浅色）——缺任一套或任一档，系统就会画「图标板」。
    - Square150x150Logo：开始菜单中等图块，输出 scale 限定符。
    - StoreLogo：应用商店/程序列表，输出 scale 限定符。

    关键约束：源图必须自带透明通道——带底板会把底色复制进每一个产物。

    图标更换后替换 app-icon.png 并重新运行本脚本即可：
    powershell.exe -NoProfile -ExecutionPolicy Bypass -File scripts\New-AppIcon.ps1
#>

$ErrorActionPreference = 'Stop'

$root = Split-Path -Parent $PSScriptRoot
$sourceFile = Join-Path $root 'src\Pixbian\Assets\app-icon.png'
$icoFile = Join-Path $root 'src\Pixbian\Assets\app.ico'
$assetDir = Join-Path $root 'src\Pixbian\Assets'

Add-Type -AssemblyName System.Drawing

if (-not (Test-Path -LiteralPath $sourceFile)) {
    throw "未找到图标源图：$sourceFile"
}

# 源图一次读入内存；GDI+ 的 Bitmap(Stream) 要求流在 Bitmap 生命周期内保持打开，故不释放
$sourceStream = [System.IO.MemoryStream]::new([System.IO.File]::ReadAllBytes($sourceFile))
$source = [System.Drawing.Bitmap]::new($sourceStream)

# 用 GDI+ 高质量插值把位图缩放到指定宽高
function New-ScaledBitmap {
    param(
        [System.Drawing.Bitmap]$Bitmap,
        [int]$Width,
        [int]$Height
    )

    $dst = [System.Drawing.Bitmap]::new(
        $Width, $Height, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($dst)
    try {
        # SourceCopy：直接覆盖像素（含 alpha），避免与透明底混合
        $g.CompositingMode = [System.Drawing.Drawing2D.CompositingMode]::SourceCopy
        $g.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
        $g.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
        $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::HighQuality
        $g.DrawImage($Bitmap, [System.Drawing.Rectangle]::new(0, 0, $Width, $Height))
    }
    finally {
        $g.Dispose()
    }
    return $dst
}

# 渲染并缓存指定方形尺寸的 PNG 字节
$pngCache = @{}

function Get-PngBytes {
    param([int]$size)

    if ($pngCache.ContainsKey($size)) {
        return $pngCache[$size]
    }

    # 等比适配到方形画布：非正方形源图不拉伸变形，多出的方向居中留白
    $fitScale = [Math]::Min($size / $source.Width, $size / $source.Height)
    $fitW = [Math]::Max(1, [int][Math]::Round($source.Width * $fitScale))
    $fitH = [Math]::Max(1, [int][Math]::Round($source.Height * $fitScale))

    # 逐级减半，直到减半后会小于目标（即最后一级缩放比不超过 2 倍）
    $cur = $source
    $owned = $false
    while ($cur.Width -gt 1 -and [Math]::Floor($cur.Width / 2) -ge $fitW) {
        $nw = [Math]::Max(1, [int][Math]::Floor($cur.Width / 2))
        $nh = [Math]::Max(1, [int][Math]::Floor($cur.Height / 2))
        $next = New-ScaledBitmap -Bitmap $cur -Width $nw -Height $nh
        if ($owned) { $cur.Dispose() }
        $cur = $next
        $owned = $true
    }

    # 最后一级：缩放并居中绘制到 size×size 透明画布
    $canvas = [System.Drawing.Bitmap]::new(
        $size, $size, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($canvas)
    try {
        $g.CompositingMode = [System.Drawing.Drawing2D.CompositingMode]::SourceCopy
        $g.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
        $g.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
        $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::HighQuality
        $x = [int](($size - $fitW) / 2)
        $y = [int](($size - $fitH) / 2)
        $g.DrawImage($cur, [System.Drawing.Rectangle]::new($x, $y, $fitW, $fitH))
    }
    finally {
        $g.Dispose()
        if ($owned) { $cur.Dispose() }
    }

    $stream = [System.IO.MemoryStream]::new()
    try {
        $canvas.Save($stream, [System.Drawing.Imaging.ImageFormat]::Png)
        $bytes = $stream.ToArray()
    }
    finally {
        $stream.Dispose()
        $canvas.Dispose()
    }

    $pngCache[$size] = $bytes
    return $bytes
}

# ICO 内部帧：资源管理器、文件属性、标题栏等场景由 Windows 直接取用
$icoSizes = @(256, 48, 32, 16)

# ICO 结构：ICONDIR(6 字节) + 每尺寸目录项(16 字节) + 各 PNG 图像数据
$ico = [System.IO.MemoryStream]::new()
$writer = [System.IO.BinaryWriter]::new($ico)

$writer.Write([uint16]0)                # 保留字段
$writer.Write([uint16]1)                # 类型：图标
$writer.Write([uint16]$icoSizes.Count)  # 图像数量

$offset = 6 + 16 * $icoSizes.Count
foreach ($size in $icoSizes) {
    # 注意：Get-PngBytes 返回的 byte[] 经 PowerShell 函数返回会被拆成 object[]，
    # BinaryWriter.Write(byte[]) 重载解析会失败（不写任何字节），故此处显式强转 [byte[]]。
    [byte[]]$data = Get-PngBytes -size $size
    $dimension = [byte]($size % 256)  # ICO 规范：256 尺寸记为 0
    $writer.Write($dimension)         # 宽度
    $writer.Write($dimension)         # 高度
    $writer.Write([byte]0)            # 调色板色数
    $writer.Write([byte]0)            # 保留字段
    $writer.Write([uint16]1)          # 颜色平面数
    $writer.Write([uint16]32)         # 位深
    $writer.Write([uint32]$data.Length)
    $writer.Write([uint32]$offset)
    $offset += $data.Length
}
foreach ($size in $icoSizes) {
    $writer.Write([byte[]](Get-PngBytes -size $size))
}
$writer.Flush()

[System.IO.File]::WriteAllBytes($icoFile, $ico.ToArray())
$writer.Dispose()
$ico.Dispose()

Write-Host ("已生成 {0}（{1}）" -f $icoFile, (($icoSizes | ForEach-Object { "$_" }) -join '/'))

# App List 图标（任务栏、开始菜单、上下文菜单等「无磁贴内边距」场景）按 targetsize
# 精确像素取图。官方规范硬性要求：① 尺寸覆盖 16~256 共 14 档；② 三套主题变体并存——
# 默认（无后缀）、_altform-unplated（深色）、_altform-lightunplated（浅色）
# ——「即使图标外观完全相同，也必须分别提供三套主题变体文件」。
# 缺任一套或任一档，系统就会在任务栏与开始菜单画上「图标板」（一块随系统强调色变化的
# 方块）并缩小图标，即最容易被误判成「ico 带底色」的那个现象。
$targetSizes = @(16, 20, 24, 30, 32, 36, 40, 48, 60, 64, 72, 80, 96, 256)
$targetForms = @(
    @{ Suffix = '';                       Label = '默认' },
    @{ Suffix = '_altform-unplated';      Label = '深色' },
    @{ Suffix = '_altform-lightunplated'; Label = '浅色' }
)
foreach ($form in $targetForms) {
    foreach ($size in $targetSizes) {
        $target = Join-Path $assetDir ('Square44x44Logo.targetsize-{0}{1}.png' -f $size, $form.Suffix)
        [System.IO.File]::WriteAllBytes($target, (Get-PngBytes -size $size))
    }
}

Write-Host ("已生成 Square44x44Logo targetsize 变体 {0} 个：{1} 档 × 默认/unplated/lightunplated" -f ($targetSizes.Count * 3), ($targetSizes -join '/'))

# scale 限定符：Windows 按当前显示缩放自动选取最接近的放大版本，
# 基础文件名（无 scale 限定符）等价于 scale-100。
# 每项的 Sizes 顺序与 scale-100/125/150/200/400 一一对应。
$scaleAssets = @(
    @{ Name = 'Square44x44Logo';   Sizes = @(44, 55, 66, 88, 176) },
    @{ Name = 'Square150x150Logo'; Sizes = @(150, 188, 225, 300, 600) },
    @{ Name = 'StoreLogo';         Sizes = @(50, 63, 75, 100, 200) }
)
$scaleNames = @('', '.scale-125', '.scale-150', '.scale-200', '.scale-400')

foreach ($asset in $scaleAssets) {
    for ($i = 0; $i -lt $asset.Sizes.Count; $i++) {
        $size = $asset.Sizes[$i]
        $suffix = $scaleNames[$i]
        $target = Join-Path $assetDir ($asset.Name + $suffix + '.png')
        [System.IO.File]::WriteAllBytes($target, (Get-PngBytes -size $size))
    }
    Write-Host ("已生成 {0} scale 变体：{1}" -f $asset.Name, (($asset.Sizes | ForEach-Object { "$_" }) -join '/'))
}

Write-Host '全部图标资源生成完毕。'
