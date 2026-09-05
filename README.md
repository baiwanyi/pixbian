# Pixbian

基于 **WinUI 3** 与 **Windows App SDK 2.4** 的本地相册浏览器，定位为 Windows 照片应用的轻量替代品：为散落在磁盘各处的照片与视频建立本地索引，提供自适应排版的浏览、查看、播放、分类与局域网访问。

**设计原则：本地优先。** 索引数据库、缩略图、设置与日志全部留在本机，不依赖任何云端服务，不上传任何数据。

---

## 技术栈

| 组件 | 版本 / 取值 | 说明 |
|---|---|---|
| UI 框架 | WinUI 3 | `UseWinUI=true` |
| Windows App SDK | 2.4.0 | `Microsoft.WindowsAppSDK`，自包含引入 |
| 运行框架 | .NET 8 | 界面与平台库 `net8.0-windows10.0.26100.0`；领域 / 数据 / Web 服务 `net8.0` |
| 最低 Windows 版本 | Windows 10 1809（Build 17763） | `TargetPlatformMinVersion=10.0.17763.0` |
| 平台 | x64 / ARM64 | `AnyCPU` 已在 csproj 内重定向到 x64 |
| 语言 | C# 12 | `LangVersion 12.0`、`Nullable enable`、`ImplicitUsings enable` |
| SDK 锁定 | 8.0.424 | 由 `global.json`（`rollForward: latestFeature`）锁定 |
| 开发工具 | Visual Studio 2022 17.8+ | 需「.NET 桌面开发」**与**「通用 Windows 平台开发」工作负载 |
| 发布形态 | 非打包自包含 | `WindowsPackageType=None`，无需 MSIX 证书，xcopy 即可部署 |

主要 NuGet 依赖（版本集中在 `Directory.Build.props` 维护，禁止在各 csproj 写死）：

| 包 | 版本 | 用途 |
|---|---|---|
| Microsoft.WindowsAppSDK | 2.4.0 | WinUI 3 运行时与 XAML 编译器 |
| CommunityToolkit.Mvvm | 8.4.0 | `[ObservableProperty]` / `[RelayCommand]` 源生成器 |
| Microsoft.Data.Sqlite | 8.0.11 | 索引库访问，全参数化查询 |
| SixLabors.ImageSharp | 3.1.7 | 非破坏性编辑、Web 端缩略图编码 |
| MetadataExtractor | 2.8.1 | EXIF / IPTC 解析 |
| Microsoft.Extensions.(\*) | 8.0.x | 依赖注入、内存缓存、日志抽象 |
| xunit + Microsoft.NET.Test.Sdk | 2.9.2 / 17.11.1 | 单元测试 |

> **关于 WASDK 2.4 的新 API**：本项目目前**未使用** 2.4 引入的触觉反馈（`Windows.Devices.Haptics`）与 LanguageModel 相关能力，2.4 在此主要作为稳定的运行时与 XAML 编译器基线。后续若要引入，须重新评估 Win10 1809 兼容目标。

> **关于 FFmpeg**：未引入。`Pixbian.Media` 基于系统解码器（`VideoProperties` + `MediaSource`/`MediaPlayer`），零额外体积；MKV 等容器能否播放取决于系统解码器。

---

## 环境要求与准备

| 项目 | 要求 |
|---|---|
| 操作系统 | Windows 10 1809（Build 17763）及以上；推荐 Windows 11 22H2+；x64 / ARM64 |
| .NET SDK | 8.0（LTS） |
| Visual Studio 2022 | 17.8+，「.NET 桌面开发」+「通用 Windows 平台开发」工作负载、Windows SDK 10.0.19041、MSVC v143 |

一键安装工具链（自动跳过已装组件，需要时弹 UAC）：

```powershell
powershell -ExecutionPolicy Bypass -File .\build\Install-Toolchain.ps1
```

> 三个易踩的坑：
>
> 1. 「通用 Windows 平台开发」工作负载**不可省略**：缺失时 PRI 任务加载失败报 `MSB4062`。
> 2. VS 的桌面开发工作负载会**顺带安装 .NET 9 SDK**，因此 `global.json` 必须保留，否则默认用 9.x 编译。
> 3. 非管理员进程调用 `setup.exe` 会以 `ExitCode 5007` 静默失败，脚本已处理提权。

---

## 快速开始

```powershell
# 1. 获取源码
git clone <仓库地址> pixbian
cd pixbian

# 2. 还原依赖
dotnet restore

# 3. 运行（首次编译约 1–3 分钟）
dotnet run --project src/Pixbian -c Debug
```

其他常用命令：

```powershell
# 一键构建并独立启动（不占用终端）
.\Pixbian.ps1                 # 或 .\Pixbian.ps1 -Configuration Release

# 运行全部测试
dotnet test -c Debug

# 发布 Release（x64，非打包自包含）
dotnet publish src/Pixbian -c Release -p:Platform=x64 -o artifacts\win-x64

# 构建 ARM64
dotnet build -c Release -p:Platform=ARM64
```

发布产物可直接拷贝整个 `artifacts\win-x64` 目录到目标机器运行，免安装、免管理员权限。

### 首次使用

1. 启动后点击标题栏「设置」，在「媒体库」中点「添加文件夹」，选择照片目录。
2. 点击「立即索引」，等待扫描完成（状态栏显示进度）。
3. 回到「图库」即可看到缩略图；**双击图片**打开查看器，**双击视频**进入播放。
4. （可选）「设置 → 局域网访问」打开开关并设置端口与密码，同网段设备用浏览器访问即可。

---

## 项目结构

```
pixbian/
├── Directory.Build.props      语言 / 平台 / 包版本集中管理，含 VS 与 PRI 任务路径解析
├── global.json                锁定 .NET 8 SDK
├── Pixbian.ps1                一键构建并独立启动
├── Pixbian.sln
├── build/
│   └── Install-Toolchain.ps1  工具链一键安装
├── tools/
│   └── gen-icon.ps1           由 Assets/app-icon.svg 生成多尺寸 ICO
├── docs/
│   ├── 可行性开发方案.md        架构与实现方案（分层 / 数据模型 / 核心流程 / ADR）
│   ├── WinUI3-界面设计规范.md    视觉规范、XAML 约定与 WinUI 3 实战避坑
│   ├── THIRD-PARTY-NOTICES.md 第三方许可声明
│   └── *.html                 图标与颜色速查表
└── src/
    ├── Pixbian/               WinUI 3 界面层（Views / ViewModels / Controls / Services）
    ├── Pixbian.Core/          领域层：模型、索引、分类引擎、路径安全（net8.0）
    ├── Pixbian.Data/          SQLite 仓储与 Schema 迁移（net8.0）
    ├── Pixbian.Imaging/       WIC 解码、EXIF 读取、非破坏性编辑
    ├── Pixbian.Media/         视频元数据读取（系统 API）
    ├── Pixbian.WebServer/     局域网 HTTP 服务（net8.0）
    └── *.Tests/               xunit 单元测试（Core / Imaging / WebServer）
```

**分层依赖（强制）**

```
Pixbian  →  Core / Data / Imaging / Media / WebServer
Data       →  Core
Imaging / Media / WebServer  →  Core（彼此之间不相互依赖）
Core       →  仅 .NET BCL
```

跨层数据交换统一使用 `Pixbian.Core.Models` 中的模型，禁止向上泄漏 `SqliteDataReader` 等基础设施类型；`Pixbian.Core` 保持纯 `net8.0`，不得引用任何 WinRT / WIC API。

---

## 功能一览

| 模块 | 能力 |
|---|---|
| 浏览 | 自适应行式（Justified）与网格两种视图；虚拟化 + 分页增量加载；按类型 / 收藏 / 分类 / 目录 / 文件名筛选；排序支持随机 / 日期 / 大小 / 名称 × 升 / 降序（随机走固定序列 + 游标分页，翻页不重复不遗漏）；三档缩略图尺寸（128 / 256 / 512，统一按 512 解码） |
| 查看器 | 缩放 0.1×–8×、旋转、幻灯片播放、EXIF 信息面板、按 EXIF 方向自动摆正；显示旋转（界面）与落盘旋转（服务层）分离，落盘一律写入新文件，当前仅服务层可用 |
| 视频 | 系统解码器播放，倍速 0.25×–4×、音量 / 静音、进度拖动；元数据（时长 / 分辨率 / 编码 / 码率 / 音轨） |
| 分类 | 自定义分类 + 正则规则（匹配文件名 / 完整路径 / 扩展名），规则启停、优先级排序、批量重匹配 |
| 索引 | 添加 / 移除扫描源；全量扫描与前缀对账自动清理失效条目；尺寸与时长由后台任务分批回填 |
| 局域网 | 内置 HTTP 服务，浏览器浏览与流式播放；密码保护、只读、IP 限流 |
| 操作 | 多选批量收藏 / 删除 / 重命名；删除走回收站（可还原），或仅从索引移除 |
| 界面 | 自定义标题栏、全窗口背景图 + 玻璃卡片、深浅主题跟随系统 |

### 快捷键（图库页）

| 按键 | 作用 |
|---|---|
| `F5` | 幻灯片播放 |
| `Ctrl+A` / `Ctrl+D` | 全选（自动进入选择模式）/ 取消全选 |
| `Ctrl+C` | 复制文件到剪贴板 |
| `Esc` | 退出选择模式 |
| `Delete` | 删除（移入回收站） |
| `F2` / `F3` | 重命名 / 在资源管理器中打开 |

> 快捷键一律走代码后置 `KeyDown`，不使用 `Page.KeyboardAccelerators`——后者会按官方设计把按键提示追加到页面内所有控件的 ToolTip 上。

---

## 关键实现

**两阶段索引。** 扫描阶段只写文件属性（路径、大小、时间、类型），秒级完成即可浏览；像素尺寸与视频时长由 `MediaMetadataBackfillService` 后台分批补齐（每批 200 条、并发 2、批间让出 1.5 秒、单次最多 25 批）。探测结果分「未探测 / 已完成 / 已失败」三态，**失败必须落为已失败**，否则损坏文件会反复占满批次额度导致回填永不收敛。写回只覆盖 `width` / `height` / `duration_ms`，收藏、评分、分类等用户数据由 `COALESCE` 保护。失效条目在扫描完成后统一对账：取该目录下索引中的全部路径与本次发现集合做内存差集后批量删除。

**缩略图管线。** 请求尺寸按「显示区最长边」语义，先乘 `RasterizationScale` 换算物理像素，再向上量化到 17 档解码档位（128–2560，相邻比值 ≤1.33），最后归一到**统一档位 512**（请求档位 ≤512 一律按 512 解码、缓存与落盘，显示端缩小呈现），缓存键为 `path|bucket`。图片走 `BitmapDecoder` + `BitmapTransform`（缩小用 `Fant`、放大用 `Cubic`，放大上限 2 倍），遵循 EXIF 方向并色彩管理到 sRGB；视频走系统 `SingleItem` 缩略图以保留原始宽高比。解码重采样在线程池经信号量限流（并发 `CPU/2`，钳制 2–8，单条编码 20 秒超时），中间产物为 `byte[]`，只在最后一跳回 UI 线程构造 `BitmapImage`。成品字节另存磁盘缓存（两级哈希分桶、LRU 2GB、条目头带源文件 mtime + size 指纹，命中即跳过全量解码）。`ThumbnailPresenter` 维护「骨架屏 / 缩略图 / 失败占位」三态，以探针 `Image` 的 `ImageOpened`（控件级「内容可绘制」信号）为门闩，再等一渲染帧后切换终值；图片淡入与骨架呼吸动画当前均已停用，直接置终值以避免密集就绪时同帧启动上百个动画。查看器走两级加载：先以 512 预览垫场，全图解码完成后再替换。

**自适应布局。** `JustifiedPanel` 按宽高比贪心分行、行内等比缩放填满可用宽度，并把实际分配尺寸回写给条目（`IDisplaySizeAware`），使位图按真实显示尺寸解码——否则位图按名义尺寸解码后被拉伸会发虚。

**局域网服务。** 基于 `TcpListener` 自研 HTTP/1.1（免 `urlacl`、免管理员），逐连接独立任务。路由：`/`、`/index.html`、`/app.css`、`/app.js`、`/api/health`（不鉴权不限流，供网络探测）、`/api/login`、`/api/items`（分页，`kind` / `q` / `offset` / `limit`，`limit` 默认 60、钳制 1–200）、`/api/items/{id}`、`/thumb/{id}`（最长边 320）、`/media/{id}`（支持 Range，供视频拖动）。非 `GET` / `HEAD` 一律 `405`，服务为**只读**；媒体与缩略图只接受数据库主键，绝不接受客户端传入的路径，且落盘前再经 `PathGuard` 校验位于已启用库目录内；对外 JSON 不含绝对路径。

**数据模型。** `media_items`（路径唯一键）、`library_folders`、`categories`、`category_rules`（外键级联删除）、`tags`、`media_tags`；时间以 ISO8601 文本存 UTC，布尔与枚举存整数。Schema 以 `PRAGMA user_version` 版本化迁移，已发布脚本禁止修改。

---

## 数据存储位置

| 数据 | 路径 |
|---|---|
| 索引数据库 | `%LOCALAPPDATA%\Pixbian\index.db` |
| 缩略图缓存目录 | `%LOCALAPPDATA%\Pixbian\Thumbs\` |
| 应用配置 | `%LOCALAPPDATA%\Pixbian\settings.json` |
| 崩溃日志 | `%LOCALAPPDATA%\Pixbian\Logs\crash.log` |

数据库含本地文件索引，属用户隐私数据，切勿提交到版本库。

---

## 安全设计

- **SQL 注入**：全部参数化查询，禁止字符串拼接；动态条件统一使用 `(@p IS NULL OR col = @p)` 形式。
- **路径穿越**：所有外部传入路径经 `PathGuard` 规范化为「以分隔符结尾的绝对路径」后做前缀比对，拒绝 `..` 与越界路径。
- **目录枚举**：索引扫描跳过重解析点（符号链接 / 联接）、隐藏与系统文件，规避目录环与越权读取。
- **ReDoS**：用户正则四道防护——① 构造时强制 1 秒匹配超时；② 超时即标记该规则为危险并跳过；③ 整批匹配 30 秒总时限；④ 待匹配文本截断至 4096 字符。
- **认证**：Web 密码使用 PBKDF2（SHA-256，10 万次迭代，16 字节随机盐，32 字节哈希）存储，格式为 `迭代数(十进制).盐(Base64).哈希(Base64)`；密码校验用 `CryptographicOperations.FixedTimeEquals` 恒定时间比较，会话令牌只在服务端字典中按哈希查找、不参与客户端可控的比较路径；令牌为 256 位 CSPRNG，仅存内存，8 小时过期，重启即失效。
- **暴力破解与滥用**：同一 IP 登录失败 5 次锁定 15 分钟；固定窗口限流 60 次 / 分钟。
- **密钥**：不硬编码任何密钥，密码哈希仅存于本机配置文件；应用为**单实例**（Mutex），重复启动会提示。

局域网服务**默认关闭**，开启时建议设置密码。

---

## 测试

```powershell
dotnet test -c Debug
```

三套 xunit 测试，共 **181 个用例**（Core 136 / WebServer 33 / Imaging 12），可在无 UI、无网络的 CI 环境通过：

- `Pixbian.Core.Tests`：`PathGuard`、媒体文件分类器、索引服务、查询构建、分类规则引擎、SQLite 仓储、元数据回填
- `Pixbian.WebServer.Tests`：鉴权与限流、HTTP 解析、路由与 Range
- `Pixbian.Imaging.Tests`：图像编辑服务

---

## 当前状态与已知未完成项

已实现并可用：索引与浏览、查看器、视频播放、正则分类、局域网访问、设置与主题。

以下为代码中存在但**尚未接线 / 尚未完成**的部分，供贡献者参考：

- `LibraryWatcherService` 已实现（FileSystemWatcher + Channel 聚合 + 1 秒静默期），但未注册到 DI，自动监控尚未启用。
- `rating`（评分）已存在于数据模型与数据库，界面未暴露。
- 非破坏性编辑（`ImageEditService` 的裁剪、调色、90 度整数倍旋转）已实现，但界面尚未暴露任何落盘编辑入口；查看器的「左转 / 右转」只改显示角度，不修改文件。
- `tags` / `media_tags` 表已建立，尚无界面。
- 深色主题下窗口背景图仍使用 `light.jpg`，文字对比度有待优化。
- 仓库暂未包含 `LICENSE` 文件。

---

## 贡献指南

1. 从 `main` 切出分支：`feature/<简述>` 或 `fix/<简述>`。
2. 本地确保 `dotnet build` 与 `dotnet test` 全绿。
3. 推送分支并向 `main` 发起 PR，说明变更动机、范围与验证方式。
4. 至少一次 Review 通过后方可合并。

**编码规范**

- 静态分析：`EnableNETAnalyzers=true`、`AnalysisLevel=latest-recommended`；本地警告不阻断，CI 用 `-warnaserror` 提升到错误，提交前请自查 warning。
- 异步：I/O 路径一律 `async/await`，禁止 `.Result` / `.Wait()`。
- 数据访问：100% 参数化查询，动态条件统一 `(@p IS NULL OR col = @p)`。
- 路径安全：外部路径必须经 `PathGuard` 校验，禁止裸拼接。
- 包版本：只改 `Directory.Build.props`，且仅使用稳定发行版。
- 新增功能须配套单元测试，并同步更新 `README.md`；引入或升级第三方组件须更新 `docs/THIRD-PARTY-NOTICES.md`。

---

## 第三方许可摘要

| 组件 | 许可 |
|---|---|
| Microsoft.WindowsAppSDK | MIT |
| CommunityToolkit.Mvvm | MIT |
| Microsoft.Data.Sqlite | Apache-2.0 |
| MetadataExtractor | Apache-2.0 |
| SixLabors.ImageSharp | Apache-2.0 + 商用授权条款（营收超 100 万美元的组织需商业许可） |

完整声明见 [`docs/THIRD-PARTY-NOTICES.md`](docs/THIRD-PARTY-NOTICES.md)。若计划分发给企业使用，请先评估 ImageSharp 的商用条款。

---

## 参考资料

- [Windows App SDK 文档](https://learn.microsoft.com/windows/apps/windows-app-sdk/)
- [WinUI 3 文档](https://learn.microsoft.com/windows/apps/winui/winui3/)
- [WinUI 3 Gallery](https://github.com/microsoft/WinUI-Gallery) — 控件交互式示例
- [Windows App SDK 示例](https://github.com/microsoft/WindowsAppSDK-Samples)
- 仓库内 `docs/可行性开发方案.md` 与 `docs/WinUI3-界面设计规范.md`
