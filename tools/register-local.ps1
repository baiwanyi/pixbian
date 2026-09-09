<#
.SYNOPSIS
    在本机注册 Pixbian 稀疏包（外部位置包），使其可被设为默认照片查看器与视频播放器。

.DESCRIPTION
    依次完成：发布自包含产物 → 生成两级自签证书（根 CA + 代码签名叶子）→ 导入根证书 →
    MakeAppx 打包 → SignTool 签名 → 反注册同名旧包 → Add-AppxPackage 注册
    （ExternalLocation 指向发布目录）。
    注册后请到「设置 → 应用 → 默认应用」按文件类型把 Pixbian 设为默认；
    这一步必须由用户在系统里完成，Windows 不允许应用自行改写默认应用。
    应用本体始终留在 ExternalLocation 目录，系统不会把它搬进 AppData；
    改代码后重新发布到同一目录即生效，无需重新注册。

.PARAMETER Configuration
    发布配置，默认 Release。

.PARAMETER ExternalLocation
    外部位置目录（应用本体所在目录），默认为仓库下的 artifacts\win-x64。

.PARAMETER SkipPublish
    跳过发布步骤，直接对已有目录打包注册。

.PARAMETER PfxPassword
    PFX 密码；省略时由脚本交互式询问。用于无人值守场景（证书已存在时必须与生成时一致）。

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File tools\register-local.ps1

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File tools\register-local.ps1 -SkipPublish

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File tools\register-local.ps1 -PfxPassword (ConvertTo-SecureString 'xxx' -AsPlainText -Force)
#>
param(
    [string]$Configuration = 'Release',
    [string]$ExternalLocation = '',
    [securestring]$PfxPassword = $null,
    [switch]$SkipPublish
)

$ErrorActionPreference = 'Stop'

# —— 路径与常量 ——
$root = Split-Path -Parent $PSScriptRoot
$publishDir = if ($ExternalLocation) { $ExternalLocation } else { Join-Path $root 'artifacts\win-x64' }
$identityDir = Join-Path $root 'Packaging\identity'
$outDir = Join-Path $root 'Packaging\_out'
$msix = Join-Path $outDir 'Pixbian.msix'
$pfx = Join-Path $outDir 'Pixbian.pfx'
$rootCer = Join-Path $outDir 'PixbianRoot.cer'
$leafThumbFile = Join-Path $outDir 'leaf-thumbprint.txt'

# 叶子的 Subject 必须与 Packaging/identity/AppxManifest.xml 的 Identity.Publisher、
# 以及 src/Pixbian/app.manifest 中 msix 元素的 publisher 逐字符一致；
# 根证书是独立的信任锚，Subject 可以不同。
$subject = 'CN=Pixbian, O=Baiwanyi, C=CN'
$rootSubject = 'CN=Pixbian Root, O=Baiwanyi, C=CN'
$packageName = 'Pixbian'

function Resolve-SdkTool {
    param([string]$ToolName)

    $roots = @(
        (Join-Path ${env:ProgramFiles(x86)} 'Windows Kits\10\bin'),
        (Join-Path $env:ProgramFiles 'Windows Kits\10\bin')
    )

    foreach ($base in $roots) {
        if (-not (Test-Path $base)) {
            continue
        }

        $found = Get-ChildItem -Path $base -Filter $ToolName -Recurse -ErrorAction SilentlyContinue |
            Where-Object { $_.FullName -match '\\x64\\' } |
            Sort-Object -Property FullName -Descending |
            Select-Object -First 1

        if ($found) {
            return $found.FullName
        }
    }

    throw "未找到 $ToolName，请安装 Windows SDK（或 Visual Studio 的「通用 Windows 平台开发」工作负载）。"
}

function Convert-SecureStringToPlain {
    param([securestring]$Value)

    $bstr = [Runtime.InteropServices.Marshal]::SecureStringToBSTR($Value)

    try {
        return [Runtime.InteropServices.Marshal]::PtrToStringBSTR($bstr)
    }
    finally {
        [Runtime.InteropServices.Marshal]::ZeroFreeBSTR($bstr)
    }
}

# ① 发布：稀疏包只登记外部位置，应用本体必须由本步骤产出到该目录
if (-not $SkipPublish) {
    $dotnet = (Get-Command dotnet -ErrorAction SilentlyContinue).Source
    if (-not $dotnet) {
        $dotnet = Join-Path $env:ProgramFiles 'dotnet\dotnet.exe'
    }
    if (-not (Test-Path $dotnet)) {
        throw '未找到 dotnet SDK，请安装 .NET 8 SDK 或将其加入 PATH。'
    }

    Write-Host "发布 $Configuration 到 $publishDir ..."
    & $dotnet publish (Join-Path $root 'src\Pixbian\Pixbian.csproj') -c $Configuration -p:Platform=x64 -o $publishDir --nologo
    if ($LASTEXITCODE -ne 0) {
        throw "发布失败（退出码 $LASTEXITCODE）。"
    }
}

$exe = Join-Path $publishDir 'Pixbian.exe'
if (-not (Test-Path $exe)) {
    throw "外部位置目录中未找到 Pixbian.exe：$publishDir"
}

# ② 证书：两级结构（根 CA 签发代码签名叶子）。
#    单张自签证书同时充当根与叶子时，Windows 的链构建不稳定，
#    实测在 signtool 侧报 0x80096019、Add-AppxPackage 侧报 0x800B0109/0x800B010A；
#    拆成两级后链可以正常终止于受信任根。
#    注意：切勿 Add-Type System.Security——它会干扰 CertificateRequest 的类型解析，
#    使构造函数的密钥参数被解析为 null。
$null = New-Item -ItemType Directory -Force -Path $outDir

if ($PfxPassword) {
    $password = $PfxPassword
}
elseif (Test-Path $pfx) {
    Write-Host "复用已有证书：$pfx"
    $password = Read-Host '请输入该 PFX 的密码' -AsSecureString
}
else {
    $password = Read-Host '请设置 PFX 保护密码' -AsSecureString
}

if (-not (Test-Path $pfx)) {
    Write-Host "生成两级自签证书（叶子 Subject：$subject）"

    $rootRsa = New-Object -TypeName System.Security.Cryptography.RSACryptoServiceProvider -ArgumentList 2048
    $rootReq = [System.Security.Cryptography.X509Certificates.CertificateRequest]::new(
        $rootSubject, $rootRsa,
        [System.Security.Cryptography.HashAlgorithmName]::SHA256,
        [System.Security.Cryptography.RSASignaturePadding]::Pkcs1)
    [void]$rootReq.CertificateExtensions.Add(
        [System.Security.Cryptography.X509Certificates.X509BasicConstraintsExtension]::new($true, $false, 0, $true))
    [void]$rootReq.CertificateExtensions.Add(
        [System.Security.Cryptography.X509Certificates.X509KeyUsageExtension]::new(
            ([System.Security.Cryptography.X509Certificates.X509KeyUsageFlags]::KeyCertSign -bor
             [System.Security.Cryptography.X509Certificates.X509KeyUsageFlags]::CRLSign), $true))
    $rootCert = $rootReq.CreateSelfSigned((Get-Date).AddDays(-1), (Get-Date).AddYears(10))

    $leafRsa = New-Object -TypeName System.Security.Cryptography.RSACryptoServiceProvider -ArgumentList 2048
    $leafReq = [System.Security.Cryptography.X509Certificates.CertificateRequest]::new(
        $subject, $leafRsa,
        [System.Security.Cryptography.HashAlgorithmName]::SHA256,
        [System.Security.Cryptography.RSASignaturePadding]::Pkcs1)
    [void]$leafReq.CertificateExtensions.Add(
        [System.Security.Cryptography.X509Certificates.X509BasicConstraintsExtension]::new($false, $false, 0, $true))
    [void]$leafReq.CertificateExtensions.Add(
        [System.Security.Cryptography.X509Certificates.X509KeyUsageExtension]::new(
            [System.Security.Cryptography.X509Certificates.X509KeyUsageFlags]::DigitalSignature, $true))

    $serial = New-Object byte[] 8
    [System.Security.Cryptography.RandomNumberGenerator]::Create().GetBytes($serial)
    $leafCert = $leafReq.Create($rootCert, (Get-Date).AddDays(-1), (Get-Date).AddYears(5), $serial)

    # CopyWithPrivateKey 是扩展方法，须按静态方法调用；老运行时回退到 PrivateKey 赋值。
    try {
        $leafWithKey = [System.Security.Cryptography.X509Certificates.RSACertificateExtensions]::CopyWithPrivateKey(
            $leafCert, $leafRsa)
    }
    catch {
        $leafWithKey = $leafCert
        $leafWithKey.PrivateKey = $leafRsa
    }

    # PFX 同时包含叶子与根，签名时链才是完整的
    $collection = New-Object System.Security.Cryptography.X509Certificates.X509Certificate2Collection
    [void]$collection.Add($leafWithKey)
    [void]$collection.Add($rootCert)

    $plainForExport = Convert-SecureStringToPlain $password
    try {
        [System.IO.File]::WriteAllBytes($pfx, $collection.Export(
            [System.Security.Cryptography.X509Certificates.X509ContentType]::Pfx, $plainForExport))
    }
    finally {
        $plainForExport = $null
    }

    [System.IO.File]::WriteAllBytes($rootCer, $rootCert.Export(
        [System.Security.Cryptography.X509Certificates.X509ContentType]::Cert))
    Set-Content -Path $leafThumbFile -Value $leafWithKey.Thumbprint
}

$leafThumbprint = (Get-Content -Path $leafThumbFile).Trim()

$principal = New-Object Security.Principal.WindowsPrincipal([Security.Principal.WindowsIdentity]::GetCurrent())
$isAdmin = $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)

# ③ 信任：受信任的是根证书。包由 AppX 部署服务校验，该服务在系统上下文运行，
#    只看得见本机（LocalMachine）存储，故管理员下必须一并写入，否则报 0x800B010A。
#    导入 Root 会弹确认对话框（无人值守下直接失败），故统一走 .NET 存储 API。
$rootPublic = [System.Security.Cryptography.X509Certificates.X509Certificate2]::new($rootCer)
$locations = @([System.Security.Cryptography.X509Certificates.StoreLocation]::CurrentUser)
if ($isAdmin) {
    $locations += [System.Security.Cryptography.X509Certificates.StoreLocation]::LocalMachine
}

foreach ($location in $locations) {
    foreach ($name in @('Root', 'TrustedPeople')) {
        $store = [System.Security.Cryptography.X509Certificates.X509Store]::new($name, $location)
        $store.Open([System.Security.Cryptography.X509Certificates.OpenFlags]::ReadWrite)
        $store.Add($rootPublic)
        $store.Close()
    }
}

# ④ 打包：/nv 必需——稀疏包引用的徽标 PNG 在外部位置，包内没有这些文件
$makeappx = Resolve-SdkTool 'MakeAppx.exe'
Write-Host '打包稀疏包 ...'
& $makeappx pack /o /d $identityDir /nv /p $msix
if ($LASTEXITCODE -ne 0) {
    throw "MakeAppx 打包失败（退出码 $LASTEXITCODE）。"
}

# ⑤ 签名：PFX 内含根与叶子两张证书，必须用 /sha1 指定叶子，否则 SignTool 报"找到多个证书"。
#     SignTool 只接受命令行明文密码，用完立即清空。
$signtool = Resolve-SdkTool 'signtool.exe'
$plainPassword = Convert-SecureStringToPlain $password

try {
    & $signtool sign /fd SHA256 /f $pfx /p $plainPassword /sha1 $leafThumbprint $msix
    $signExit = $LASTEXITCODE
}
finally {
    $plainPassword = $null
    [GC]::Collect()
}

if ($signExit -ne 0) {
    throw "SignTool 签名失败（退出码 $signExit）。"
}

# ⑥ 旁加载开关：未开启时 Windows 只接受链到商业根证书的包，自签证书即便受信任也会被拒。
#    开启要写 HKLM，故要求管理员权限。
$unlockPath = 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\AppModelUnlock'
$unlock = Get-ItemProperty $unlockPath -ErrorAction SilentlyContinue

if (-not $unlock -or $unlock.AllowAllTrustedApps -ne 1) {
    if (-not $isAdmin) {
        throw '未开启旁加载：请以管理员身份重新运行本脚本，或在「设置 → 系统 → 开发者选项」中开启开发人员模式。'
    }

    Write-Host '开启旁加载（AllowAllTrustedApps）...'
    $null = New-Item -Path $unlockPath -Force
    Set-ItemProperty -Path $unlockPath -Name 'AllowAllTrustedApps' -Value 1 -Type DWord
    Set-ItemProperty -Path $unlockPath -Name 'AllowDevelopmentWithoutDevLicense' -Value 1 -Type DWord
}

# ⑦ 反注册同名旧包：同一版本不能重复注册（否则报 0x80073CF9）
$existing = Get-AppxPackage -Name $packageName -ErrorAction SilentlyContinue
if ($existing) {
    Write-Host '发现已注册的旧包，先反注册 ...'
    $existing | Remove-AppxPackage
}

# ⑧ 注册：把清单登记到系统，并把外部位置指向发布目录
Write-Host "注册稀疏包，外部位置：$publishDir"
Add-AppxPackage -Path $msix -ExternalLocation $publishDir

Write-Host ''
Write-Host '注册完成。' -ForegroundColor Green
Write-Host '请在「设置 → 应用 → 默认应用」中按文件类型把 Pixbian 设为默认；' -ForegroundColor Green
Write-Host '若文件类型图标未刷新，可执行 ie4uinit.exe -show 重建图标缓存。' -ForegroundColor Green
Write-Host "外部位置：$publishDir（改代码后重新发布到本目录即生效，无需重新注册）" -ForegroundColor Green
