# 第三方组件与许可声明

本应用包含以下第三方组件。列出许可信息是为了满足相应许可证的归属与告知要求。

## FFmpegInteropX

- **用途**：把 FFmpeg 解码能力接入 Windows 的 `MediaPlayer` / `MediaPlayerElement`，用于视频播放。
- **版本**：2.1.0.81200
- **许可**：Apache License 2.0
- **项目主页**：<https://github.com/ffmpeginteropx/FFmpegInteropX>

## FFmpeg

- **用途**：视频与音频解码（含 AV1 的 dav1d 软解码、以及可用的 D3D11 硬件解码）。
- **提供方式**：随 `FFmpegInteropX.Desktop.FFmpeg` 包以动态链接库形式分发（`avcodec-62.dll`、
  `avformat-62.dll`、`avutil-60.dll`、`swscale-9.dll`、`swresample-6.dll`、`avfilter-11.dll`、
  `avdevice-62.dll`）。
- **许可**：GNU Lesser General Public License v2.1 或更高版本（LGPL-2.1-or-later），
  并包含部分以 Zlib 与 MIT 许可分发的组件。构建配置未启用 GPL 组件。
- **项目主页**：<https://ffmpeg.org/>

### LGPL 要求的履行方式

1. **署名**：应用「设置 → 关于」中标注了 FFmpeg 与 FFmpegInteropX 及其许可证名称。
2. **动态链接**：FFmpeg 以独立的动态链接库（DLL）形式随应用分发，未静态链接进可执行文件。
3. **可替换**：上述 DLL 位于应用安装目录，使用者可以自行替换为兼容版本的 FFmpeg 构建；
   替换后应用仍按原有方式调用 FFmpeg 的公开接口。
4. **源码获取**：FFmpeg 的完整对应源码可从 <https://ffmpeg.org/download.html> 获取，
   或向分发方索取；也可使用 <https://github.com/BtbN/FFmpeg-Builds> 的构建脚本自行构建。

## 其他

其余第三方依赖（WindowsAppSDK / WinUI、CommunityToolkit.Mvvm、Microsoft.Extensions.* 等）
均为 NuGet 包，许可信息可在各自的项目主页查看。
