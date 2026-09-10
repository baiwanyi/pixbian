<#
.SYNOPSIS
    一键构建并启动 Pixbian，直接显示应用界面。
.DESCRIPTION
    自动定位 dotnet SDK（PATH 优先，回退默认安装路径），先构建再独立启动应用进程；
    启动前请求 Shell 重建图标缓存，使任务栏与开始菜单显示最新图标。
    用法：.\scripts\Build-Pixbian.ps1（Debug）或 .\scripts\Build-Pixbian.ps1 -Configuration Release
    注意：产物路径中的 TFM 由 MSBuild 从 csproj 动态读取（TargetFramework 升级后本脚本免改）；
    x64 平台与 RID 仍为固定约定（csproj 为复制 FFmpeg 原生库设置了 RuntimeIdentifier=win-x64，
    故产物落在 win-x64 子目录），若修改平台或 RuntimeIdentifier 需同步更新。
    脚本位于 scripts\，仓库根由 $PSScriptRoot 的父目录推出，故可从任意目录调用。
#>
param([string]$Configuration = 'Debug')

$ErrorActionPreference = 'Stop'

$command = Get-Command dotnet -ErrorAction SilentlyContinue
$dotnetPath = if ($command) { $command.Source } else { Join-Path $env:ProgramFiles 'dotnet\dotnet.exe' }
if (-not (Test-Path $dotnetPath)) {
    throw '未找到 dotnet SDK，请安装 .NET 10 SDK 或将其加入 PATH。'
}

$root = Split-Path -Parent $PSScriptRoot
$csproj = Join-Path $root 'src\Pixbian\Pixbian.csproj'
& $dotnetPath build $csproj -c $Configuration --nologo
if ($LASTEXITCODE -ne 0) {
    throw "构建失败（退出码 $LASTEXITCODE），请检查上方输出。"
}

# TFM 从项目文件动态读取，避免脚本随 TFM 升级而失效（曾硬编码 net8.0 于 net10 迁移后报错）。
$tfm = (& $dotnetPath msbuild $csproj -getProperty:TargetFramework -nologo | Out-String).Trim()
if ([string]::IsNullOrWhiteSpace($tfm)) {
    throw '未能从 csproj 读取 TargetFramework，请确认 dotnet msbuild -getProperty 可用（需 .NET 8+ SDK）。'
}

# 图标资源（Assets\app.ico 与包徽标 PNG）改动后，Windows 仍按缓存的旧图渲染任务栏与
# 开始菜单；启动前请求 Shell 重建图标缓存，ie4uinit -show 只重建缓存、不重启资源管理器。
$ie4uinit = Join-Path $env:SystemRoot 'System32\ie4uinit.exe'
if (Test-Path $ie4uinit) {
    Write-Host '刷新图标缓存 ...'
    & $ie4uinit -show
}

# Start-Process 启动独立进程后脚本即返回，应用窗口不受脚本宿主影响
$exe = Join-Path $root "src\Pixbian\bin\x64\$Configuration\$tfm\win-x64\Pixbian.exe"
if (-not (Test-Path $exe)) {
    throw "未找到应用产物：$exe"
}
Start-Process $exe -WorkingDirectory (Split-Path $exe)
