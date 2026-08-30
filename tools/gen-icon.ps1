<#
.SYNOPSIS
    生成应用图标：把 Assets/app-icon.svg 渲染为多尺寸 PNG 并打包成 ICO。

.DESCRIPTION
    使用 Windows PowerShell 5.1 自带的 WPF 几何引擎离线渲染，零外部依赖；
    产物为 PNG-in-ICO 格式（Windows Vista 及以上原生支持）。
    图标更换后修改 app-icon.svg 并重新运行本脚本即可：
    powershell.exe -NoProfile -ExecutionPolicy Bypass -File tools\gen-icon.ps1
#>

$ErrorActionPreference = 'Stop'

$root = Split-Path -Parent $PSScriptRoot
$svgFile = Join-Path $root 'src\Pixbian\Assets\app-icon.svg'
$icoFile = Join-Path $root 'src\Pixbian\Assets\app.ico'

Add-Type -AssemblyName PresentationCore
Add-Type -AssemblyName WindowsBase

# 以 app-icon.svg 为唯一数据源，避免路径数据与填充色两处维护
[xml]$svg = Get-Content -LiteralPath $svgFile -Raw
$viewBox = [double]($svg.svg.viewBox -split '\s+')[2]
$nodes = @($svg.svg.path)
if ($nodes.Count -eq 0) {
    throw "SVG 中未找到 path 元素：$svgFile"
}

$sizes = @(256, 48, 32, 16)
$pngs = @{}

foreach ($size in $sizes) {
    $scale = $size / $viewBox

    $visual = New-Object System.Windows.Media.DrawingVisual
    $context = $visual.RenderOpen()
    foreach ($node in $nodes) {
        # Parse 返回的几何处于冻结状态，Clone 出可变副本后才能附加缩放变换
        $geometry = [System.Windows.Media.Geometry]::Parse($node.d).Clone()
        $geometry.Transform = New-Object System.Windows.Media.MatrixTransform($scale, 0, 0, $scale, 0, 0)
        $color = [System.Windows.Media.Color]([System.Windows.Media.ColorConverter]::ConvertFromString($node.fill))
        $null = $context.DrawGeometry((New-Object System.Windows.Media.SolidColorBrush($color)), $null, $geometry)
    }
    $context.Close()

    $bitmap = New-Object System.Windows.Media.Imaging.RenderTargetBitmap($size, $size, 96, 96, [System.Windows.Media.PixelFormats]::Pbgra32)
    $bitmap.Render($visual)

    $encoder = New-Object System.Windows.Media.Imaging.PngBitmapEncoder
    $null = $encoder.Frames.Add([System.Windows.Media.Imaging.BitmapFrame]::Create($bitmap))
    $stream = New-Object System.IO.MemoryStream
    $encoder.Save($stream)
    $pngs[$size] = $stream.ToArray()
    $stream.Dispose()
}

# ICO 结构：ICONDIR(6 字节) + 每尺寸目录项(16 字节) + 各 PNG 图像数据
$ico = New-Object System.IO.MemoryStream
$writer = New-Object System.IO.BinaryWriter($ico)

$writer.Write([uint16]0)            # 保留字段
$writer.Write([uint16]1)            # 类型：图标
$writer.Write([uint16]$sizes.Count) # 图像数量

$offset = 6 + 16 * $sizes.Count
foreach ($size in $sizes) {
    $data = $pngs[$size]
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
foreach ($size in $sizes) {
    $writer.Write($pngs[$size])
}
$writer.Flush()

[System.IO.File]::WriteAllBytes($icoFile, $ico.ToArray())
$writer.Dispose()
$ico.Dispose()

Write-Host ("已生成 {0}（{1}）" -f $icoFile, (($sizes | ForEach-Object { "$_" }) -join '/'))
