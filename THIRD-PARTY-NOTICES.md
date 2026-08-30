# 第三方组件许可声明（THIRD-PARTY NOTICES）

本文档列出 Pixbian 及其发行包中所含第三方组件的许可信息。依照各许可条款的要求，使用与分发本应用时须一并保留本声明。

**最近更新**：2026-08-30（M8）

---

## 发行包内组件

### Microsoft.WindowsAppSDK

| | |
|---|---|
| 版本 | 1.6.250108002 |
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

### WinUIEx

| | |
|---|---|
| 版本 | 2.3.0 |
| 许可 | MIT |
| 来源 | https://github.com/dotMorten/WinUIEx |
| 版权 | Copyright (c) Morten Nielsen |

### Microsoft.Data.Sqlite（含 SQLite 本体）

| | |
|---|---|
| 版本 | 8.0.11 |
| 许可 | MIT（SQLite 本体为 Public Domain） |
| 来源 | https://github.com/dotnet/aspnetcore / https://sqlite.org |

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
| Microsoft.NET.Test.Sdk | 17.11.1 | MIT |
| coverlet.collector | 6.0.2 | MIT |
| FFmpegInteropX（评估用） | 2.1.0.81200 | LGPL-2.1-or-later（未采用，见下） |

---

## 关于 FFmpegInteropX 的决策记录

本项目在 M4 里程碑**评估但最终未引入** FFmpegInteropX：

- 其桌面版 FFmpeg 构建为 `LGPL-2.1-or-later AND Zlib AND MIT`，**不含 GPL 组件**（未链接 libx264/libx265），引入不会产生开源传染义务；
- 未引入的原因是**兼容性冲突**：唯一提供 .NET 投影的 2.1.0 版本内部引用 Windows SDK 26100 投影程序集，与本项目维持 Windows 10 1809 兼容基线所需的 19041 编译目标冲突（CS1705）；
- 若未来引入，须在发行包中补充 FFmpeg 的 LGPL 义务履行说明（源码获取方式与重新链接指引），并复核包体积（实测 175.6 MB）。

---

## 本项目许可

Pixbian 本身的代码许可由项目所有者确定后，应在此处补充（如 MIT / GPL-3.0 / 专有）。
