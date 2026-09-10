<#
.SYNOPSIS
    生成应用图标资源：以 Assets/app-icon.png 为唯一数据源，输出 ICO、包徽标及其全部限定符变体。

.DESCRIPTION
    用 Windows PowerShell 5.1 自带的 WPF 位图管线离线缩放，零外部依赖；
    产物为 PNG-in-ICO 格式（Windows Vista 及以上原生支持）。

    输出项：
    - app.ico：资源管理器、文件属性、标题栏等场景使用。
    - Square44x44Logo：任务栏/开始菜单小图标，输出 scale 与 targetsize 两套限定符。
      targetsize 同时生成 altform-unplated（深色主题）和 altform-lightunplated（浅色主题）。
    - Square150x150Logo：开始菜单中等图块，输出 scale 限定符。
    - StoreLogo：应用商店/程序列表，输出 scale 限定符。

    关键约束：源图必须自带透明通道——带底板会把底色复制进每一个产物；
    缩放统一走 HighQuality 插值，小尺寸的锯齿与噪点全部来自插值方式。

    图标更换后替换 app-icon.png 并重新运行本脚本即可：
    powershell.exe -NoProfile -ExecutionPolicy Bypass -File scripts\New-AppIcon.ps1
#>

$ErrorActionPreference = 'Stop'

$root = Split-Path -Parent $PSScriptRoot
$sourceFile = Join-Path $root 'src\Pixbian\Assets\app-icon.png'
$icoFile = Join-Path $root 'src\Pixbian\Assets\app.ico'
$assetDir = Join-Path $root 'src\Pixbian\Assets'

Add-Type -AssemblyName PresentationCore
Add-Type -AssemblyName WindowsBase

if (-not (Test-Path -LiteralPath $sourceFile)) {
    throw "未找到图标源图：$sourceFile"
}

# 以 app-icon.png 为唯一数据源：OnLoad 一次读入内存，之后各尺寸共用同一份位图
$source = [System.Windows.Media.Imaging.BitmapImage]::new()
$source.BeginInit()
$source.CacheOption = [System.Windows.Media.Imaging.BitmapCacheOption]::OnLoad
$source.UriSource = [uri]$sourceFile
$source.EndInit()
$source.Freeze()

# 渲染并缓存指定方形尺寸的 PNG 字节。使用 Pbgra32 保证透明通道。
$pngCache = @{}
function Get-PngBytes {
    param([int]$size)

    if ($pngCache.ContainsKey($size)) {
        return $pngCache[$size]
    }

    $scale = [Math]::Min($size / $source.PixelWidth, $size / $source.PixelHeight)
    $width = $source.PixelWidth * $scale
    $height = $source.PixelHeight * $scale
    $area = [System.Windows.Rect]::new(
        ($size - $width) / 2, ($size - $height) / 2, $width, $height)

    $visual = [System.Windows.Media.DrawingVisual]::new()
    [System.Windows.Media.RenderOptions]::SetBitmapScalingMode(
        $visual, [System.Windows.Media.BitmapScalingMode]::HighQuality)

    $context = $visual.RenderOpen()
    $context.DrawImage($source, $area)
    $context.Close()

    $bitmap = [System.Windows.Media.Imaging.RenderTargetBitmap]::new(
        $size, $size, 96, 96, [System.Windows.Media.PixelFormats]::Pbgra32)
    $bitmap.Render($visual)

    $encoder = [System.Windows.Media.Imaging.PngBitmapEncoder]::new()
    $null = $encoder.Frames.Add([System.Windows.Media.Imaging.BitmapFrame]::Create($bitmap))
    $stream = [System.IO.MemoryStream]::new()
    $encoder.Save($stream)
    $bytes = $stream.ToArray()
    $stream.Dispose()

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

# 包徽标去背板变体：任务栏与开始菜单在不带底板地展示图标时会查找这些文件。
# 关键约束：unplated（深色主题）与 lightunplated（浅色主题）必须同时存在——
# 官方规范要求「即使图标外观完全相同也要各自提供独立文件」，缺任一套系统就会
# 改画「图标板」以保证最小对比度，表现为任务栏图标带一块强调色方块。
$badgeSizes = @(256, 64, 48, 44, 40, 32, 24, 20, 16)
foreach ($form in @('altform-unplated', 'altform-lightunplated')) {
    foreach ($size in $badgeSizes) {
        $target = Join-Path $assetDir ('Square44x44Logo.targetsize-{0}_{1}.png' -f $size, $form)
        [System.IO.File]::WriteAllBytes($target, (Get-PngBytes -size $size))
    }
}

Write-Host ("已生成 Square44x44Logo targetsize 变体 {0} 个：{1} × unplated/lightunplated" -f ($badgeSizes.Count * 2), ($badgeSizes -join '/'))

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
