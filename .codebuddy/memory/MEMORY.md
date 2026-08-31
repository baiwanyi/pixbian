# 长期记忆

> 只收规范、稳定事实与可复用方法论；不收代码定义、具体数值、一次性排障流水（那些进当日日志）。

## 项目与开发环境
- Pixbian：WinUI 3 桌面**相册浏览器**（非编辑器）。技术栈：**WASDK 2.4.0** + `net8.0-windows10.0.26100.0`，最低 OS 基线 17763。
- 本机 `dotnet` 不在 PATH，命令行须用 `C:\Program Files\dotnet\dotnet.exe`。包管理一律 `pnpm`。
- 已启用 `ImplicitUsings`，不要手动添加 `System`/`System.Linq` 等隐式 using。
- CI 用 `-warnaserror`，改动须 0 警告。缩进 4 空格、文件头 3–8 行中文 JSDoc 模块说明。
- 测试基线：`dotnet test` 共 160 个（Core 115 / WebServer 33 / Imaging 12）。
- 工作区在 OneDrive：新产物落盘后立即启动可能被同步/杀软瞬时锁定；一键脚本用「显式 build + Start-Process」两段式绕开。

## 分层与依赖方向（改动前必查）
- `Pixbian.Core`：最底层、**零项目引用**、TFM 纯 `net8.0`。`Data`/`Imaging`/`Media`/`WebServer` 单向引用 Core，`Pixbian`（UI）引用全部。
- **Core 不能反向使用 Imaging 的服务**，也不能用 WIC / `Windows.Graphics.Imaging`（会把 Core 绑到 `net8.0-windows10.x`，破坏 `Core.Tests` 基线）。需要时：Core 定义抽象 + UI 层注入实现。

## 编码与工具规范
- 含中文的 `.ps1` 必须存为 UTF-8 with BOM；**终端传入的含中文命令会语法错误**，诊断命令一律写纯英文。终端中文日志 GBK 乱码不等于程序字符串有误。
- 禁用 eslint-disable、禁用 TypeScript 双重断言（`as unknown as X`，改用类型收窄/泛型修正）；import 排序遵循 `import-x/order`。
- 禁用已弃用 API；公共工具/类型放 `/shared`，优先复用既有 utils。
- 敏感信息：禁止硬编码密钥/凭证；API 响应须 DTO 白名单过滤，日志脱敏。
- 单次改动 > 5 个文件须先弹窗确认是否 `git add` + commit（Conventional Commits）；> 2 个文件的大改要先取得用户确认方案。
- 每次修改须记录到 `.codebuddy/memory/YYYY-MM-DD.md`（超 1000 行分卷 `YYYY-MM-DD_01.md`）。
- 非用户要求不主动写入记忆；整理记忆时剔除代码定义与操作步骤。

## 工具/验证手段的可靠结论
- `search_content` 的 `glob` **不支持 `!` 取反**（静默 0 结果），排除式搜索必须用无 glob 全量搜索复核。
- 判断路径是否被忽略只能用 `git check-ignore -v <path>`。
- **查 WinRT/WinUI API 是否存在，直接读 NuGet 包里的 winmd**（不要凭记忆或文档）：
  - WASDK 2.x 在 `<包>/metadata/`；SDK 投影在 `microsoft.windows.sdk.net.ref/<ver>/winmd/`。
  - 字符串堆是 ASCII/UTF-8，用 `[Text.Encoding]::ASCII.GetString([IO.File]::ReadAllBytes($w)).IndexOf("名")` 判定，-1 即不存在。
  - winmd 不能用 `Assembly.LoadFrom` 加载（.NET 8 报 0x80131515）。
  - 查控件模板/主题资源默认值：读 `<包>/lib/net6.0-windows10.0.18362.0/Microsoft.WinUI/Themes/generic.xaml`。
- **Microsoft Learn 的 WinRT 页与中文图标表都会写错内容**（方法名、枚举值、码点映射），一律以 winmd / 官方源逐项 diff 为准；超长页面不要用 web_fetch 摘要（会截断）。
- 查 MSBuild 属性值：`dotnet msbuild xxx.csproj -getProperty:属性名`。

## WinUI 3 / WASDK 关键事实
- **2.x 的 `PkgMicrosoft_WindowsAppSDK` 已失效（空值）**，XAML 编译器路径必须用 `PkgMicrosoft_WindowsAppSDK_WinUI`；沿用旧属性会 MarkupCompilePass1 静默失败，症状是全项目爆 `CS0103`。
- dotnet（Core 宿主）下 **exe 模式 XAML 编译器是唯一可用路径**（net6.0 进程内 Task 在 .NET 8 SDK 下 `MSB4062`）。
- TFM 升到 26100 不需本机装 Windows SDK 26100（投影来自 `Microsoft.Windows.SDK.NET.Ref`）。改 TFM 后须同步 `run.ps1`、`README.md` 的硬编码输出路径。
- `WinUIEx` 依赖 WASDK 1.8.x，已移除；改用官方 `AppWindow.SetIcon(string)`。
- 2.0 起弃用的 `Window.Current`、`DependencyObject.Dispatcher`、`FocusManager.GetFocusedElement` 等：项目内 0 命中，不要浪费时间复查。
- **WinUI 3 无 `RenderOptions.BitmapInterpolationMode` API**；`SoftwareBitmapSource` 属 `Windows.UI.Xaml`，桌面应用不可用（用 `WriteableBitmap`）。
- UWP 遗留 `Windows.Storage.Pickers` 在 unpackaged 下须手工关联网柄，已统一换成 `Microsoft.Windows.Storage.Pickers`（构造传 `WindowId`）。
- 取显示缩放比用 **`XamlRoot.RasterizationScale`**（`DisplayInformation.GetForCurrentView()` 已弃用且会抛异常）。
- **C# 编译错误会连锁引发 XAML "Unknown type" 假错误**（无 LocalAssembly）；先修 CS 错误再查 XAML。
- **VS Code + WinUI 3 的 `.g.i.cs` 误报 CS0103 是固有限制**，以 `dotnet build` 为真相来源；不要动 `csproj` 的 `BaseIntermediateOutputPath`，不要删 `obj\`（被 Safe-Delete 钩子拦）。

## WinUI 3 平台约束（Pixbian 实战）
- 分组 ListViewBase 配自定义 ItemsPanel 时排列的是 `GroupItem` 组容器；Justified 行式布局须用「ItemsControl 按组迭代 + 组内非分组 GridView」。WinUI 至今无内置 Justified 布局与 GridLength 动画，自实现面板不可替换。`InputNonClientPointerSource` Passthrough 自定义标题栏是官方推荐方案。
- ItemsPanelTemplate 内不能 x:Bind 页面属性、不能 ElementName 跨 namescope；模板内动态值须代码后置（Loaded + 视觉树查找）。自定义面板解析子项数据读 `FrameworkElement.DataContext`（不是 `ContentControl.Content`）。
- **x:Bind 默认 OneTime**：绑定会变的属性（缩略图、加载态）必须显式 `Mode=OneWay`。
- **`Border` 的 `CornerRadius` 不裁剪子内容**：圆角图片交给 `Border.Background` 的 `ImageBrush`。
- 缩进/间距统一由面板 `Spacing` 承担，子项模板保持零 Margin。
- 嵌套 ScrollViewer 内的 GridView 须 `VerticalScrollMode/BarVisibility="Disabled"`；多实例 GridView 的选择聚合须经实例列表（Loaded/Unloaded 登记）。
- **改控件外观优先覆盖主题资源，而非重写控件模板**：ThemeResource 沿视觉树向上查找、元素级优先；键名规律 `Xxx` / `XxxPointerOver` / `XxxFocused` / `XxxDisabled`。代码后置改内部部件会被视觉状态动画覆盖回去。给 `MenuFlyoutItem` 自定义 ControlTemplate 会触发旋转忙碌光标，换行改用文本 `\n` + `MaxWidth`。
- **`MenuFlyout` 从 `Application.Current.Resources` 取出的是共享单例**，重复 `ShowAt` 会因旧 `XamlRoot` 残留抛 `E_INVALIDARG`；可重复弹出的菜单必须用工厂方法每次 `new`。

## 自动化验证（本机实测结论）
- **模拟鼠标完全不可用**：`SetCursorPos`、`mouse_event`、`[Windows.Forms.Cursor]::Position` 均触发不了 `PointerEntered`/`ItemClick`；UIA `SelectionItemPattern.Select()` 只改选择状态。
- 可靠替代：① `VisualStateManager.GoToState` 激活视觉状态；② 代码后置直接赋值 ViewModel 属性 + 属性通知处打日志；③ 运行时 dump 真实属性值（比截图精确）。
- WinUI 应用的 `$p.MainWindowHandle` 常为 0，取句柄用 UIA 的 `$win.Current.NativeWindowHandle`。WinUI 的 `TextBlock`/`Image` 不暴露 UIA 节点。
- 本机会话中 PowerShell 删除文件受 Safe-Delete 钩子拦截；顶层工作区文件夹无法在会话内重命名。

## 图片显示与缩略图管线（稳定结论）
- **WinUI 3 的 `Image`/`ImageBrush` 插值不可控且无 mipmap** → 显示端那次缩放无法调，唯一手段是让位图物理像素尽量精确等于显示区物理像素。
- 排查顺序：① 显示尺寸算对没（最长边 vs 高度）→ ② 物理像素/DPI → ③ 位图来源 → ④ 插值算法 → ⑤ 显示端插值（不可控）。
- 请求尺寸语义统一为「显示区最长边」；档位量化**先乘 `RasterizationScale` 再 `SnapToBucket`**（物理域量化，缓存键天然区分，不需要代次失效）。
- 缩小用 `Fant`、放大用 `Cubic`；放大上限 2 倍。`WIC BitmapInterpolationMode` 只有 4 个值，无 HighQualityCubic。
- 升级加载必须**只升不降 + 容差**，否则解码舍入与布局抖动会引发反复重解码。
- **`BitmapImage` 绝不能同时设 `DecodePixelWidth` 与 `DecodePixelHeight`**：语义是「拉伸到该矩形」而非 Fit，必然变形；只设最长边那一维。
- 自定义面板只要在 `MeasureOverride` 里缩放过子项，就必须把实际分配尺寸回写（本项目 `IDisplaySizeAware`）——「请求尺寸」与「显示尺寸」之间必须有回写通路。
- 尺寸预取：图片取 `BitmapDecoder.OrientedPixel*`（只读文件头，与缩略图解码同源）；视频取 `GetVideoPropertiesAsync()` 并按 `VideoOrientation.Rotate90/270` 交换宽高（`VideoProperties.Width/Height` 是 `uint` 不是 `uint?`）。
- **异步管线按线程亲和性切开**：中间产物用纯数据（`byte[]`），CPU 段（解码/重采样/编码）经信号量限流放线程池，只在最后一跳回 UI 线程构造 `BitmapImage`。
- **批量写回 UI 线程**；**绝不能 `EnqueueAsync(async () => await Xxx())` 而 Xxx 内部又 `EnqueueAsync`**（自我死锁）；需要 lambda 内产生的对象时在外部声明、lambda 内赋值、出队后用。
- **批量加载循环必须 `await Task.WhenAll`**：`await` 在此是**背压**，不等待就无法阻止上下页任务叠加。
- `DataReader.ReadBytes(byte[] buffer)` 填充调用方缓冲区、不返回数组；改用对称的 `DataWriter`（`WriteBytes` → `StoreAsync` → **`DetachStream`**）。
- 持有 `SemaphoreSlim` 等可释放字段的类型必须实现 `IDisposable`（CA1001，`-warnaserror` 会失败）。
- 异步加载链路治理分层：**信号量限流只控并发数，不控"该不该做"**。虚拟化列表由 `ContainerContentChanging` 按需加载；判断条目是否可见用 `ContainerFromItem(item) is null`。滚走取消 + 滚回重新触发。
- 该绑定是**逐控件**的：每个 GridView（含分组模板里的内层 GridView）都必须各自挂。
- **删除"整页提交"兜底是高危险操作**：必须先确认替代路径在所有视图真实生效并做对照组测量。本项目曾误删导致图片全不显示。两条加载路径覆盖不对称，兜底不可轻易删。
- **色彩链路已验证正确，不要为此改动**：`ColorManageToSRgb` + `RespectExifOrientation` + `OrientedPixel*`。
- **"分辨率"是绝对像素，"宽高比"是相对值，二者不可共用同一套取值优先级**：以缩略图位图像素为来源是错的（缩略图是按显示尺寸降采样的），只有宽高比可以容忍这种来源。

## 产品/技术决策（已定，勿反复）
- 定位相册浏览器 → 砍掉 MagicScaler，Win2D 降为可选；优先「查看器两级加载 + 磁盘缩略图缓存」。
- **基线测量优先于选型**：实测缩略图过采样比 Max 仅 1.245，画质分支据此砍掉，反而暴露秒级加载延迟。没有数据不要做第三方库选型。
- Windows 照片应用不可作为对标（微软自研闭源管线）；可对标的是 ImageGlass / FlyPhotos 等（共同形态：保留 WIC 解码，只替换重采样环节）。
- **扫描器不写 `MediaItem.Width/Height`**（Core 无法引用 Imaging 的 EXIF 读取器、EXIF 对 PNG 覆盖率不足、首次扫描会从秒级掉到分钟级）→ 尺寸一律走 `GetDimensionsAsync` 预取。

## 已知未做项
- 磁盘缩略图缓存目录 `AppPaths.ThumbnailCacheDirectory` 已定义但零引用 → 冷启动全量重解码。
- 图片查看器绕过 `ThumbnailService`（全分辨率 `SetSourceAsync` + XAML 双线性缩放）：首帧慢、内存高、大幅缩放走样，是当前最大洼地。
- 内存缓存 `SizeLimit=2000` 是条数而非字节数，存在 OOM 隐患。
- HEIC/AVIF 依赖 WIC 编解码器，未装商店扩展的机器会解码失败。
