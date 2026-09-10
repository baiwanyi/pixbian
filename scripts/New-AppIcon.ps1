<#
.SYNOPSIS
    生成应用图标：把 Assets/app-icon.svg 渲染为多尺寸 PNG 并打包成 ICO。

.DESCRIPTION
    使用 Windows PowerShell 5.1 自带的 WPF 几何引擎离线渲染，零外部依赖；
    产物为 PNG-in-ICO 格式（Windows Vista 及以上原生支持）。
    支持三类图元：rect（含圆角与渐变填充）、image（内嵌 base64 位图）、path（纯色路径）。
    图标更换后修改 app-icon.svg 并重新运行本脚本即可：
    powershell.exe -NoProfile -ExecutionPolicy Bypass -File scripts\New-AppIcon.ps1
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

# defs 中的线性渐变：id -> LinearGradientBrush（坐标按 viewBox 归一化为相对坐标）
$script:gradients = @{}
foreach ($gradient in @($svg.svg.defs.linearGradient | Where-Object { $_ })) {
    $brush = [System.Windows.Media.LinearGradientBrush]::new()
    $brush.MappingMode = [System.Windows.Media.BrushMappingMode]::RelativeToBoundingBox
    $brush.StartPoint = [System.Windows.Point]::new(
        ([double]$gradient.x1) / $viewBox, ([double]$gradient.y1) / $viewBox)
    $brush.EndPoint = [System.Windows.Point]::new(
        ([double]$gradient.x2) / $viewBox, ([double]$gradient.y2) / $viewBox)

    foreach ($stop in @($gradient.stop | Where-Object { $_ })) {
        $color = [System.Windows.Media.Color](
            [System.Windows.Media.ColorConverter]::ConvertFromString($stop.'stop-color'))
        $null = $brush.GradientStops.Add(
            [System.Windows.Media.GradientStop]::new($color, [double]$stop.offset))
    }

    $script:gradients[$gradient.id] = $brush
}

# <style> 中的类选择器：类名 -> fill 值（形如 .cls-1 { fill: url(#linear-gradient); }）
# XML 适配器把无属性的纯文本元素投影成 String，此时取不到 InnerText，故按类型分别取值。
$script:classFills = @{}
$styleNode = $svg.svg.defs.style
$styleText = if ($styleNode -is [System.Xml.XmlNode]) { $styleNode.InnerText } else { [string]$styleNode }
if ($styleText) {
    foreach ($rule in [regex]::Matches($styleText, '\.([\w-]+)\s*\{([^}]*)\}')) {
        $fill = [regex]::Match($rule.Groups[2].Value, 'fill\s*:\s*([^;]+)').Groups[1].Value.Trim()
        if ($fill) {
            $script:classFills[$rule.Groups[1].Value] = $fill
        }
    }
}

function Get-Brush {
    param([string]$fill)

    if ([string]::IsNullOrWhiteSpace($fill)) {
        return $null
    }

    # 渐变引用：url(#linear-gradient)
    if ($fill -match '^url\(#(.+)\)$') {
        return $script:gradients[$Matches[1]]
    }

    $color = [System.Windows.Media.Color](
        [System.Windows.Media.ColorConverter]::ConvertFromString($fill))
    return [System.Windows.Media.SolidColorBrush]::new($color)
}

function Get-ElementFill {
    param($node)

    if ($node.fill) {
        return $node.fill
    }

    $class = $node.class
    if ($class -and $script:classFills.ContainsKey($class)) {
        return $script:classFills[$class]
    }

    return $null
}

function New-NodeDrawing {
    param($node)

    switch ($node.Name) {
        'rect' {
            $bounds = [System.Windows.Rect]::new(
                [double]$node.x, [double]$node.y, [double]$node.width, [double]$node.height)
            $radiusX = if ($node.rx) { [double]$node.rx } else { 0 }
            $radiusY = if ($node.ry) { [double]$node.ry } else { 0 }
            $geometry = [System.Windows.Media.RectangleGeometry]::new($bounds, $radiusX, $radiusY)

            return [System.Windows.Media.GeometryDrawing]::new(
                (Get-Brush (Get-ElementFill $node)), $null, $geometry)
        }
        'image' {
            $href = $node.GetAttribute('href', 'http://www.w3.org/1999/xlink')
            if (-not $href) {
                $href = $node.href
            }
            if (-not $href) {
                throw 'image 元素缺少 xlink:href。'
            }

            # 数据 URI 的 MIME 由导出工具决定（可能是非标准的 img/png），只取逗号之后的 base64 负载。
            $base64 = ($href.Substring($href.IndexOf(',') + 1)) -replace '\s', ''
            $stream = [System.IO.MemoryStream]::new([byte[]][Convert]::FromBase64String($base64))
            $bitmap = [System.Windows.Media.Imaging.BitmapImage]::new()
            $bitmap.BeginInit()
            $bitmap.CacheOption = [System.Windows.Media.Imaging.BitmapCacheOption]::OnLoad
            $bitmap.StreamSource = $stream
            $bitmap.EndInit()
            $bitmap.Freeze()
            $stream.Dispose()

            $rect = [System.Windows.Rect]::new(
                [double]$node.x, [double]$node.y, [double]$node.width, [double]$node.height)

            return [System.Windows.Media.ImageDrawing]::new($bitmap, $rect)
        }
        'path' {
            $geometry = [System.Windows.Media.Geometry]::Parse($node.d)

            return [System.Windows.Media.GeometryDrawing]::new(
                (Get-Brush (Get-ElementFill $node)), $null, $geometry)
        }
    }

    return $null
}

# 按文档顺序取可绘制图元：defs / style 等非绘制节点被排除，堆叠顺序即声明顺序。
$nodes = @($svg.svg.ChildNodes | Where-Object { $_.Name -in @('rect', 'image', 'path') })
if ($nodes.Count -eq 0) {
    throw "SVG 中未找到可绘制图元（rect / image / path）：$svgFile"
}

$sizes = @(256, 48, 32, 16)
$pngs = @{}

foreach ($size in $sizes) {
    $scale = $size / $viewBox

    # 每尺寸重建图元：Drawing 一次只能挂在一个 DrawingGroup 下，跨尺寸复用会被抢走。
    $group = [System.Windows.Media.DrawingGroup]::new()
    foreach ($node in $nodes) {
        $drawing = New-NodeDrawing $node
        if ($drawing) {
            $null = $group.Children.Add($drawing)
        }
    }
    $group.Transform = [System.Windows.Media.ScaleTransform]::new($scale, $scale)

    $visual = [System.Windows.Media.DrawingVisual]::new()
    $context = $visual.RenderOpen()
    $context.DrawDrawing($group)
    $context.Close()

    $bitmap = [System.Windows.Media.Imaging.RenderTargetBitmap]::new(
        $size, $size, 96, 96, [System.Windows.Media.PixelFormats]::Pbgra32)
    $bitmap.Render($visual)

    $encoder = [System.Windows.Media.Imaging.PngBitmapEncoder]::new()
    $null = $encoder.Frames.Add([System.Windows.Media.Imaging.BitmapFrame]::Create($bitmap))
    $stream = [System.IO.MemoryStream]::new()
    $encoder.Save($stream)
    $pngs[$size] = $stream.ToArray()
    $stream.Dispose()
}

# ICO 结构：ICONDIR(6 字节) + 每尺寸目录项(16 字节) + 各 PNG 图像数据
$ico = [System.IO.MemoryStream]::new()
$writer = [System.IO.BinaryWriter]::new($ico)

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
