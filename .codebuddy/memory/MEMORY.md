# 长期记忆

## 项目与开发环境
- Pixbian：WinUI 3 桌面**相册浏览器**（2026-08-31 明确定位：非编辑器）。2026-08-30 由 PhotoApps 更名而来。用户数据目录 `%LOCALAPPDATA%\Pixbian`，含旧目录一次性迁移。
- 技术栈基线：**Windows App SDK 2.4.0** + `net8.0-windows10.0.26100.0`；最低 OS 基线 17763（Win10 1809），不因用新 API 抬高基线。
- 本机 `dotnet` 不在 PATH，命令行须用 `C:\Program Files\dotnet\dotnet.exe`；仓库根 `run.ps1` / `run.cmd` 一键构建+启动。包管理用 `pnpm`。
- 工作区在 OneDrive：新产物落盘后立即启动 exe 可能被同步/杀软瞬时锁定；`dotnet run` 偶发无输出挂起。一键脚本用「显式 build + Start-Process」两段式绕开。
- 已启用 `ImplicitUsings`，**不要手动添加** `System`/`System.Linq` 等隐式 using。
- CI 用 `-warnaserror`，改动应力求 0 警告。缩进 4 空格、单引号（前端）、文件头需 3–8 行中文 JSDoc 模块说明。
- 测试基线：`dotnet test` 共 160 个（Core 115 / WebServer 33 / Imaging 12）。

## 项目分层与依赖方向（改动前必查）
- `Pixbian.Core`：最底层、**零项目引用**、TFM 为纯 `net8.0`。`Data` / `Imaging` / `Media` / `WebServer` 单向引用 Core，`Pixbian`（UI）引用全部。
- **Core 不能反向使用 Imaging 的服务**（如 `ImageMetadataReader`），也不能用 WIC / `Windows.Graphics.Imaging`（会把 Core 绑到 `net8.0-windows10.x`，破坏 `Core.Tests` 的 net8.0 基线）。需要时在 Core 定义抽象、由 UI 层注入实现。

## 编码与工具规范
- 含中文的 `.ps1` 必须存为 UTF-8 with BOM；通过终端传入的含中文命令也会语法错误，诊断命令一律写纯英文。终端读中文日志的 GBK 乱码不等于程序字符串有误。
- 禁止 TypeScript 双重断言（`as unknown as X`），用类型收窄/泛型修正解决。
- import 排序遵循 `import-x/order`（builtin → external → internal → sibling → index → type，组内升序、组间无空行）；**禁止 eslint-disable**，靠重构（useReducer / 纯异步函数）解决。
- 禁止使用已弃用 API；公共工具/类型放 `/shared`，优先复用既有 utils。
- 敏感信息：禁止硬编码密钥/凭证；API 响应须 DTO 白名单过滤，日志脱敏。
- 单次改动 > 5 个文件须先弹窗确认是否 `git add` + commit（Conventional Commits）；> 2 个文件的大改要先取得用户确认方案。
- 每次修改须记录到 `.codebuddy/memory/YYYY-MM-DD.md`（超 1000 行分卷 `YYYY-MM-DD_01.md`）。
- **MEMORY.md 只收规范与稳定事实**，不收代码定义、表结构、函数签名、具体数值、排障步骤；非用户要求不主动写入。

## 工具/验证手段的可靠结论
- `search_content` 的 `glob` **不支持 `!` 取反**（静默 0 结果），排除式搜索必须用无 glob 全量搜索复核。
- 判断路径是否被忽略只能用 `git check-ignore -v <path>`。
- **查 WinRT/WinUI API 是否存在，直接读 NuGet 包里的 winmd**（不要凭记忆或文档）：
  - WASDK 2.x 的 winmd 在 `<包>/metadata/`；SDK 投影 winmd 在 `microsoft.windows.sdk.net.ref/<ver>/winmd/`。
  - winmd 字符串堆是 ASCII/UTF-8，用 `[System.Text.Encoding]::ASCII.GetString([IO.File]::ReadAllBytes($w)).IndexOf("名")` 判定，-1 即不存在。
  - winmd 不能用 `Assembly.LoadFrom` 加载（.NET 8 报 0x80131515）。
  - 查控件模板/主题资源默认值：读 `<包>/lib/net6.0-windows10.0.18362.0/Microsoft.WinUI/Themes/generic.xaml`。
- **Microsoft Learn 的 WinRT 页会写错内容**（方法名、枚举值都可能与实际不符），以 winmd 或实测为准。
- 查 MSBuild 属性值：`dotnet msbuild xxx.csproj -getProperty:属性名`。

## WinUI 3 / WASDK 关键事实
- **2.x 的 `PkgMicrosoft_WindowsAppSDK` 已失效（空值）**，XAML 编译器路径必须用 `PkgMicrosoft_WindowsAppSDK_WinUI`；沿用旧属性会 MarkupCompilePass1 静默失败，症状是全项目爆 `CS0103`。
- dotnet（Core 宿主）下 **exe 模式 XAML 编译器是唯一可用路径**（net6.0 进程内 Task 在 .NET 8 SDK 下 `MSB4062` 加载失败）。
- TFM 升到 26100 不需要本机装 Windows SDK 26100（投影来自 `Microsoft.Windows.SDK.NET.Ref`）。改 TFM 后须同步 `run.ps1`、`README.md` 里的硬编码输出路径。
- `WinUIEx` 依赖 WASDK 1.8.x、与 2.x 不兼容，已移除；`WindowExtensions.SetIcon` 由官方 `AppWindow.SetIcon(string)` 1:1 替代。
- 2.0 起弃用的 `Window.Current`、`DependencyObject.Dispatcher`、`FocusManager.GetFocusedElement` 等：项目内 0 命中。
- **WinUI 3 无 `RenderOptions.BitmapInterpolationMode` API**。
- UWP 遗留 `Windows.Storage.Pickers` 在 unpackaged 下须手工关联网柄，已统一换成 `Microsoft.Windows.Storage.Pickers`（构造传 `WindowId`）。
- 取显示缩放比用 **`XamlRoot.RasterizationScale`**。
- `SoftwareBitmapSource` 属 `Windows.UI.Xaml`，**WinUI 3 桌面应用不可用**；要用 `WriteableBitmap` 承载像素。

## WinUI 3 平台约束（Pixbian 实战）
- 分组 ListViewBase 配自定义 ItemsPanel 时，面板排列的是 `GroupItem` 组容器；Justified 行式布局须用「ItemsControl 按组迭代 + 组内非分组 GridView」的组合结构。
- WinUI 至今无内置 Justified 布局与 GridLength 动画，`JustifiedPanel` / `GridLengthAnimation` 为必须的自实现。`InputNonClientPointerSource` Passthrough 自定义标题栏是官方推荐方案。
- ItemsPanelTemplate 内不能 x:Bind 页面属性、不能 ElementName 跨 namescope；模板内动态值须代码后置（Loaded + 视觉树查找）。
- 嵌套 ScrollViewer 内的 GridView 须 `VerticalScrollMode/BarVisibility="Disabled"`；多实例 GridView 的选择聚合须经实例列表（Loaded/Unloaded 登记）。
- 自定义面板解析子项数据读 `FrameworkElement.DataContext`（不是 `ContentControl.Content`）。
- **x:Bind 默认 OneTime**：绑定异步/会变的属性（缩略图、加载态）必须显式 `Mode=OneWay`。
- **`Border` 的 `CornerRadius` 不裁剪子内容**：圆角图片应交给 `Border.Background` 的 `ImageBrush`。
- 缩进/间距统一由面板 `Spacing` 承担，子项模板保持零 Margin，否则内容区宽高比偏离面板计算值。
- 计算属性（如 `AspectRatio`）须在依赖属性变更回调里手动 `OnPropertyChanged`。
- 系统缩略图 API 的 `PicturesView`/`VideosView` 返回**居中裁剪方形图**，取原图纵横比必须用 `ThumbnailMode.SingleItem`。

## 改控件外观的正确手法
- **优先覆盖主题资源，而非重写控件模板**：ThemeResource 沿视觉树向上查找、元素级优先，在元素 `Resources` 里定义同名键即可。键名规律 `Xxx`（Normal）/ `XxxPointerOver` / `XxxFocused` / `XxxDisabled`。
- 元素级 `Resources` 中用 `StaticResource` 引用主题字典资源可行。
- 代码后置直接改内部 `BorderElement` 等部件会被视觉状态动画覆盖回去。

## 自动化验证（本机实测结论）
- **模拟鼠标完全不可用**：`SetCursorPos`、`mouse_event`、`[Windows.Forms.Cursor]::Position` 均触发不了 `PointerEntered`/`ItemClick`。
- UIA `SelectionItemPattern.Select()` 只改选择状态，不触发 `ItemClick`/`Tapped`。
- 可靠替代：① `VisualStateManager.GoToState` 验证视觉状态；② 代码后置直接赋值 ViewModel 属性 + 属性通知处打日志；③ 运行时 dump 真实属性值（比截图精确）。
- WinUI 应用的 `$p.MainWindowHandle` 常为 0，取句柄用 UIA 的 `$win.Current.NativeWindowHandle`。
- WinUI 的 `TextBlock` / `Image` 不暴露 UIA 节点；界面验证须截屏（CopyFromScreen，截图前 `SetWindowPos(HWND_TOPMOST)` + 最大化）。
- 本机会话中 PowerShell 删除文件受 Safe-Delete 钩子拦截，复合命令中避免 `Remove-Item`。
- 顶层工作区文件夹无法在会话内重命名（IDE 持有句柄 + OneDrive FileCoAuth）。

## 图片显示与缩略图管线
- **WinUI 3 的 `Image`/`ImageBrush` 插值完全不可控且无 mipmap** → 最终那次缩放质量无法调，唯一手段是让位图物理像素尽量精确等于显示区物理像素。
- 排查顺序：① 显示尺寸算对没 → ② 物理像素/DPI → ③ 位图来源 → ④ 插值算法 → ⑤ 显示端插值（不可控）。
- 档位量化：**先乘 `RasterizationScale` 再 `SnapToBucket`**（顺序颠倒会让档位误差被缩放比成倍放大）。物理档位已隐含缩放比，缓存键天然区分，不需要额外「代次」失效机制。
- 缩小用 `Fant`、放大用 `Cubic`（Fant 放大偏软）；放大上限 2 倍，超过保留原图。WIC `BitmapInterpolationMode` **只有 4 个值**（NearestNeighbor/Linear/Cubic/Fant），无 HighQualityCubic。
- 升级加载必须**只升不降 + 容差（12%）**，否则解码舍入与布局抖动会引发反复重解码。
- 自定义面板只要在 `MeasureOverride` 里缩放过子项，就必须把实际分配尺寸回写（本项目 `IDisplaySizeAware`），由条目自行判断是否重新解码。
- 尺寸预取：图片取 `BitmapDecoder.OrientedPixel*`（与缩略图解码同源，宽高比必然一致，只读文件头）；视频取 `GetVideoPropertiesAsync()` 并**必须按 `VideoOrientation.Rotate90/270` 交换宽高**（注意 `VideoProperties.Width/Height` 是 `uint` 不是 `uint?`，写 `.Value` 报 CS1061）。
- **`BitmapImage` 绝不能同时设 `DecodePixelWidth` 与 `DecodePixelHeight`**：语义是"拉伸到该矩形"而非 Fit，必然变形；须先判方向只设一个维度。且其插值不可控（画质偏软）。
- 中转流编码：JPEG 无 alpha 用 `BmpEncoderId`（纯拷贝），其余用 `PngEncoderId`（保留 alpha）。无损，但耗内存与 CPU。
- **批量写回 UI 线程**而非逐条 `EnqueueAsync`。**绝不能写成 `EnqueueAsync(async () => await Xxx())` 而 Xxx 内部又 `EnqueueAsync` —— 会自我死锁**；需要 lambda 内产生的对象时在外部声明变量、lambda 内赋值、出队后再用。
- **产出 DependencyObject 的异步流水线要按线程亲和性切开**：中间产物一律用纯数据（如 `byte[]`），CPU 段（解码/重采样/编码）经信号量限流后放线程池，只在最后一跳回 UI 线程构造 `BitmapImage`。全程留在 UI 线程会让上百个 CPU 密集续体轮流抢占 UI 线程。
- **批量加载循环必须 `await Task.WhenAll` 而非 fire-and-forget**：`await` 在此不只是等结果，更是**背压**——不等待就无法阻止上下页任务叠加，条目数上千后延迟线性恶化（实测端到端可达 7 秒）。
- `DataReader.ReadBytes(byte[] buffer)` 填充调用方缓冲区、**不返回数组**；`byte[].AsBuffer()` 需 `System.Runtime.InteropServices.WindowsRuntime`，可改用对称的 `DataWriter`（`WriteBytes` → `StoreAsync` → **`DetachStream`**，不脱离流则 writer 释放时会连带关掉 stream）。
- 持有 `SemaphoreSlim` 等可释放字段的类型必须实现 `IDisposable`（CA1001，本项目 `-warnaserror` 会直接失败）。
- 异步加载链路治理要分层：**信号量限流只控并发数，不控"该不该做"**。虚拟化的 WinUI 列表（GridView/ItemsWrapGrid、内层 GroupGridView）由 `ContainerContentChanging` 接管按需加载，**不要**再在分页加载里"整页一次性提交所有未加载条目"——那会绕过虚拟化、让不可见条目占满信号量导致尾延迟雪崩。
- 判断虚拟化列表条目当前是否可见用 `ContainerFromItem(item) is null`（回收容器返回 null）这个标准探针，比算 ScrollViewer 偏移更稳、跨网格/Justified 布局通用。
- 滚走取消范式：滚动停止时对"所有 GridView 中 `ContainerFromItem` 均为 null 且仍在途"的条目调取消令牌；滚回时虚拟化容器回收再进入会重新触发按需加载，不丢图。
- WinUI 虚拟化列表的 `ContainerContentChanging` 绑定是**逐控件**的：每个要用按需加载的 GridView（含分组模板里的内层 GridView）都必须各自挂绑定，网格视图挂了不代表分组 GridView 也挂了。
- **异步加载链路里删除"整页 / 全量提交"兜底是高危险操作**：删除前必须先确认替代路径（虚拟化 ContainerContentChanging 等）在所有视图都真实生效，且要做对照组测量。本项目曾因"假设自适应视图 GroupGridView 已按需加载"而误删兜底，导致图片全不显示（THUMB=0）。
- 缩略图加载在本项目有两条路径：虚拟化 GridView 的 `ContainerContentChanging` 按需加载 + `GalleryViewModel.ExecuteLoadAsync` 整页兜底（首屏 / 改尺寸重解码），二者覆盖不对称，兜底不可轻易删。
- **色彩链路已验证正确，不要为此改动**：WIC 分支的 `ColorManageToSRgb` + `RespectExifOrientation` + `OrientedPixel*` 已正确处理宽色域与 EXIF 方向。

## 图片链路已知未做项（截至 2026-08-31）
- 扫描器 `MediaIndexingService.CreateItem` **不写宽高**（`MediaItem.Width/Height` 恒 null）→ 已用 `GetDimensionsAsync` 预取绕过，**不要在扫描期补写**（Core 无法引用 Imaging 的 EXIF 读取器、EXIF 对 PNG 覆盖率不足、首次扫描会从秒级掉到分钟级）。
- **磁盘缩略图缓存目录 `AppPaths.ThumbnailCacheDirectory` 已定义但零引用** → 冷启动每次全量重解码。
- **图片查看器完全绕过 `ThumbnailService`**：全分辨率 `BitmapImage.SetSourceAsync` + XAML 双线性缩放，首帧慢、内存高、大幅缩放走样。**这是当前最大的清晰度与体验洼地。**
- 内存缓存 `SizeLimit=2000` 是**条数**而非字节数，大档位位图可达 17MB/张，存在 OOM 隐患，也限制了「提高档位精度」的空间。
- 全链路依赖 WIC 编解码器覆盖，HEIC/AVIF 在未装商店扩展的机器上会解码失败 → 缩略图永久空白。

## 图片质量选型调研结论（2026-08-31）
- **Windows 照片应用不可作为对标**：微软自研闭源 C++ 管线（WIC 解码 + Direct2D 自绘 + 自研重采样器 + ONNX Runtime 做 AI），是 WIC/D2D 的作者，能力不可复制。它能给我们的启示是**架构方向**（自绘受控管线），不是"选不选第三方库"。
- **真正可对标的是第三方 Windows 看图应用**：ImageGlass(14.2k)、Playnite(13.9k)、NeeView、FlyPhotos、FileExplorer 均用 `PhotoSauce.MagicScaler`，且共同形态是「**保留 WIC 解码，只替换 WIC 最弱的重采样环节**」，再配 Magick.NET(格式兼容) + DirectN(渲染) + 格式插件。
- 库选型：`MagicScaler`(MIT/零依赖/线性光+自动锐化/WIC DCT 快速路径) > `Win2D`(GPU/`Anisotropic` mipmap，但 WASDK 2.4 兼容性未验证) > `ImageSharp`(**已在本项目**，但无 DCT 缩放、纯托管全解码，只适合离线与测试基线，不可进实时链路) > SkiaSharp/NetVips(体积大，不推荐)。
- **已决策**：因定位为相册浏览器，砍掉 MagicScaler、Win2D 降级为可选；优先做查看器两级加载 + 磁盘缩略图缓存。
- **该决策已有实测依据（2026-08-31 基线补充）**：缩略图过采样比值 Max 仅 1.245，即显示端只需缩放 13~24%，肉眼几乎不可辨，替换重采样器的收益极小。**基线测量的价值恰在于推翻预设**——动手前认定瓶颈是画质，实测反而暴露出未预料的秒级加载延迟。结论：没有数据不要做第三方库选型。
