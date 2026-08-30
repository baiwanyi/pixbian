<#
.SYNOPSIS
    一键构建并启动 Pixbian，直接显示应用界面。
.DESCRIPTION
    自动定位 dotnet SDK（PATH 优先，回退默认安装路径），先构建再独立启动应用进程。
    用法：.\run.ps1（Debug）或 .\run.ps1 -Configuration Release
    注意：输出路径按当前 TFM（net8.0-windows10.0.26100.0）与 x64 平台硬编码，
    若修改 csproj 的 TargetFramework 或平台需同步更新。
#>
param([string]$Configuration = 'Debug')

$ErrorActionPreference = 'Stop'

$command = Get-Command dotnet -ErrorAction SilentlyContinue
$dotnetPath = if ($command) { $command.Source } else { Join-Path $env:ProgramFiles 'dotnet\dotnet.exe' }
if (-not (Test-Path $dotnetPath)) {
    throw '未找到 dotnet SDK，请安装 .NET 8 SDK 或将其加入 PATH。'
}

$csproj = Join-Path $PSScriptRoot 'src\Pixbian\Pixbian.csproj'
& $dotnetPath build $csproj -c $Configuration --nologo
if ($LASTEXITCODE -ne 0) {
    throw "构建失败（退出码 $LASTEXITCODE），请检查上方输出。"
}

# Start-Process 启动独立进程后脚本即返回，应用窗口不受脚本宿主影响
$exe = Join-Path $PSScriptRoot "src\Pixbian\bin\x64\$Configuration\net8.0-windows10.0.26100.0\Pixbian.exe"
if (-not (Test-Path $exe)) {
    throw "未找到应用产物：$exe"
}
Start-Process $exe -WorkingDirectory (Split-Path $exe)
