# 长期记忆

## 项目与开发环境
- Pixbian：WinUI 3 桌面相册应用，2026-08-30 由 PhotoApps 更名而来（9 个子项目/命名空间/文档/脚本全同步）。用户数据目录 `%LOCALAPPDATA%\Pixbian`，含旧目录一次性迁移（`AppPaths.MigrateLegacyDirectory`）。
- 技术栈基线（用户明确约定）：**Windows App SDK 2.4.0** + `net8.0-windows10.0.26100.0`，运行框架 .NET 8；最低 OS 基线 17763（Win10 1809），不因用新 API 抬高基线。
- 本机 `dotnet` 不在 PATH，命令行须用 `C:\Program Files\dotnet\dotnet.exe`；仓库根提供 `run.ps1` / `run.cmd` 一键构建+启动。包管理用 `pnpm`（非 npm/yarn）。
- 工作区在 OneDrive：新产物落盘后立即启动 exe 可能被同步/杀软瞬时锁定；`dotnet run` 偶发无输出挂起（残留 build server + OneDrive 锁）。一键脚本用「显式 build + Start-Process」两段式绕开。
- 已启用 `ImplicitUsings`，**不要手动添加** `System`/`System.Linq`/`System.IO`/`System.Collections.Generic` 等隐式 using。
- CI 用 `-warnaserror`，改动应力求 0 警告。缩进 4 空格、单引号（前端）、文件头需 3–8 行中文 JSDoc 模块说明。
- 测试基线：`dotnet test` 共 160 个（Core 115 / WebServer 33 / Imaging 12）。

## 编码与工具规范
- 含中文的 `.ps1` 必须存为 UTF-8 with BOM（PS 5.1 按 GBK 解析无 BOM 文件会语法错误）；通过终端传入的含中文命令也会语法错误，诊断命令一律写纯英文。
- 终端读中文日志会出现 GBK 乱码，不等于程序字符串有误。
- 禁止 TypeScript 双重断言（`as unknown as X`），用类型收窄/泛型修正解决。
- import 排序遵循 `import-x/order`（builtin → external → internal → sibling → index → type，组内升序、组间无空行）；**禁止 eslint-disable**，靠重构（useReducer / 纯异步函数）解决。
- 禁止使用已弃用 API；公共工具/类型放 `/shared`，优先复用既有 utils。
- 敏感信息：禁止硬编码密钥/凭证，`appSettings.json` 同级的 `appSettings.Development.json` 仅本地；API 响应须 DTO 白名单过滤，日志脱敏。
- 单次改动 > 5 个文件须先弹窗确认是否 `git add` + commit（Conventional Commits）；> 2 个文件的大改要先取得用户确认方案。
- 每次修改须记录到 `.codebuddy/memory/YYYY-MM-DD.md`（超 1000 行分卷 `YYYY-MM-DD_01.md`）。
- **MEMORY.md 只收规范与稳定事实**，不收代码定义、表结构、函数签名、排障步骤；非用户要求不主动写入。

## 工具/验证手段的可靠结论
- `search_content` 的 `glob` **不支持 `!` 取反**（静默 0 结果，易误判），排除式搜索必须用无 glob 全量搜索复核。
- 判断路径是否被忽略只能用 `git check-ignore -v <path>`，不可文本猜 `.gitignore`。
- **查 WinRT/WinUI API 是否存在，直接读 NuGet 包里的 winmd**，不要凭记忆或文档：
  - WASDK 2.x 的 winmd 在 `<包>/metadata/`（1.x 的 `lib/uap10.0/` 已失效）。1.x 的 winmd 在 `lib/uap10.0/`。
  - winmd 字符串堆是 **ASCII/UTF-8**，用 `[System.Text.Encoding]::ASCII.GetString([IO.File]::ReadAllBytes($w)).IndexOf("名")`，-1 即不存在。
  - winmd 不能用 `Assembly.LoadFrom` 加载（.NET 8 报 0x80131515）。
  - 查控件模板/主题资源默认值：读 `<包>/lib/net6.0-windows10.0.18362.0/Microsoft.WinUI/Themes/generic.xaml`。
- **Microsoft Learn 的 WinRT 页会写错方法名**（如 `PickFolderResult.PickFolderAsync` 实为 `PickSingleFolderAsync`），以 winmd 为准。
- 查 MSBuild 属性值：`dotnet msbuild xxx.csproj -getProperty:属性名`。

## WinUI 3 / WASDK 关键事实
- **2.x 的 `PkgMicrosoft_WindowsAppSDK` 已失效（空值）**，XAML 编译器路径必须用 `PkgMicrosoft_WindowsAppSDK_WinUI`；沿用旧属性会 MarkupCompilePass1 静默失败，症状是全项目爆 `CS0103`（InitializeComponent 与 x:Name 全丢）。
- dotnet（Core 宿主）下 **exe 模式 XAML 编译器是唯一可用路径**（net6.0 进程内 Task 在 .NET 8 SDK 下 `MSB4062 System.Security.Permissions` 加载失败）。别被 targets 里的"no longer supported"误导。
- TFM 升到 26100 不需要本机装 Windows SDK 26100（投影来自 `Microsoft.Windows.SDK.NET.Ref`）。TFM 平台版本 ≠ `TargetPlatformMinVersion`。改 TFM 后须同步 `run.ps1`、`README.md` 里的硬编码输出路径。
- `WinUIEx`（2.9.3）依赖 WASDK 1.8.x、与 2.x 不兼容，已移除；`WindowExtensions.SetIcon` 由官方 `AppWindow.SetIcon(string)` 1:1 替代。
- 2.0 起弃用的 `Window.Current`、`DependencyObject.Dispatcher`、`FocusManager.GetFocusedElement`、`SystemBackdropHost`、`WrapPanel.HorizontalSpacing` 等：项目内 0 命中。
- **WinUI 3 无 `RenderOptions.BitmapInterpolationMode` API**（winmd 里不存在），`Image` 显示端插值不可控。
- UWP 遗留 `Windows.Storage.Pickers` 在 unpackaged 下须手工关联网柄，已统一换成 `Microsoft.Windows.Storage.Pickers`（构造传 `WindowId`）。
- 取显示缩放比用 **`XamlRoot.RasterizationScale`**，`DisplayInformation.GetForCurrentView()` 已弃用且常抛异常。

## WinUI 3 平台约束（Pixbian 实战）
- 分组 ListViewBase 配自定义 ItemsPanel 时，面板排列的是 `GroupItem` 组容器；Justified 行式布局须用「ItemsControl 按组迭代 + 组内非分组 GridView」的组合结构。
- WinUI 至今无内置 Justified 布局与 GridLength 动画，`JustifiedPanel` / `GridLengthAnimation` 为必须的自实现，不可替换。`InputNonClientPointerSource` Passthrough 自定义标题栏是官方推荐方案，保持原样。
- ItemsPanelTemplate 内不能 x:Bind 页面属性、不能 ElementName 跨 namescope；模板内动态值须代码后置（Loaded + 视觉树查找）设置。
- 嵌套 ScrollViewer 内的 GridView 须 `VerticalScrollMode/BarVisibility="Disabled"`；多实例 GridView 的选择聚合须经实例列表（Loaded/Unloaded 登记）。
- 自定义面板解析子项数据读 `FrameworkElement.DataContext`（不是 `ContentControl.Content`）。
- **x:Bind 默认 OneTime**：绑定异步/会变的属性（缩略图、加载态）必须显式 `Mode=OneWay`。
- `BoolToVisibilityConverter` 语义为「布尔按值、其余按非空」。
- 缩进/间距统一由面板 `Spacing` 承担，子项模板保持零 Margin，否则内容区宽高比偏离面板计算值。
- **`Border` 的 `CornerRadius` 不裁剪子内容**：内嵌 `Image` 时四角会露出直角尖角。圆角图片应交给 `Border.Background` 的 `ImageBrush`（背景绘制必定按圆角）；`x:Bind` 到 `ImageBrush.ImageSource` 可编译通过。
- 计算属性（如 `AspectRatio`）须在依赖属性变更回调里手动 `OnPropertyChanged`。
- 系统缩略图 API 的 `PicturesView`/`VideosView` 返回**居中裁剪方形图**，取原图纵横比必须用 `ThumbnailMode.SingleItem`。

## 改控件外观的正确手法
- **优先覆盖主题资源，而非重写控件模板**：ThemeResource 沿视觉树向上查找、元素级优先，在元素 `Resources` 里定义同名键即可。键名规律 `Xxx`（Normal）/ `XxxPointerOver` / `XxxFocused` / `XxxDisabled`。
- 例（AutoSuggestBox/TextBox 边框）：`<Thickness x:Key="TextControlBorderThemeThicknessFocused">1</Thickness>`（默认 `1,1,1,2` 会底部加粗）；`<StaticResource x:Key="TextControlBorderBrush" ResourceKey="ControlStrokeColorSecondaryBrush" />`。
- `TextControlBorderBrush` 默认是 `TextControlElevationBorderBrush` 竖向渐变（上淡下深，底部 #72000000），看起来像"黑底边"，属刻意设计。
- `AutoSuggestBox` 的 `Background/BorderBrush/BorderThickness/CornerRadius` 会通过 TemplateBinding 传给内部 TextBox，设在外层是有效的初始值；代码后置直接改内部 `BorderElement` 会被视觉状态动画覆盖回去。
- 元素级 `Resources` 中用 `StaticResource` 引用主题字典资源可行。

## 自动化验证（本机实测结论）
- **模拟鼠标完全不可用**：`SetCursorPos`、`mouse_event(MOUSEEVENTF_ABSOLUTE)`、`[Windows.Forms.Cursor]::Position` 均触发不了 `PointerEntered`/`ItemClick`。
- UIA `SelectionItemPattern.Select()` 只改选择状态，不触发 `ItemClick`/`Tapped`。
- 可靠替代：① 视觉状态 → `VisualStateManager.GoToState(控件, "PointerOver", true)`；② 选中联动 → 代码后置直接赋值 ViewModel 属性 + 属性通知处打日志；③ 验证控件外观 → 运行时 dump 真实属性值（比截图精确）。
- WinUI 应用的 `$p.MainWindowHandle` 常为 0，取句柄用 UIA 的 `$win.Current.NativeWindowHandle`。
- WinUI 的 `TextBlock` / `Image` 不暴露 UIA 节点，搜不到不代表未显示；界面验证须截屏（CopyFromScreen + 存 PNG，截图前 `SetWindowPos(HWND_TOPMOST)` + 最大化）。
- 本机会话中 PowerShell 删除文件受 Safe-Delete 钩子拦截，复合命令中避免 `Remove-Item`。
- 顶层工作区文件夹无法在会话内重命名（IDE 持有句柄 + OneDrive FileCoAuth），需用户关闭 IDE 后手动改。

## 缩略图质量优化要点（五轮迭代沉淀）
- 排查顺序：① 显示尺寸算对没 → ② 物理像素/DPI → ③ 位图来源（系统缓存 vs 原图） → ④ 插值算法 → ⑤ 显示端插值（不可控）。
- **请求尺寸与实际显示尺寸之间必须有回写通路**：自定义面板只要在 `MeasureOverride` 里对子项缩放过（如 JustifiedPanel 填满行宽的 `scale ∈ [0.5,1.5]`），名义尺寸必然失真。做法是在 `child.Measure` 后把实际分配尺寸回写给 DataContext（本项目 `IDisplaySizeAware`），由条目自行判断是否重新解码。
- **量化必须在物理域**（先乘 `RasterizationScale` 再 `SnapToBucket`）：顺序颠倒会让档位误差被缩放比成倍放大；物理档位已隐含缩放比，缓存键天然区分，不需要额外的「代次」失效机制。
- 缩小用 `Fant`、**放大用 `Cubic`**（Fant 放大偏软），`BitmapTransform` 默认 `Linear` 两头都不占优；放大上限 2 倍，超过不如保留原图。
- 升级加载必须**只升不降 + 容差**（本项目 12%），否则解码舍入与布局抖动会引发反复重解码。
- 竖图的最长边就是行高，首帧按正方形（ratio=1）请求已覆盖，**只有横图需要二次升级**（实测 200 项中仅 21 次升级）。
- `GetThumbnailAsync(mode, size)` 的 size 是**最长边**语义，等高布局的横图必须按「行高 × 宽高比」换算后再请求。
- **`BitmapImage` 绝不能同时设 `DecodePixelWidth` 与 `DecodePixelHeight`**：语义是"拉伸到该矩形"而非 Fit，必然变形；须先判方向只设一个维度。
- 高质量降采样用 `BitmapDecoder` + `BitmapTransform`：插值 `BitmapInterpolationMode.Fant`（> Cubic > Linear）、`RespectExifOrientation` 配合 `OrientedPixel*`、`ColorManageToSRgb`；`BitmapImage` 的插值不可控。
- 中转流编码：JPEG 无 alpha 用 `BmpEncoderId`（纯拷贝），其余用 `PngEncoderId`（保留 alpha）。
- 图片应直接解码原图降采样（系统缩略图缓存质量更差）；视频走 `GetThumbnailAsync(SingleItem, size, ResizeThumbnail)`。
- 「先模糊后清晰」的二次升级加载必须**只升不降**，否则解码舍入抖动会在档位边界反复重解码（配 `_inflightSize` 防重入）。
- 请求尺寸须量化到固定档位（`ThumbnailSizes.DecodeBuckets`）控制系统缓存与内存规模；缓存键加入缩放比代次，DPI 变化时整体失效。
