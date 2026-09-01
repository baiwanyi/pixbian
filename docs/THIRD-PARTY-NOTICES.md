# 第三方组件许可声明（THIRD-PARTY NOTICES）

本文档列出 Pixbian 及其发行包中所含第三方组件的许可信息。依照各许可条款的要求，使用与分发本应用时须一并保留本声明。

**最近更新**：2026-09-02（对齐当前代码基线）

---

## 发行包内组件

### Microsoft.WindowsAppSDK

| | |
|---|---|
| 版本 | 2.4.0 |
| 许可 | MIT |
| 来源 | https://github.com/microsoft/WindowsAppSDK |
| 版权 | Copyright (c) Microsoft Corporation |

### CommunityToolkit.Mvvm

| | |
|---|---|
| 版本 | 8.4.0 |
| 许可 | MIT |
| 来源 | https://github.com/CommunityToolkit/dotnet |
| 版权 | Copyright (c) .NET Foundation and Contributors |

### Microsoft.Data.Sqlite（含 SQLite 本体）

| | |
|---|---|
| 版本 | 8.0.11 |
| 许可 | **Apache-2.0**（托管的 ADO.NET 提供程序部分，随 dotnet/aspnetcore 分发） |
| 附带 | SQLite 本体（`SQLitePCLRaw` 所链接的原生库）为 **Public Domain**，无使用限制 |
| 来源 | https://github.com/dotnet/aspnetcore / https://sqlite.org |
| 版权 | Copyright (c) .NET Foundation and Contributors |

> 更正说明：此前本条记为 MIT，与来源仓库不符。`Microsoft.Data.Sqlite` 的托管代码位于
> `dotnet/aspnetcore`，该仓库整体采用 Apache-2.0；MIT 是 `dotnet/runtime` 与
> `dotnet/aspnetcore` 之外的部分 .NET 仓库所用许可，不可混用。

### SixLabors.ImageSharp

| | |
|---|---|
| 版本 | 3.1.7 |
| 许可 | Apache-2.0 与 Six Labors Split License 双许可 |
| 来源 | https://github.com/SixLabors/ImageSharp |
| 版权 | Copyright (c) Six Labors |

> **商用授权提示**：ImageSharp 采用 Split License——开源项目与年总收入低于 100 万美元的组织可免费使用 Apache-2.0 条款；年总收入超过 100 万美元的组织须购买商业许可（详见 https://sixlabors.com/pricing/ ）。本项目默认遵循 Apache-2.0 使用。

### MetadataExtractor

| | |
|---|---|
| 版本 | 2.8.1 |
| 许可 | Apache-2.0 |
| 来源 | https://github.com/drewnoakes/metadata-extractor |
| 版权 | Copyright (c) Drew Noakes |

### Microsoft.Extensions.*（DependencyInjection / Caching.Memory / Logging.Abstractions）

| | |
|---|---|
| 版本 | 8.0.1 / 8.0.1 / 8.0.2 |
| 许可 | MIT |
| 来源 | https://github.com/dotnet/runtime |
| 版权 | Copyright (c) Microsoft Corporation |

---

## 开发与测试期组件（不随发行包分发）

| 组件 | 版本 | 许可 |
|---|---|---|
| xunit | 2.9.2 | Apache-2.0 |
| xunit.runner.visualstudio | 2.8.2 | Apache-2.0 |
| Microsoft.NET.Test.Sdk | 17.11.1 | MIT |
| coverlet.collector | 6.0.2 | MIT |

> `FFmpegInteropX` 的版本号虽在 `Directory.Build.props` 中定义，但**无任何项目引用**，
> 不进入编译产物与发行包，故不计入上表。详见下节决策记录。

---

## 关于 FFmpegInteropX 的决策记录

本项目在 M4 里程碑**评估但最终未引入** FFmpegInteropX，视频播放改走系统解码器。
决策依据与历史变更如下：

**许可结论（不变）**：其桌面版 FFmpeg 构建为 `LGPL-2.1-or-later AND Zlib AND MIT`，
**不含 GPL 组件**（未链接 libx264/libx265），引入不会产生开源传染义务。
若未来引入，仍须在发行包中补充 FFmpeg 的 LGPL 义务履行说明（源码获取方式与重新链接指引）。

**原始阻塞项已消除**：此前记录的不引入原因是唯一提供 .NET 投影的 2.1.0 版本内部引用
Windows SDK 26100 投影程序集，与项目当时 Windows 10 1809 兼容基线所需的 19041 编译目标
冲突（CS1705）。**该前提现已不成立**——项目目标框架已升为 `net8.0-windows10.0.26100.0`
（`SupportedOSPlatformVersion` 仍保持 17763）。

**当前不引入的理由**：兼容障碍消失后，成本收益依然不成立——

1. 包体积代价过高：FFmpeg 编解码包实测约 **175.6 MB**，与本项目「轻量替代、xcopy 部署」的定位冲突；
2. 必要性不足：绝大多数用户视频为 H.264 / HEVC，系统解码器已覆盖；
3. 与 `MediaPlayerElement`、窗口化、Surface 的兼容性问题反而增多。

`Directory.Build.props` 中仍保留 `FFmpegInteropXVersion` 定义，但**无任何项目引用**，
不会进入发行包，也不产生 LGPL 义务。若后续确有特殊容器（如部分 MKV）需求，
优先建议**按需引导用户安装 Windows 应用商店解码器**，其次再评估引入。

---

## 本项目许可

**状态：尚未确定。** 仓库当前**不包含 `LICENSE` 文件**，本项目自身的源码许可尚未声明，
因此严格来说目前不可被他人合法再分发。

需在仓库根目录补充 `LICENSE` 后回填本节。选型时请注意与依赖的兼容性：

- 若选 **MIT / Apache-2.0**：与全部依赖无冲突，声明负担最小。
- 若选 **GPL-3.0**：与 `SixLabors.ImageSharp` 的双许可存在张力——
  其 Apache-2.0 分支对 GPL-3.0 项目是否可用存在争议（Apache-2.0 含专利终止条款，
  通常认为与 GPL-3.0 不兼容），届时须改走 ImageSharp 的商业许可，或替换为 BSD/MIT 许可的图像处理库。
