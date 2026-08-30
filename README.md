# Pixbian

基于 **WinUI 3** 的本地媒体管理桌面应用，定位为 Windows 照片应用的增强替代品。覆盖媒体库索引、图片浏览与编辑、视频播放、正则自动分类、随机发现与局域网跨设备访问的完整闭环。

**设计原则：本地优先。** 索引数据库、缩略图缓存、用户配置全部保存在本机，不依赖任何云端服务，不上传任何数据。

---

## 当前状态

| 项目 | 状态 |
|---|---|
| 功能开发 | **全部完成**（M0–M7，8 大需求均有实现） |
| 测试 | 160 个用例全部通过 |
| 收尾（M8） | 进行中：发布验证、兼容矩阵 |

> 详细的需求可行性评级、架构设计、数据模型、安全设计与里程碑排期，请查阅 [`docs/可行性开发方案.md`](docs/可行性开发方案.md)；第三方许可见 [`THIRD-PARTY-NOTICES.md`](THIRD-PARTY-NOTICES.md)。

---

## 功能规划

| 模块 | 能力 |
|---|---|
| 图片浏览 | JPEG / PNG / GIF / WebP / HEIC / AVIF / RAW 等格式；缩放、旋转、全屏、幻灯片、EXIF 查看、裁剪与基础编辑（非破坏性，原图只读） |
| 视频管理 | MP4 / MOV / AVI / MKV 等格式；播放暂停、进度条、音量、全屏、倍速（0.25×–4×）、缩略图预览、元数据（时长 / 分辨率 / 编码 / 码率） |
| 局域网访问 | 内置 HTTP 服务，同网段设备用浏览器即可浏览与播放；密码保护、只读模式、IP 限流 |
| 正则分类 | 自定义正则规则按文件名 / 路径 / 扩展名自动归类；规则启停、优先级排序、手动调整、批量重匹配；内置 ReDoS 防护 |
| 发现模式 | 随机浏览，可限定范围为图片或视频；定时切换、快捷键与手势、收藏或跳过 |
| 媒体库管理 | 添加 / 移除扫描源；自动监控新增、删除、重命名；SQLite 索引；按日期 / 大小 / 类型 / 分类 / 标签筛选与搜索 |
| 界面 | 左侧导航 + 中间内容区 + 右侧详情面板；深浅主题、网格 / 列表视图、缩略图尺寸调节、多选批量操作 |
| 性能与兼容 | 虚拟化列表、三级缩略图缓存、后台索引；支持 Windows 10 1809 及以上 |

---

## 环境要求

### 操作系统

| | 版本 |
|---|---|
| 最低支持 | Windows 10 1809（Build 17763） |
| 推荐 | Windows 11 22H2 及以上 |
| 架构 | x64 / ARM64 |

### 开发工具

| 组件 | 版本 | 说明 |
|---|---|---|
| .NET SDK | 8.0（LTS） | 必需 |
| Visual Studio 2022 | 17.8+ | 需「.NET 桌面开发」**与**「通用 Windows 平台开发」工作负载 |
| Windows SDK | 10.0.19041.0 | 随 VS 工作负载安装；TFM 的 26100 WinRT 投影由 NuGet 包提供，无需另行安装该版本 SDK |
| MSVC 生成工具 | v143 | 随 VS 工作负载安装，XAML 编译器依赖 |
| Windows App SDK | 2.4 | **通过 NuGet 自动引入**，无需单独安装运行时 |

### 安装工具链

若本机尚未安装 .NET SDK 与 Windows SDK，按以下顺序执行（PowerShell，约 15–25 分钟）：

```powershell
# 1. 安装 .NET 8 SDK
winget install --id Microsoft.DotNet.SDK.8 --exact `
    --accept-source-agreements --accept-package-agreements

# 2. 为 VS2022 追加工作负载与组件（.NET 桌面开发 / 通用 Windows 平台开发 / Win10 SDK / MSVC）
& "C:\Program Files (x86)\Microsoft Visual Studio\Installer\setup.exe" modify `
    --installPath "C:\Program Files\Microsoft Visual Studio\2022\Community" `
    --add Microsoft.VisualStudio.Workload.ManagedDesktop `
    --add Microsoft.VisualStudio.Component.Windows10SDK.19041 `
    --add Microsoft.VisualStudio.Component.VC.Tools.x86.x64 `
    --add Microsoft.VisualStudio.Workload.Universal `
    --includeRecommended --passive --norestart

# 3. 重新打开终端后验证
dotnet --list-sdks        # 应出现 8.0.x
dotnet --list-runtimes    # 应出现 Microsoft.WindowsDesktop.App 8.0.x
```

> 更推荐直接执行仓库内的脚本：`powershell -ExecutionPolicy Bypass -File .\build\Install-Toolchain.ps1`，
> 它会自动跳过已装组件，并在需要时弹出 UAC 提权窗口。
>
> **四个易踩的坑**：
>
> 1. 命令行入口必须用 `setup.exe`，**不能**用同目录的 `vs_installer.exe`（后者只是 UI 启动器，传参不会执行安装）。
> 2. 非管理员进程调用 `setup.exe` 会以 `ExitCode 5007` **静默失败**，必须显式提权（脚本已处理）。
> 3. 「通用 Windows 平台开发」工作负载不可省略。缺失时编译报 `MSB4062`，无法加载 `ExpandPriContent` / `RemovePayloadDuplicates` 任务。
> 4. VS 的 .NET 桌面开发工作负载会**顺带安装 .NET 9 SDK**，导致 `dotnet build` 默认选中 9.x。
>    仓库已用 `global.json` 锁定 `8.0.424`，请勿删除该文件。

---

## 安装步骤

以下步骤适用于从零开始在本机搭建可运行环境（Windows 10 1809 及以上）。若已具备 .NET 8 SDK 与 Visual Studio 相应工作负载，可直接跳到步骤 4。

1. **确认系统版本**：设置 → 系统 → 关于，确认 Windows 版本不低于 1809（Build 17763）。x64 与 ARM64 均支持。
2. **安装开发工具链**：按上文「环境要求 → 安装工具链」操作，或直接执行仓库脚本（自动跳过已装组件，需要时弹 UAC 提权）：
   ```powershell
   powershell -ExecutionPolicy Bypass -File .\build\Install-Toolchain.ps1
   ```
3. **获取源码**：
   ```powershell
   git clone <仓库地址> photo.apps
   cd photo.apps
   ```
4. **还原依赖**：
   ```powershell
   dotnet restore
   ```
5. **编译**：
   ```powershell
   dotnet build -c Debug
   ```
   > 首次编译会触发 NuGet 还原与 XAML / PRI 生成，耗时较长（1–3 分钟）。若报 `MSB4062` / `MSB3073`，几乎都是缺少「通用 Windows 平台开发」工作负载或 `global.json` 被改动，见下文「常见问题」。
6. **运行**：参考下方「快速开始」。

> 关键约束：`global.json` 锁定 SDK 为 `8.0.424`，**请勿删除**；本项目默认按 `x64` 平台构建与运行（ARM64 仅用于交叉编译发布，详见「快速开始 → 其他常用命令」）。

---

## 快速开始

### 方式一：直接运行（推荐）

```powershell
dotnet run --project src/Pixbian -c Debug
```

首次运行会自动还原依赖并编译，稍慢（约 30 秒）；之后启动约 5 秒。应用窗口标题为 **Pixbian**。

> `dotnet run` 会占用当前终端，关闭终端即退出应用。想让终端保持可用，请用方式二。

### 方式二：编译后运行可执行文件

```powershell
# 构建
dotnet build -c Debug

# 运行（路径中的 x64 对应平台）
.\src\Pixbian\bin\x64\Debug\net8.0-windows10.0.26100.0\Pixbian.exe
```

这种方式下应用独立于终端运行，也可以直接双击 exe 启动。

### 其他常用命令

```powershell
# 仅还原依赖
dotnet restore

# 运行测试（Core / Imaging / WebServer 三套，共 126 个测试方法）
dotnet test -c Debug

# 发布 Release（x64）
dotnet publish src/Pixbian -c Release -p:Platform=x64 -o artifacts\win-x64

# 构建 ARM64
dotnet build -c Release -p:Platform=ARM64
```

发布产物为**非打包自包含**形态，可直接拷贝整个 `artifacts\win-x64` 目录到目标机器运行，无需安装，无需管理员权限。

> **注意**：项目已用 `global.json` 锁定 .NET 8 SDK，请勿删除该文件。若本机装有多个 SDK（VS 安装程序会附带 .NET 9），缺少该文件会导致默认用 9.x 编译而失败。

## 首次使用

1. 启动应用后点击左下角「**设置**」。
2. 点击「**添加文件夹**」，选择一个存放照片的目录。
3. 点击「**立即索引**」，等待扫描完成（进度显示在下方状态栏）。
4. 回到「**全部照片**」即可看到缩略图网格。
5. **双击**任意图片打开查看器（缩放 / 旋转 / 幻灯片 / EXIF）；**双击视频**直接播放。

### 局域网访问（可选）

1. 「设置」→「局域网访问」→ 打开开关，设置端口与访问密码（可留空，仅限可信局域网）。
2. 点击「**保存并应用**」，界面会显示访问地址（如 `http://192.168.0.111:8756/`）。
3. 同一局域网内的手机 / 平板浏览器打开该地址即可浏览、搜索与播放视频（支持拖动进度条）。

安全机制：**只读模式**（浏览器端无法修改或删除任何文件）、PBKDF2 加盐密码哈希、会话令牌、IP 限流（60 次/分钟）、登录失败 5 次锁定 15 分钟。服务默认关闭；应用退出后访问立即失效。

### 分类规则（可选）

「分类」页新建分类（如「手机照片」），再添加正则规则（如 `IMG_\d{4}`）并选择目标分类，点击「重新匹配全部」即可自动归类。规则支持启停、优先级排序与三种匹配目标（文件名 / 完整路径 / 扩展名）；正则在输入时即时校验，存在回溯风险（ReDoS）的规则会被强制超时跳过并提示。

### 发现模式（可选）

「发现」页随机浏览媒体库：可选范围（全部 / 仅图片 / 仅视频）、定时切换（3–30 秒）、收藏或跳过；快捷键：空格=下一张、F=收藏、X=跳过、Esc=停止。

索引数据库位于 `%LOCALAPPDATA%\Pixbian\index.db`，删除它即可重置全部索引（不会删除原始照片）。应用为**单实例运行**，重复启动会聚焦提示。

## 使用示例

### 终端用户场景

**场景一：建立第一个媒体库**
1. 启动应用，进入「设置」→「添加文件夹」，选择 `D:\Photos`。
2. 点击「立即索引」，状态栏显示扫描进度；完成后「全部照片」出现缩略图网格。
3. 双击图片进入查看器：滚轮缩放、右键旋转、空格进入幻灯片、`E` 查看 EXIF。

**场景二：把照片共享到手机**
1. 「设置」→「局域网访问」打开开关，设置端口（如 `8756`）与访问密码。
2. 点击「保存并应用」，复制界面显示的地址（如 `http://192.168.0.111:8756/`）。
3. 同一局域网内手机 / 平板浏览器打开该地址，即可在只读模式下浏览、搜索、播放视频。

**场景三：用正则自动归类「手机照片」**
1. 「分类」页新建分类「手机照片」。
2. 添加规则：名称 `IMG_ 开头`，正则 `^IMG_\d{4}`，匹配目标「文件名」，优先级 `10`。
3. 点击「重新匹配全部」，符合规则的文件（如 `IMG_2024.jpg`）自动归入该分类。

**场景四：发现模式做屏保式轮播**
1. 「发现」页选择范围「仅图片」，定时切换设为 `5` 秒。
2. 按空格开始随机播放；`F` 收藏、`X` 跳过、`Esc` 停止。

### 开发者 API 示例

`Pixbian.Core` 与 `Pixbian.Data` 是纯净的领域 / 数据层，可在其他 .NET 8 项目中引用，以复用索引、分类、发现与 SQLite 存储能力。以下示例均使用已验证的公开 API。

**1. 路径安全校验（防路径穿越）**

```csharp
using Pixbian.Core.Utilities;

// 规范化目录（解析符号链接、统一分隔符、去除尾随斜杠）
string root = PathGuard.NormalizeDirectory(@"D:\Photos");

// 校验外部传入路径是否仍位于授权根目录内
if (PathGuard.IsInside(root, userProvidedPath))
{
    // 安全：路径未逃逸授权范围
}
```

**2. 正则分类匹配**

```csharp
using Pixbian.Core.Models;
using Pixbian.Core.Services;

var rule = new CategoryRule
{
    Name = "手机照片",
    Pattern = @"^IMG_\d{4}",
    Target = RuleMatchTarget.FileName,
    CategoryId = 1,
    IsCaseSensitive = false
};

// 编译为带超时保护的规则集（ReDoS 防护在引擎内部完成）
var compiled = new List<CompiledRule> { new(rule, new Regex(rule.Pattern, RegexOptions.IgnoreCase)) };

RuleMatchResult result = CategoryRuleEngine.Match(mediaItem, compiled);
// result.CategoryId 命中分类；未命中则为 null
```

**3. 媒体库查询与写入**

```csharp
// mediaItems / libraryFolders 通常由依赖注入提供（实现 IMediaItemRepository 等接口）
long count = await mediaItems.CountAsync(cancellationToken);
MediaItem? one = await mediaItems.GetByIdAsync(id, cancellationToken);
await mediaItems.UpsertBatchAsync(newItems, cancellationToken);
```

**4. 发现模式随机抽取**

```csharp
using Pixbian.Core.Services;

var discover = new DiscoverService(mediaItems); // mediaItems: IMediaItemRepository
MediaItem? next = await discover.PickRandomAsync(cancellationToken: cancellationToken);
// 返回随机抽取的条目；库为空时返回 null
```

> 分层约束：调用方只能依赖 `Pixbian.Core` / `Pixbian.Data` 的公开接口，不得反向引用 `Pixbian`（UI 层），也不得向上泄漏 `SqliteDataReader` 等基础设施类型。

## 常见问题

**Q：`dotnet run` 报 "requires a supported Windows architecture"？**

A：这是 AnyCPU 平台导致的。项目已在 `Pixbian.csproj` 中把 AnyCPU 重定向到 x64，若仍出现请确认未误删该行。

**Q：提示找不到 SDK 版本？**

A：确认 `global.json` 存在且版本为 `8.0.424`，然后执行 `dotnet --list-sdks` 检查本机是否已安装 .NET 8 SDK。

**Q：编译时 XAML 报错但只看到 MSB3073？**

A：XAML 编译器的真实错误会被 MSB3073 掩盖。改用 Visual Studio 的 MSBuild 可以看到具体信息：

```powershell
& "C:\Program Files\Microsoft Visual Studio\2022\Community\MSBuild\Current\Bin\MSBuild.exe" `
    src/Pixbian/Pixbian.csproj -p:Configuration=Debug -p:Platform=x64 -t:Build
```

---

## 目录结构

```
photo.apps/
├── Directory.Build.props        语言版本、目标平台、NuGet 版本集中管理
├── Pixbian.sln                解决方案
├── README.md
├── .gitignore
├── build/
│   └── Install-Toolchain.ps1    工具链一键安装脚本
├── docs/
│   └── 可行性开发方案.md          完整方案（架构 / 数据模型 / 安全 / 排期）
└── src/
    ├── Pixbian/               WinUI 3 界面层（net8.0-windows10.0.26100.0）
    ├── Pixbian.Core/          领域层：模型、索引、分类、发现、路径安全（net8.0）
    ├── Pixbian.Data/          SQLite 数据访问与 Schema 迁移（net8.0）
    ├── Pixbian.Imaging/       图像解码、EXIF、裁剪编辑（M3）
    ├── Pixbian.Media/         视频播放与元数据（M4）
    ├── Pixbian.WebServer/     局域网 HTTP 服务（M7）
    └── Pixbian.Core.Tests/    单元测试（xunit）
```

**分层依赖规则（强制）**

```
Pixbian  →  Core / Data / Imaging / Media / WebServer
Data       →  Core
Imaging / Media / WebServer  →  Core（彼此之间不相互依赖）
Core       →  仅 .NET BCL
```

下层项目禁止反向引用上层，跨层数据交换统一使用 `Pixbian.Core.Models` 中的模型，禁止向上泄漏 `SqliteDataReader` 等基础设施类型。

---

## 技术栈

| 类别 | 选型 |
|---|---|
| UI 框架 | WinUI 3（Windows App SDK 2.4） |
| 运行框架 | .NET 8 (`net8.0-windows10.0.26100.0`，`TargetPlatformMinVersion = 10.0.17763.0`) |
| MVVM | CommunityToolkit.Mvvm（源生成器） |
| 数据库 | SQLite（Microsoft.Data.Sqlite，全参数化查询） |
| 图像处理 | WIC 解码为主，ImageSharp 为编辑与回退 |
| 元数据 | MetadataExtractor（EXIF） |
| 视频 | FFmpegInteropX（M4 引入） |
| 局域网服务 | TcpListener 自研 HTTP/1.1（M7 引入） |
| 测试 | xunit + Microsoft.NET.Test.Sdk |

### 为什么不用 EF Core

本项目的数据访问以「批量 Upsert + 分页查询 + 前缀对账」为主，手写参数化 SQL 能精确控制事务与索引策略，且天然满足「禁止 SQL 拼接」的安全要求；EF Core 的启动开销与包体积对桌面应用不划算。

---

## 数据存储位置

| 数据 | 路径 |
|---|---|
| 索引数据库 | `%LOCALAPPDATA%\Pixbian\index.db` |
| 缩略图缓存 | `%LOCALAPPDATA%\Pixbian\Thumbs\` |
| 应用配置 | `%LOCALAPPDATA%\Pixbian\settings.json` |
| 日志 | `%LOCALAPPDATA%\Pixbian\Logs\` |

> 以上路径均已加入 `.gitignore`。数据库文件含本地文件索引，**请勿提交到版本库**。

---

## 安全说明

本项目涉及本地文件系统访问与局域网服务，已落实以下措施：

- **SQL 注入**：100% 参数化查询，动态条件一律使用 `(@p IS NULL OR col = @p)` 形式，禁止字符串拼接。
- **路径穿越**：所有外部传入路径经 `PathGuard` 规范化后做目录前缀比对，拒绝 `..`、越界驱动器号与未授权 UNC 路径。
- **目录枚举**：扫描时跳过重解析点（符号链接 / 联接），避免目录环与越权读取。
- **认证**：Web 访问密码使用 PBKDF2（10 万次迭代 + 随机盐）存储哈希，禁止明文或可逆加密；会话令牌使用 CSPRNG 生成并设过期时间。
- **ReDoS**：用户自定义正则强制设置匹配超时，批量匹配设总时限，输入长度截断。
- **密钥**：禁止硬编码任何密钥；配置文件权限限制为仅当前用户可读写。
- **Web 响应头**：强制下发 `X-Content-Type-Options`、`X-Frame-Options`、`Referrer-Policy` 与 CSP。

局域网服务**默认关闭**，开启时强制设置密码并默认只读。

---

## 开发路线

| 里程碑 | 内容 | 状态 |
|---|---|---|
| M0 | 工程骨架、工具链脚本、最小可运行外壳 | ✅ 完成 |
| M1 | SQLite 数据层、索引服务、文件夹监控、路径安全 | ✅ 完成 |
| M2 | 导航外壳、主题、网格 / 列表、详情面板、多选批量、设置 | ✅ 完成 |
| M3 | 图片查看器、EXIF、裁剪与基础编辑（非破坏性） | ✅ 完成 |
| M4 | 视频播放器、元数据（基于系统解码器） | ✅ 完成 |
| M5 | 正则分类规则引擎（含 ReDoS 四道防护）、批量重匹配 | ✅ 完成 |
| M6 | 发现模式（随机浏览、定时切换、快捷键） | ✅ 完成 |
| M7 | 局域网 Web 服务（TcpListener、Range、鉴权、限流） | ✅ 完成 |
| M8 | 发布验证、兼容矩阵、文档收尾 | 进行中 |

> **M4 决策记录**：评估后未引入 FFmpegInteropX——其 2.1 版本要求 SDK 26100 投影，与 Win10 1809 兼容基线冲突（CS1705），且包体积实测 175.6 MB。视频改为系统解码器（`VideoProperties` + `MediaClip`），零体积、保持兼容；MKV 等容器依赖系统解码器支持。详见 `THIRD-PARTY-NOTICES.md` 的决策记录。

---

## 第三方许可

| 组件 | 许可 | 备注 |
|---|---|---|
| Microsoft.WindowsAppSDK | MIT | |
| CommunityToolkit.Mvvm | MIT | |
| Microsoft.Data.Sqlite | Apache-2.0 | |
| MetadataExtractor | Apache-2.0 | |
| SixLabors.ImageSharp | Apache-2.0 + 商用授权条款 | **营收超 100 万美元的组织需商业许可** |
| FFmpegInteropX / FFmpeg | LGPL-2.1 或 GPL-2.0 | M4 将锁定 LGPL-only 构建配置 |

完整的许可声明文件 `THIRD-PARTY-NOTICES.md` 将在 M4 随 FFmpeg 引入时补全。

> **使用提示**：若你计划将本项目分发给企业使用，请先评估 ImageSharp 的商用授权条款，必要时替换为 `SkiaSharp`（MIT）。

---

## 贡献指南

欢迎以 Issue、Pull Request 或文档修订的方式参与贡献。开始前请先完成一次本地成功构建（见上文「安装步骤」）。

### 前置条件

- Windows 10 1809 及以上，已安装 .NET 8 SDK 与 Visual Studio 2022「.NET 桌面开发」+「通用 Windows 平台开发」工作负载（见 `build/Install-Toolchain.ps1`）。
- Git 与所用编辑器的 C# 支持（VS / Rider / VS Code + C# Dev Kit）。

### 工作流

1. 从 `main` 切出分支：`feature/<简述>`（新功能）或 `fix/<简述>`（缺陷修复）。
2. 在本地完成开发与自测，确保 `dotnet build` 与 `dotnet test` 全绿。
3. 推送分支并向 `main` 发起 PR，描述变更动机、范围与验证方式，并关联相关 Issue。
4. 至少一次 Review 通过、CI 绿色后方可合并；合并后删除临时分支。

### 编码规范

- 语言：`LangVersion 12`、`Nullable enable`、`ImplicitUsings enable`（由 `Directory.Build.props` 统一强制）。
- 静态分析：`EnableNETAnalyzers=true`、`AnalysisLevel=latest-recommended`；本地不将警告视为错误，但 CI 通过 `-warnaserror` 把警告提升到错误级别，提交前请自查 warning。
- 异步：I/O 路径一律 `async/await`，禁止 `.Result` / `.Wait()` 阻塞。
- 数据访问：100% 参数化查询，禁止字符串拼接 SQL；动态条件统一使用 `(@p IS NULL OR col = @p)`。
- 路径安全：所有外部路径必须经 `PathGuard` 规范化与 `IsInside` 校验，禁止直接拼接用户路径。
- 正则：用户可达的正则必须设置匹配超时（参考 `CategoryRuleEngine` 的 ReDoS 防护），禁止无界回溯。

### 分层与依赖规则（强制）

```
Pixbian  →  Core / Data / Imaging / Media / WebServer
Data       →  Core
Imaging / Media / WebServer  →  Core（彼此之间不相互依赖）
Core       →  仅 .NET BCL
```

跨层数据交换统一使用 `Pixbian.Core.Models` 中的模型，禁止向上泄漏基础设施类型。

### 包版本管理

所有 NuGet 版本集中在 `Directory.Build.props` 的「包版本」分组维护。新增或升级依赖时**只改此处**，禁止在各 `.csproj` 内写死 `Version`，且仅使用稳定发行版。

### 测试要求

- 新增功能须配套单元测试；核心算法（分类引擎、路径守护、索引对账）建议覆盖边界与异常分支。
- 运行全部测试：
  ```powershell
  dotnet test -c Debug
  ```
  当前包含 `Pixbian.Core.Tests` / `Pixbian.Imaging.Tests` / `Pixbian.WebServer.Tests` 三套，共 126 个测试方法（合并 Theory 数据集后约 160 用例）。
- 测试须可在无 UI、无网络的 CI 环境通过，不依赖真实外部资源。

### 资源与工具

- 修改应用图标：编辑 `src/Pixbian/Assets/app-icon.svg` 后运行 `powershell -ExecutionPolicy Bypass -File tools\gen-icon.ps1` 重新生成多尺寸 ICO。
- 一键构建运行：仓库根目录的 `run.ps1` / `run.cmd`（构建后独立启动进程，不占用终端）。

### 文档与许可

- 用户可见行为变更须同步更新 `README.md` 与 `docs/可行性开发方案.md`。
- 引入新第三方组件或升级版本时，须在 `THIRD-PARTY-NOTICES.md` 追加 / 更新许可条目。
- 本项目以「本地优先、不依赖云端」为原则；提交内容须保持与现有许可（MIT / Apache-2.0 等）兼容。
