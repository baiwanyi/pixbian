# 第三方组件与许可声明

本应用包含以下第三方组件。列出许可信息是为了满足相应许可证的归属与告知要求；
**本文件须随分发产物**（`artifacts/` 发布目录或稀疏包）**一同提供**。

## 视频解码

### FFmpegInteropX

- **用途**：把 FFmpeg 解码能力接入 Windows 的 `MediaPlayer` / `MediaPlayerElement`，用于视频播放。
- **版本**：2.1.0.81200
- **许可**：Apache License 2.0
- **项目主页**：<https://github.com/ffmpeginteropx/FFmpegInteropX>

### FFmpeg

- **用途**：视频与音频解码（含 AV1 的 dav1d 软解码、以及可用的 D3D11 硬件解码）。
- **提供方式**：随 `FFmpegInteropX.Desktop.FFmpeg` 包以动态链接库形式分发（`avcodec-62.dll`、
  `avformat-62.dll`、`avutil-60.dll`、`swscale-9.dll`、`swresample-6.dll`、`avfilter-11.dll`、
  `avdevice-62.dll`）。
- **版本**：8.1.2
- **许可**：GNU Lesser General Public License v2.1 或更高版本（LGPL-2.1-or-later），
  并包含部分以 Zlib 与 MIT 许可分发的组件。构建配置未启用 GPL 组件。
- **项目主页**：<https://ffmpeg.org/>

#### LGPL 要求的履行方式

1. **署名**：应用「设置 → 关于」中标注 FFmpeg 与 FFmpegInteropX 及其许可证名称；
   完整清单见本文件，随分发产物一同提供。
2. **动态链接**：FFmpeg 以独立的动态链接库（DLL）形式随应用分发，未静态链接进可执行文件。
3. **可替换**：上述 DLL 位于应用安装目录，使用者可以自行替换为兼容版本的 FFmpeg 构建；
   替换后应用仍按原有方式调用 FFmpeg 的公开接口。
4. **源码获取**：FFmpeg 的完整对应源码可从 <https://ffmpeg.org/download.html> 获取，
   或向分发方索取；也可使用 <https://github.com/BtbN/FFmpeg-Builds> 的构建脚本自行构建。

## 运行时与界面框架

| 组件 | 版本 | 许可 | 项目主页 |
|---|---|---|---|
| Microsoft.WindowsAppSDK（含 WinUI 3） | 2.4.0 | MIT | <https://github.com/microsoft/WindowsAppSDK> |
| Microsoft.Graphics.Win2D | 1.3.2 | MIT | <https://github.com/microsoft/Win2D> |
| CommunityToolkit.Mvvm | 8.4.0 | MIT | <https://github.com/CommunityToolkit/dotnet> |

> Microsoft.WindowsAppSDK 是元包，会按其组件划分引入若干子包（含
> `Microsoft.WindowsAppSDK.AI` / `.ML` / `.Search` / `.Widgets` 以及 ONNX Runtime、DirectML 等）。
> 本应用**未使用**这些子包的功能，但其程序集会随自包含产物一同分发；相关许可均为 MIT
> 或各项目声明的开源许可，详见各包主页。

### Microsoft.Extensions.*

| 组件 | 版本 | 许可 |
|---|---|---|
| Microsoft.Extensions.DependencyInjection | 8.0.1 | MIT |
| Microsoft.Extensions.Caching.Memory | 8.0.1 | MIT |
| Microsoft.Extensions.Logging.Abstractions | 8.0.2 | MIT |

许可均为 MIT，项目主页 <https://github.com/dotnet/runtime>。

### System.Security.Cryptography.ProtectedData

- **版本**：8.0.0
- **用途**：以 DPAPI 保护本机存储的敏感配置（局域网共享密码哈希）。
- **许可**：MIT，<https://github.com/dotnet/runtime>

## 数据与图像

### Microsoft.Data.Sqlite

- **版本**：8.0.31
- **用途**：本地索引数据库访问（全参数化查询）。
- **许可**：MIT，<https://github.com/dotnet/efcore>
- **传递依赖**：`SQLitePCLRaw.bundle_e_sqlite3` 与 `SQLitePCLRaw.core`（2.1.12，
  Apache-2.0，<https://github.com/ericsink/SQLitePCL.raw>），其中包含原生 SQLite 库
  （公有领域，<https://www.sqlite.org/copyright.html>）。

### SixLabors.ImageSharp

- **版本**：3.1.12
- **用途**：非破坏性图像编辑、局域网共享端的缩略图编码。
- **许可**：Apache License 2.0，**并附商用授权条款**——年营收超过 100 万美元的组织
  需另行取得商业许可。详见 <https://github.com/SixLabors/ImageSharp/blob/main/LICENSE>
  与 <https://sixlabors.com/pricing/>。
- **项目主页**：<https://github.com/SixLabors/ImageSharp>

### MetadataExtractor

- **版本**：2.8.1
- **用途**：EXIF / IPTC / XMP 元数据解析。
- **许可**：Apache License 2.0，<https://github.com/drewnoakes/metadata-extractor-dotnet>

## 测试组件（不随分发产物提供）

| 组件 | 版本 | 许可 |
|---|---|---|
| Microsoft.NET.Test.Sdk | 17.11.1 | MIT |
| xunit | 2.9.2 | Apache-2.0 |
| xunit.runner.visualstudio | 2.8.2 | Apache-2.0 |
| coverlet.collector | 6.0.2 | MIT |

## 维护约定

- 直接依赖与传递依赖的权威清单以 `packages.lock.json` 与
  `dotnet list package --include-transitive` 的输出为准；本文件随依赖变更同步更新。
- 新增或升级第三方组件时必须更新本文件（见 `README.md` 贡献指南）。
