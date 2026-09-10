<#
.SYNOPSIS
    安装生成 Pixbian 所需的开发环境。

.DESCRIPTION
    补齐编译 WinUI 3 项目的全部前置依赖：
      1. .NET 10 SDK（通过 winget 安装）
      2. Visual Studio 2022 的「.NET 桌面开发」工作负载、Windows 10 SDK 19041 与 MSVC 生成工具

    注意：VS 组件安装需要管理员权限，脚本会弹出 UAC 窗口，请点击「是」。
    若安装过程中断，可重复执行本脚本，VS 安装引擎会自动续装。

.PARAMETER VisualStudioInstallPath
    Visual Studio 2022 的安装目录。企业版 / 专业版请替换为对应的 Edition 目录。

.PARAMETER SkipDotNetSdk
    跳过 .NET 10 SDK 安装（已通过其他方式安装时使用）。

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File .\scripts\Install-Toolchain.ps1
#>

[CmdletBinding()]
param(
    [string] $VisualStudioInstallPath = 'C:\Program Files\Microsoft Visual Studio\2022\Community',
    [switch] $SkipDotNetSdk
)

$ErrorActionPreference = 'Stop'

# VS 安装引擎。注意：不可使用同目录的 vs_installer.exe，它只是 UI 启动器，传入命令行参数不会执行安装。
$script:VsSetupExe = 'C:\Program Files (x86)\Microsoft Visual Studio\Installer\setup.exe'

# 必需组件：
#   ManagedDesktop          —— .NET 桌面开发工作负载（C# 项目系统、调试器）
#   Windows10SDK.19041      —— Windows 10 SDK，编译目标与 PRI 资源索引所需
#   VC.Tools.x86.x64        —— MSVC 生成工具，WinUI 3 的 XAML 编译器依赖它提供的 vcmeta.dll
#   Workload.Universal      —— 通用 Windows 平台开发，提供 MSIX/PRI 任务程序集（AppxPackage 目录）
#                              缺失时编译会报 MSB4062：无法加载 ExpandPriContent / RemovePayloadDuplicates
#   Component.XamlTools      —— XAML 设计器与 WinUI 3 的 XAML Live Preview（17.14+ 内置）核心组件
#   Component.WinAppSDK      —— Windows App SDK 扩展，WinUI 3 项目模板与预览器数据源依赖
$script:VsComponentIds = @(
    'Microsoft.VisualStudio.Workload.ManagedDesktop',
    'Microsoft.VisualStudio.Component.Windows10SDK.19041',
    'Microsoft.VisualStudio.Component.VC.Tools.x86.x64',
    'Microsoft.VisualStudio.Workload.Universal',
    'Microsoft.VisualStudio.Component.XamlTools',
    'Microsoft.VisualStudio.Component.WinAppSDK'
)

function Write-Step {
    param([string] $Message)

    Write-Host ''
    Write-Host "==> $Message" -ForegroundColor Cyan
}

function Test-DotNet10SdkInstalled {
    if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
        return $false
    }

    $installedSdks = & dotnet --list-sdks 2>$null
    return @($installedSdks | Where-Object { $_ -match '^10\.\d+\.\d+' }).Count -gt 0
}

function Install-DotNet10Sdk {
    Write-Step '安装 .NET 10 SDK'

    if (-not (Get-Command winget -ErrorAction SilentlyContinue)) {
        throw '未找到 winget。请从 Microsoft Store 安装「应用安装程序」，或手动访问 https://dotnet.microsoft.com/download 下载 .NET 10 SDK。'
    }

    winget install --id Microsoft.DotNet.SDK.10 --exact --accept-source-agreements --accept-package-agreements

    if ($LASTEXITCODE -ne 0) {
        throw ".NET 10 SDK 安装失败，winget 退出码：$LASTEXITCODE"
    }

    Write-Host '.NET 10 SDK 安装完成。' -ForegroundColor Green
}

function Install-VisualStudioComponents {
    param([string] $InstallPath)

    if (-not (Test-Path $script:VsSetupExe)) {
        Write-Warning "未找到 VS 安装引擎：$($script:VsSetupExe)"
        Write-Warning '请确认已安装 Visual Studio 2022 后重新执行本脚本。'
        return
    }

    if (-not (Test-Path $InstallPath)) {
        Write-Warning "未找到 Visual Studio 安装目录：$InstallPath"
        Write-Warning '请使用 -VisualStudioInstallPath 参数指定实际路径（Community / Professional / Enterprise）。'
        return
    }

    Write-Step '安装 Visual Studio 2022 组件（请在弹出的 UAC 窗口中点击「是」）'
    Write-Host "    安装路径：$InstallPath" -ForegroundColor DarkGray
    foreach ($componentId in $script:VsComponentIds) {
        Write-Host "    组件：$componentId" -ForegroundColor DarkGray
    }

    # 逐个组件显式拼接为 `--add <id>`，避免把数组拼成单个字符串导致后续组件被吞掉。
    $addArgs = ($script:VsComponentIds | ForEach-Object { "--add $_" }) -join ' '
    $arguments = "modify --installPath `"$InstallPath`" $addArgs --passive --norestart"

    # 非提权进程调用 setup.exe 会以 ExitCode 5007 静默失败，故必须显式申请管理员权限。
    $process = Start-Process `
        -FilePath $script:VsSetupExe `
        -ArgumentList $arguments `
        -Verb RunAs `
        -Wait `
        -PassThru

    if ($process.ExitCode -ne 0) {
        throw "Visual Studio 组件安装失败，ExitCode=$($process.ExitCode)。常见含义：5004=操作被取消，5007=需要提升权限，3010=需要重启计算机。"
    }

    Write-Host 'Visual Studio 组件安装完成。' -ForegroundColor Green
}

Write-Host 'Pixbian 开发环境安装' -ForegroundColor Cyan
Write-Host '==================================================' -ForegroundColor Cyan

if ($SkipDotNetSdk) {
    Write-Step '已指定 -SkipDotNetSdk，跳过 .NET 10 SDK 安装'
}
elseif (Test-DotNet10SdkInstalled) {
    Write-Step '.NET 10 SDK 已安装，跳过'
}
else {
    Install-DotNet10Sdk
}

Install-VisualStudioComponents -InstallPath $VisualStudioInstallPath

Write-Step '安装完成'
Write-Host '请重新打开终端（使 PATH 生效），然后执行以下命令验证：' -ForegroundColor Yellow
Write-Host '    dotnet --list-sdks'
Write-Host '    dotnet build -c Debug'
Write-Host ''
