# 长期记忆

> 只收规范、稳定事实与可复用方法论；不收代码定义、具体数值、一次性排障流水（那些进当日日志）。

## 项目与开发环境
- Pixbian：WinUI 3 桌面**相册浏览器**（非编辑器）。WASDK 2.4.0 + `net8.0-windows10.0.26100.0`，最低 OS 基线 17763。
- 本机 `dotnet` 不在 PATH，须用 `C:\Program Files\dotnet\dotnet.exe`。包管理一律 `pnpm`。
- 已启用 `ImplicitUsings`，不要手动添加隐式 using。
- CI 用 `-warnaserror`，改动须 0 警告。缩进 4 空格、文件头 3–8 行中文 JSDoc 模块说明。
- 测试基线：`dotnet test` 共 165 个（Core 120 / WebServer 33 / Imaging 12）。
- 工作区在 OneDrive：新产物落盘后立即启动可能被同步/杀软瞬时锁定；一键脚本用「显式 build + Start-Process」两段式绕开。

## 分层与依赖方向（改动前必查）
- `Pixbian.Core`：最底层、**零项目引用**、TFM 纯 `net8.0`。`Data`/`Imaging`/`Media`/`WebServer` 单向引用 Core，`Pixbian`（UI）引用全部。
- **Core 不能反向使用 Imaging 的服务**，也不能用 WIC / `Windows.Graphics.Imaging`（会把 Core 绑到 `net8.0-windows10.x`，破坏 `Core.Tests` 基线）。需要时：Core 定义抽象 + UI 层注入实现。

## 编码与工具规范
- 含中文的 `.ps1` 必须存为 UTF-8 with BOM；**终端传入的含中文命令会语法错误**，诊断命令一律写纯英文。终端中文日志 GBK 乱码不等于程序字符串有误。
- 禁用 eslint-disable、禁用 TS 双重断言（改用类型收窄/泛型修正）；import 排序遵循 `import-x/order`。禁用已弃用 API。
- 敏感信息：禁止硬编码密钥/凭证；API 响应须 DTO 白名单过滤，日志脱敏。
- 单次改动 > 5 个文件须先弹窗确认是否 commit（Conventional Commits）；> 2 个文件的大改要先取得用户确认方案。
- 每次修改须记录到 `.codebuddy/memory/YYYY-MM-DD.md`（超 1000 行分卷 `YYYY-MM-DD_01.md`）。
- 非用户要求不主动写入记忆；整理记忆时剔除代码定义与操作步骤。

## 工具/验证手段的可靠结论
- `search_content` 的 `glob` **不支持 `!` 取反**（静默 0 结果）；判断路径是否被忽略只能用 `git check-ignore -v <path>`。
- **查 API 是否存在一律读包内二进制，不凭记忆或文档**：WinRT/SDK 投影在 `microsoft.windows.sdk.net.ref/<ver>/winmd/`；**WinUI 组件（`Microsoft.WindowsAppSDK.WinUI`）是托管程序集 `lib/net6.0-windows10.0.17763.0/Microsoft.WinUI.dll`，没有 winmd**——其同名 `.xml` 的成员索引（`T:`/`P:`/`M:` 前缀）比搜二进制更精确。winmd 不能用 `Assembly.LoadFrom`（.NET 8 报 0x80131515）。
- 控件模板/主题资源默认值读 `<包>/lib/net6.0-windows10.0.17763.0/Microsoft.WinUI/Themes/generic.xaml`（**注意是 17763 不是 18362**，两者都存在于包内，WinUI 组件只有 17763 这一份）。
- **Microsoft Learn 的 WinRT 页与中文图标表都会写错**（方法名、枚举值、码点），一律以包内二进制逐项 diff；超长页面不要用 web_fetch 摘要（会截断）。
- 查 MSBuild 属性值：`dotnet msbuild xxx.csproj -getProperty:属性名`。

## WinUI 3 / WASDK 关键事实
- **2.x 的 `PkgMicrosoft_WindowsAppSDK` 已失效（空值）**，XAML 编译器路径必须用 `PkgMicrosoft_WindowsAppSDK_WinUI`；沿用旧属性会 MarkupCompilePass1 静默失败，症状是全项目爆 `CS0103`。
- dotnet（Core 宿主）下 **exe 模式 XAML 编译器是唯一可用路径**（net6.0 进程内 Task 在 .NET 8 SDK 下 `MSB4062`）。
- TFM 升到 26100 不需本机装 Windows SDK 26100（投影来自 `Microsoft.Windows.SDK.NET.Ref`）。改 TFM 后须同步 `run.ps1`、`README.md` 的硬编码输出路径。
- **C# 编译错误会连锁引发 XAML "Unknown type" 假错误**；先修 CS 错误再查 XAML。
- **VS Code + WinUI 3 的 `.g.i.cs` 误报 CS0103 是固有限制**，以 `dotnet build` 为真相来源；不要动 `csproj` 的 `BaseIntermediateOutputPath`，不要删 `obj\`（被 Safe-Delete 钩子拦）。
- `WinUIEx` 已移除，改用官方 `AppWindow.SetIcon(string)`；UWP 遗留 `Windows.Storage.Pickers` 已换成 `Microsoft.Windows.Storage.Pickers`（构造传 `WindowId`）。
- **WinUI 3 无 `RenderOptions.BitmapInterpolationMode`**；`SoftwareBitmapSource` 属 `Windows.UI.Xaml` 不可用（用 `WriteableBitmap`）。取显示缩放比用 `XamlRoot.RasterizationScale`。2.0 起弃用的 `Window.Current` 等：项目内 0 命中，无需复查。
- **WASDK 2.3.6 无 `TransparentBackdrop`**：unpackaged 应用要 Windows 11 窗口圆角只能靠 `MicaBackdrop`（由 DWM 合成轮廓），材质可被不透明背景完全覆盖而不影响观感。
- `ThemeShadow` + `UIElement.Shadow` / `UIElement.Translation`(Vector3)：**z 分量是投影的唯一输入**，按物理模型（离背景越远→投影越大越淡）换算成模糊半径+偏移——`ThemeShadowIsUsingDropShadows`=True（generic.xaml 23270）表明底层即 Composition `DropShadow`。**z=0 时元素"贴"在背景上，投影几乎不可见**。z 档位有官方出处：generic.xaml 33807 的 `OuterOverflowContentRootV2`（CommandBarFlyout 溢出浮层）= `Translation="0,0,32"`，其 Shadow 在 33745 由 VisualState Setter 赋 `CommandBarFlyoutOverflowShadow`——本项目内容卡片沿用该 32。
- `Translation` 在合成层、**不参与布局**（不占 Margin、不触发重新布局、不影响兄弟排列），做"抬起"零代价，另有 `TranslationTransition` 可动画。**注意 `Translation="0,0,1"`（34839 ComboBox 背景）是另一种用法——z 排序把元素画到同层之上，与阴影无关**，勿混为一谈。
- **`Page.KeyboardAccelerators` 会污染页面内所有 ToolTip**（官方 by design）：声明后所有控件（除 MenuFlyoutItem）都在 ToolTip 显示按键组合，多个则只显示第一个；`KeyboardAcceleratorPlacementMode="Hidden"` **实测无效**。唯一可靠修法是不用 accelerator、改代码后置 KeyDown。菜单项用 `KeyboardAcceleratorTextOverride` 是豁免的正确用法。

## WinUI 3 平台约束（Pixbian 实战）
- 分组 ListViewBase 配自定义 ItemsPanel 时排列的是 `GroupItem` 组容器；Justified 行式布局须用「ItemsControl 按组迭代 + 组内非分组 GridView」。无内置 Justified 布局与 GridLength 动画。`InputNonClientPointerSource` Passthrough 自定义标题栏是官方推荐方案。
- ItemsPanelTemplate 内不能 x:Bind 页面属性、不能 ElementName 跨 namescope；自定义面板解析子项数据读 `FrameworkElement.DataContext`。
- **x:Bind 默认 OneTime**：会变的属性（缩略图、加载态）必须显式 `Mode=OneWay`。
- **`Border.CornerRadius` 会裁剪子内容（含投影）**：圆角图片交给 `Border.Background` 的 `ImageBrush`（WinUI 3 无 `Image.CornerRadius`、`RectangleGeometry` 无 RadiusX/Y、`Clip` 强类型 `RectangleGeometry`）。容器级圆角键同样会裁掉子元素阴影。
- 缩进/间距统一由面板 `Spacing` 承担，子项模板零 Margin。嵌套 ScrollViewer 内的 GridView 须禁用自身垂直滚动；多实例 GridView 的选择聚合须经实例列表（Loaded/Unloaded 登记）。
- **改控件外观优先覆盖主题资源，而非重写控件模板**：ThemeResource 沿视觉树向上查找、元素级优先；键名规律 `Xxx` / `XxxPointerOver` / `XxxFocused` / `XxxDisabled`。给 `MenuFlyoutItem` 自定义 ControlTemplate 会触发旋转忙碌光标，换行改用文本 `\n` + `MaxWidth`。
- **`MenuFlyout` 从 `Application.Current.Resources` 取出的是共享单例**，重复 `ShowAt` 会因旧 `XamlRoot` 残留抛 `E_INVALIDARG`；可重复弹出的菜单必须用工厂方法每次 `new`。
- 自定义控件的默认样式写在 `Controls/Xxx.xaml`，由 App.xaml 的 `MergedDictionaries` 以 `Source` 引入。
- **unpackaged 应用的 PRI 不索引 `<Content>` 项**，`ms-appx://` URI 解析不到 → 图片/图标一律按 `AppContext.BaseDirectory` 磁盘路径加载。默认 Content glob 不含 `.jpg`，须显式声明（与默认项重复会报 NETSDK1022）；输出时按相对路径落到 `Assets\` 子目录。

## 窗口视觉分层（背景图 + 玻璃卡片）
- 主窗口自下而上：全窗口 `Image` 背景（`Grid.RowSpan` 覆盖标题栏行+内容行，`UniformToFill` 居中裁切）→ 透明标题栏与透明 NavigationView → 内容区半透明卡片（`Border` + `ThemeShadow` + `Translation` z 抬高）。
- 让左栏/内容区透出背景：把 `NavigationView{Default,Expanded,Top}PaneBackground` 与 `NavigationViewContentBackground` 全部改 `Transparent`；NavigationView 自身的 `Background`（SplitView 底层）也要转透明。
- 承载卡片的容器级圆角必须归零（`NavigationViewContentGridCornerRadius`），否则会裁掉卡片投影的左上角；卡片圆角由卡片自己声明。
- 卡片「右/底贴窗口边缘」= `Margin="24,24,0,0"` + `CornerRadius="12,0,0,0"` + `BorderThickness="1,1,0,0"`（顺序：Margin/BorderThickness 为「左,上,右,下」，CornerRadius 为「左上,右上,右下,左下」）；贴边的两边给圆角或描边也会被裁掉，故直接归零。
- **`StaticResource` 无法解析 `ThemeDictionaries` 内的资源**（加载时一次性查找、不穿透主题字典）。业务侧要以 StaticResource 引用的画刷，必须定义在 App.xaml 的 `ResourceDictionary` 顶层、ThemeDictionaries **之外**；需要随主题变的才放主题字典并用 `{ThemeResource}`。
- **NavigationView 内容网格的纵向结构**（generic.xaml 17767 起，决定内容区顶部留白）：
  Row0 `ContentTopPadding`（Height=TemplateSettings.TopPadding，Visibility 绑 LeftPaneVisibility，补偿 ExtendsContentIntoTitleBar 的标题栏高度）
  → Row1 `HeaderContent`（`MinHeight=PaneToggleButtonHeight`=**36**，受 `HeaderGroup` 的 HeaderVisible/HeaderCollapsed 状态控制）
  → Row2 `ContentPresenter`（承载页面内容，`NavigationViewContentPresenterMargin`=0）。
  Column0 宽度恒为 0（`ContentLeftPadding` 无宽度），故 ContentPresenter 占满全宽、可贴左右边缘。
  → 背景透明后，Row0/Row1 的占位会露出背景图，内容区顶部间隙 = TopPadding + 36 + 卡片上边距。嫌大可用 `AlwaysShowHeader="False"` 消掉 Row1。

## 自动化验证（本机实测结论）
- **模拟鼠标完全不可用**（`SetCursorPos`/`mouse_event`/`Cursor.Position` 均触发不了 Pointer 事件；UIA `SelectionItemPattern.Select()` 只改选择状态）。
- 可靠替代：① `VisualStateManager.GoToState` 直接激活状态；② 代码后置赋值 ViewModel 属性 + 通知处打日志；③ 运行时 dump 真实属性（比截图精确）。
- WinUI 应用的 `$p.MainWindowHandle` 常为 0，取句柄用 UIA 的 `$win.Current.NativeWindowHandle`。WinUI 的 `TextBlock`/`Image` 不暴露 UIA 节点。
- 本机会话中 PowerShell 删除文件受 Safe-Delete 钩子拦截；顶层工作区文件夹无法在会话内重命名（子目录内文件可 `Rename-Item`）。

## 图片显示与缩略图管线（稳定结论）
- **WinUI 3 的 `Image`/`ImageBrush` 插值不可控且无 mipmap** → 显示端那次缩放无法调，唯一手段是让位图物理像素尽量精确等于显示区物理像素。
- 排查顺序：① 显示尺寸算对没 → ② 物理像素/DPI → ③ 位图来源 → ④ 插值算法 → ⑤ 显示端插值（不可控）。
- 请求尺寸语义统一为「显示区最长边」；档位量化**先乘 `RasterizationScale` 再 `SnapToBucket`**。缩小用 `Fant`、放大用 `Cubic`，放大上限 2 倍（`WIC BitmapInterpolationMode` 只有 4 个值，无 HighQualityCubic）。升级加载必须**只升不降 + 容差**。
- **`BitmapImage` 绝不能同时设 `DecodePixelWidth` 与 `DecodePixelHeight`**（语义是拉伸到该矩形），只设最长边那一维。
- 自定义面板缩放过子项就必须回写实际分配尺寸（`IDisplaySizeAware`）。尺寸预取：图片取 `BitmapDecoder.OrientedPixel*`；视频取 `GetVideoPropertiesAsync()` 并按 `Rotate90/270` 交换宽高。
- **异步管线按线程亲和性切开**：中间产物用 `byte[]`，CPU 段经信号量限流放线程池，只在最后一跳回 UI 线程构造 `BitmapImage`。**批量写回 UI 线程**；**绝不能 `EnqueueAsync(async () => await Xxx())` 而 Xxx 又 `EnqueueAsync`**（自我死锁）。**批量加载循环必须 `await Task.WhenAll`**（await 是背压）。
- `DataReader.ReadBytes(buffer)` 填充调用方缓冲区、不返回数组；写用对称的 `DataWriter`（`WriteBytes` → `StoreAsync` → **`DetachStream`**）。持有 `SemaphoreSlim` 等可释放字段的类型必须实现 `IDisposable`（CA1001 会让 `-warnaserror` 失败）。
- 异步加载治理：**信号量只控并发数，不控"该不该做"**。虚拟化列表由 `ContainerContentChanging` 按需加载；可见性判定用 `ContainerFromItem(item) is null`；滚走取消 + 滚回重新触发。该绑定是**逐控件**的。
- **删除"整页提交"兜底是高危险操作**：本项目曾误删导致图片全不显示，两条加载路径覆盖不对称，兜底不可轻易删。
- **色彩链路已验证正确，不要为此改动**：`ColorManageToSRgb` + `RespectExifOrientation` + `OrientedPixel*`。
- **"分辨率"是绝对像素，"宽高比"是相对值**，不可共用同一套取值优先级：宽高比可容忍以缩略图位图像素为来源，分辨率不行（缩略图是按显示尺寸降采样的）。

## 缩略图三态展示（骨架屏）— 方法论
- `ThumbnailPresenter` + `ThumbnailLoadState` 承载三态，`MediaItemViewModel.ThumbnailState` 是唯一数据源，控件只读。**取消 ≠ 失败**，必须回落 Loading；发起加载前必须先重置为 Loading，否则失败项永久停在 Failed。
- **WinUI 3 的 XAML 不支持 `EventTrigger`/`BeginStoryboard`**，动画只能由 VSM 或 `Storyboard.Begin()` 驱动 → 状态驱动动画应选「模板化控件 + VisualState」。`RepeatBehavior=Forever` 动画在容器回收后不会自停，须在 `Unloaded` 里 `GoToState` 到静态状态。
- **`{TemplateBinding}` 是一次性求值**：运行期会变的属性（异步图片）绝不能用，须在依赖属性回调里写入或用 `TemplatedParent` 绑定（ControlTemplate 内可用；DataTemplate 内 AncestorType 不可用）。把 DataTemplate 重构为 ControlTemplate 时绑定机制会静默改变，须逐个复核。
- **虚拟化容器回收复用不会重新应用模板**：凡在 `Unloaded` 改过的状态，都要在 `Loaded` 对称恢复；用实例字段记录"上一次 X"的，必须在 `DataContextChanged` 里清除。
- **VSM Setters 同步、Storyboard 值下一帧才接管**：混用须手动设动画起始值；`Storyboard.Stop()` 必须在 `GoToState` 之前；Setters 与 Transitions 混用会破坏渐变；VSM Storyboard 无法表达"仅跃迁时播放"，需要该语义就移出 VSM 由代码播。
- **「内容可绘制」与「数据已就绪」是两个时刻**：位图级 `ImageOpened` 只表示 CPU 解码完成；可靠信号是 Image **控件级** `ImageOpened`（透明探针 Image 拿信号，ImageBrush 保显示，共享同一 BitmapImage）。探针就绪后纹理可能晚一帧，淡入还需 `CompositionTarget.Rendering` 再等一帧。
- 骨架屏/占位层必须与内容实际渲染区域**同形**（Uniform 留白区也按同一算法居中），填满容器会留下逐渐淡出的残留边缘 = 观感抖动。
- **动画异常排查顺序**：① 内容就绪信号级别 → ② 状态被重置（反复重置 = 闪烁）→ ③ 跃迁判据（用**正向枚举**）→ ④ 第二次换源 → ⑤ GoToState 与内容就绪的帧间隔 → ⑥ 形状/位置 → ⑦ 播放时机 → ⑧ 时长/缓动。**先问"什么时候播"再问"怎么播"**。
- 两种到达顺序要分别修（x:Bind 多属性更新顺序不稳定）；「首次加载」需占位、「升级/重解码」应保持不动（加"回到加载中"副作用前先判现有内容 is null）。小元素动画需要更长时长：250ms 对缩略图实测不足（本项目定 500ms）；小尺寸占位块用 Opacity 呼吸而非渐变扫光。

## NuGet 漏洞审计（NU19xx）
- 语义：NU1900 = 获取漏洞数据失败（本轮没检查）；NU1901~1904 = 低/中/高/严重；NU1905 = 审计源无漏洞库。豁免 NU1900 对运行时零影响，但会**静默丧失安全可见性**。
- 本机 NuGet 6.11.2.1 / SDK 8.0.424：`NuGetAudit*` 系列可用，**`auditSources` 需 NuGet 6.12 / .NET 9 SDK，本机不支持**。
- **官方推荐「审计流水线分离」**：常规构建 `WarningsNotAsErrors` 全部 NU19xx，审计流水线 `-p:AuditPipeline=true` + `WarningsAsErrors` 才升级为错误。

## 产品/技术决策（已定，勿反复）
- 定位相册浏览器 → 砍掉 MagicScaler，Win2D 降为可选；优先「查看器两级加载 + 磁盘缩略图缓存」。
- **基线测量优先于选型**，没有数据不要做第三方库选型。Windows 照片应用不可作为对标（微软自研闭源管线）；可对标 ImageGlass / FlyPhotos。
- **扫描器不写 `MediaItem.Width/Height`**（Core 无法引用 Imaging 的 EXIF 读取器、EXIF 对 PNG 覆盖率不足、首次扫描会从秒级掉到分钟级）→ 尺寸一律走 `GetDimensionsAsync` 预取。

## 已知未做项
- 磁盘缩略图缓存目录 `AppPaths.ThumbnailCacheDirectory` 已定义但零引用 → 冷启动全量重解码。
- 图片查看器绕过 `ThumbnailService`（全分辨率 `SetSourceAsync` + XAML 双线性缩放）：首帧慢、内存高、大幅缩放走样，是当前最大洼地。
- 内存缓存 `SizeLimit=2000` 是条数而非字节数，存在 OOM 隐患。HEIC/AVIF 依赖 WIC 编解码器，未装商店扩展的机器会解码失败。
- 背景图固定 `light.jpg` 不随主题切换：系统深色主题下标题栏/左栏文字取浅色前景，压在浅色背景图上对比度不足（`dark.jpg` 已在 Assets 待用）。
