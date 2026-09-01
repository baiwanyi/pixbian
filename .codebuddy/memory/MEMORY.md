# 长期记忆

> 只收规范、稳定事实与可复用方法论；不收代码定义、具体数值、一次性排障流水（那些进当日日志）。

## 项目与开发环境
- Pixbian：WinUI 3 桌面**相册浏览器**（非编辑器）。WASDK 2.4.0 + `net8.0-windows10.0.26100.0`，最低 OS 基线 17763。
- 本机 `dotnet` 不在 PATH，须用 `C:\Program Files\dotnet\dotnet.exe`。包管理一律 `pnpm`。
- 已启用 `ImplicitUsings`，不要手动添加隐式 using。
- CI 用 `-warnaserror`，改动须 0 警告。缩进 4 空格、文件头 3–8 行中文 JSDoc 模块说明。
- 测试基线：`dotnet test` 共 165 个（Core 120 / WebServer 33 / Imaging 12）。
- 工作区在 OneDrive：新产物落盘后立即启动可能被同步/杀软瞬时锁定；一键脚本用「显式 build + Start-Process」两段式绕开；构建前确认应用未在运行（exe 被进程持有报 MSB3026）。

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
- **查 API 是否存在一律读包内二进制，不凭记忆或文档**：WinRT/SDK 投影在 `microsoft.windows.sdk.net.ref/<ver>/winmd/`；**WinUI 组件是托管程序集 `lib/net6.0-windows10.0.17763.0/Microsoft.WinUI.dll`，没有 winmd**——其同名 `.xml` 的成员索引（`T:`/`P:`/`M:` 前缀）比搜二进制更精确。winmd 不能用 `Assembly.LoadFrom`（.NET 8 报 0x80131515）。
- 控件模板/主题资源默认值读 `<包>/lib/net6.0-windows10.0.17763.0/Microsoft.WinUI/Themes/generic.xaml`（只有 17763 这一份）。
- **Microsoft Learn 的 WinRT 页与中文图标表都会写错**（方法名、枚举值、码点），一律以包内二进制逐项 diff；超长页面不要用 web_fetch 摘要（会截断）。查 MSBuild 属性值：`dotnet msbuild xxx.csproj -getProperty:属性名`。

## WinUI 3 / WASDK 关键事实
- **2.x 的 `PkgMicrosoft_WindowsAppSDK` 已失效（空值）**，XAML 编译器路径必须用 `PkgMicrosoft_WindowsAppSDK_WinUI`；沿用旧属性会 MarkupCompilePass1 静默失败，症状是全项目爆 `CS0103`。
- dotnet（Core 宿主）下 **exe 模式 XAML 编译器是唯一可用路径**（net6.0 进程内 Task 在 .NET 8 SDK 下 `MSB4062`）。TFM 升到 26100 不需本机装 SDK 26100。
- **C# 编译错误会连锁引发 XAML "Unknown type" 假错误**；先修 CS 错误再查 XAML。
- **VS Code + WinUI 3 的 `.g.i.cs` 误报 CS0103 是固有限制**，以 `dotnet build` 为真相来源；不要动 `csproj` 的 `BaseIntermediateOutputPath`，不要删 `obj\`（被 Safe-Delete 钩子拦）。
- **XAML 编译期不校验颜色字面量**：`##RRGGBB` 能 0 警告通过构建、运行时才崩（stowed exception `0xC000027B`，托管堆栈不在 `.NET Runtime` 日志）。「构建成功 + 启动崩溃」先 `git status` 全量排查改动文件，查 Windows 应用日志定位出错模块。
- `WinUIEx` 已移除，改用官方 `AppWindow.SetIcon(string)`；`Windows.Storage.Pickers` 已换 `Microsoft.Windows.Storage.Pickers`（构造传 `WindowId`）。
- WinUI 3 无 `RenderOptions.BitmapInterpolationMode`；`SoftwareBitmapSource` 不可用（用 `WriteableBitmap`）；显示缩放比用 `XamlRoot.RasterizationScale`。
- **unpackaged 应用要 Win11 窗口圆角只能靠 `MicaBackdrop`**（DWM 合成轮廓；WASDK 2.3.6 无 `TransparentBackdrop`）；材质可被不透明背景完全覆盖而不影响圆角。Mica 仅 Win11 生效。
- **`Page.KeyboardAccelerators` 会污染页面内所有 ToolTip**（官方 by design，`KeyboardAcceleratorPlacementMode="Hidden"` 实测无效）。唯一可靠修法：不用 accelerator、改代码后置 KeyDown。菜单项 `KeyboardAcceleratorTextOverride` 是豁免的正确用法。
- `ThemeShadow` + `Translation`(Vector3)：**z 是投影唯一输入**，z=0 几乎不可见；官方 z 档位出处 generic.xaml 33807 = `32`。`Translation` 在合成层、**不参与布局**；`Translation="0,0,1"` 是 z 排序用法、与阴影无关。
- **持有 `SemaphoreSlim`/`CancellationTokenSource` 等可释放字段的类型必须实现 `IDisposable`**（CA1001 在 `-warnaserror` 下是错误）。

## WinUI 3 平台约束（Pixbian 实战）
- 分组 ListViewBase 配自定义 ItemsPanel 时排列的是 `GroupItem` 组容器；无内置 Justified 布局与 GridLength 动画。`InputNonClientPointerSource` Passthrough 自定义标题栏是官方推荐方案。
- ItemsPanelTemplate 内不能 x:Bind 页面属性、不能 ElementName 跨 namescope；自定义面板解析子项数据读 `FrameworkElement.DataContext`。
- **x:Bind 默认 OneTime**：会变的属性必须显式 `Mode=OneWay`。
- **ItemContainerStyle 的 ControlTemplate 内 x:Bind 根是模板化控件**（SelectorItem，`IsSelected` 即容器选中、与 GridView 选择机制同步）；页面属性须经 PageProxy（`Data="{x:Bind}"` 放 Page.Resources）用传统 `Binding` 访问。**模板内 x:Bind 禁配 StaticResource Converter**：生成的 `LookupConverter` 运行时 NRE，编译期 0 警告、启动后容器生成时延迟崩溃（stowed exception `0xC000027B`，约数秒到数十秒）——模板内条件显隐一律用 VisualState 状态机（如 SelectionStates 四态：Selected/SelectedUnfocused/SelectedPointerOver/SelectedPressed）。模板内事件挂接可靠性未验证，交互联动放页面级事件（如 OnSelectionChanged）收口。
- **VisualState Setter 优先级高于本地绑定值**：基础值走绑定、hover 态用 Setter 覆盖，可实现「选择模式常显 + 普通模式 hover 浮现」这类条件显示，纯 XAML 表达。
- **`Border.CornerRadius` 会裁剪子内容（含投影）**：圆角图片交给 `Border.Background` 的 `ImageBrush`（WinUI 3 无 `Image.CornerRadius`、`RectangleGeometry` 无 RadiusX/Y）。容器级圆角键同样裁掉子元素阴影。
- 缩进/间距统一由面板 `Spacing` 承担，子项模板零 Margin。嵌套 ScrollViewer 内的 GridView 须禁用自身垂直滚动；多实例 GridView 的选择聚合须经实例列表（Loaded/Unloaded 登记）。
- **改控件外观优先覆盖主题资源，而非重写控件模板**；键名规律 `Xxx` / `XxxPointerOver` / `XxxFocused` / `XxxDisabled`。给 `MenuFlyoutItem` 自定义 ControlTemplate 会触发旋转忙碌光标。
- **`MenuFlyout` 从 `Application.Current.Resources` 取出的是共享单例**，重复 `ShowAt` 抛 `E_INVALIDARG`；可重复弹出的菜单必须工厂方法每次 `new`。
- **unpackaged 应用的 PRI 不索引 `<Content>` 项**，`ms-appx://` 解析不到 → 资源一律按 `AppContext.BaseDirectory` 磁盘路径加载；默认 Content glob 不含 `.jpg`，须显式声明（重复声明报 NETSDK1022）。
- **`StaticResource` 无法解析 `ThemeDictionaries` 内的资源**；业务侧 StaticResource 引用的画刷须定义在 App.xaml `ResourceDictionary` 顶层、主题字典之外。
- **Button hover 底色看不见通常是默认键太淡**，用 Page.Resources 的 `<StaticResource x:Key="ButtonBackgroundPointerOver" ResourceKey="SubtleFillColorSecondaryBrush"/>` 覆盖即可；主题键覆盖不要放 `Style.Resources`（共享字典异常）。圆角两档：4（控件）/8（表面），不得发明第三档。
- **`CommandBar` 动态溢出有未修 bug（微软 issue #6450 not planned）**：触发溢出后 Flyout 永久异常，带 Flyout 按钮的工具栏溢出只能手动实现（AdaptiveTrigger + VisualState 断点，「更多」菜单每次 Opening 重建补齐被收起命令）。
- 菜单改「数据定义 + 工厂方法 + 每次 Opening 重建」可消掉一整类勾选同步代码；`MenuFlyoutSeparator` 只继承 `MenuFlyoutItemBase`。
- **切换 ListViewBase 的 `SelectionMode` 会重置选择**：需要保留选择的模式切换须先抓 `SelectedItems` 快照、切换归零后再恢复。

## 窗口视觉分层（背景图 + 玻璃卡片）
- 主窗口自下而上：全窗口 `Image` 背景（`Grid.RowSpan` 覆盖标题栏行+内容行，`UniformToFill` 居中裁切）→ 透明标题栏与透明 NavigationView → 内容区半透明卡片（`Border` + `ThemeShadow` + `Translation` z 抬高）。
- 让左栏/内容区透出背景：`NavigationView{Default,Expanded,Top}PaneBackground` 与 `NavigationViewContentBackground` 全改 `Transparent`；NavigationView 自身 `Background`（SplitView 底层）也要转透明。**Pane 展开形态下 Pane 背景走 `NavigationViewExpandedPaneBackground`（默认透明）**，改 `NavigationViewDefaultPaneBackground` 无效；死键：`NavigationViewPaneBackground`/`NavigationViewContentBackground`/`NavigationViewBackground`。
- 承载卡片的容器级圆角必须归零（`NavigationViewContentGridCornerRadius`），否则裁掉卡片投影；卡片圆角由卡片自己声明（`8,0,0,0` 是默认值，浅色主题下须在 App.xaml 显式声明）。
- 卡片「右/底贴窗口边缘」= `Margin="24,24,0,0"` + `CornerRadius="12,0,0,0"` + `BorderThickness="1,1,0,0"`；贴边的两边直接归零。
- **Pane 与内容区之间的竖线来自 `ContentGrid` 的 `BorderThickness` 键（默认 1,1,0,0）的 Left=1**，不是 Pane 自身 Border；覆盖 Thickness 归零即可，勿动 `NavigationViewItemSeparatorForeground`（被别处复用）。左栏圆角来自模板 `RightCornerRadiusFilterConverter`（只留右侧），元素级覆盖 `OverlayCornerRadius` 无效（Binding 绕过元素级资源）。
- **内容区紧贴左栏时，「内容区左上圆角」与「左栏右上深色圆弧」是同一几何事实**，无法同时消除；要兼得只能留缝。
- NavigationView 内容网格纵向：Row0 `ContentTopPadding`（补偿标题栏）→ Row1 `HeaderContent`（MinHeight=36，可 `AlwaysShowHeader="False"` 消掉）→ Row2 `ContentPresenter`（占满全宽）。背景透明后顶部间隙 = TopPadding + 36 + 卡片上边距。

## 自动化验证（本机实测结论）
- **模拟鼠标完全不可用**（`SetCursorPos`/`mouse_event` 均触发不了 Pointer 事件；UIA `SelectionItemPattern.Select()` 只改选择状态）。
- 可靠替代：① `VisualStateManager.GoToState` 直接激活状态；② 代码后置赋值 ViewModel 属性 + 通知处打日志；③ 运行时 dump 真实属性（比截图精确）。
- 截图采样：须 `SetProcessDPIAware` + `SetForegroundWindow`（还须先 `ShowWindow(SW_RESTORE)`）+ 校验前台窗口句柄与主色健全性，否则采到别的窗口。
- WinUI 应用的 `$p.MainWindowHandle` 常为 0，取句柄用 UIA 的 `$win.Current.NativeWindowHandle`。WinUI 的 `TextBlock`/`Image` 不暴露 UIA 节点。
- 本机会话中 PowerShell 删除文件受 Safe-Delete 钩子拦截；顶层工作区文件夹无法在会话内重命名。

## 图片显示与缩略图管线（稳定结论）
- **WinUI 3 的 `Image`/`ImageBrush` 插值不可控且无 mipmap** → 唯一手段是让位图物理像素尽量精确等于显示区物理像素。排查顺序：显示尺寸 → DPI → 位图来源 → 插值算法 → 显示端插值（不可控）。
- 请求尺寸语义统一为「显示区最长边」；档位量化**先乘 `RasterizationScale` 再量化**。缩小 `Fant`、放大 `Cubic`（上限 2 倍）。升级加载**只升不降 + 容差**。
- **`BitmapImage` 绝不能同时设 `DecodePixelWidth` 与 `DecodePixelHeight`**（语义是拉伸），只设最长边一维。
- 自定义面板缩放过子项就必须回写实际分配尺寸（`IDisplaySizeAware`）。尺寸预取：图片取 `BitmapDecoder.OrientedPixel*`；视频取 `GetVideoPropertiesAsync()` 并按旋转交换宽高。
- **异步管线按线程亲和性切开**：中间产物用 `byte[]`，CPU 段限流放线程池，只在最后一跳回 UI 线程构造 `BitmapImage`。**批量写回 UI 线程**；**绝不能 `EnqueueAsync(async () => await Xxx())` 而 Xxx 又 `EnqueueAsync`**（自我死锁）。**批量加载循环必须 `await Task.WhenAll`**（await 是背压）。UI 状态赋值统一放进 `EnqueueAsync` 块（其延续落在线程池线程）。
- `DataReader.ReadBytes` 填充调用方缓冲区；写用 `DataWriter`（`WriteBytes` → `StoreAsync` → **`DetachStream`**）。
- 异步加载治理：**信号量只控并发数，不控"该不该做"**。可见性判定用 `ContainerFromItem(item) is null`；滚走取消 + 滚回重触发。**删除"整页提交"兜底是高危操作**（曾误删致图片全不显示）。
- **"分辨率"是绝对像素、"宽高比"是相对值，取值优先级不可共用**（分辨率忌用缩略图位图像素）。色彩链路（`ColorManageToSRgb` + `RespectExifOrientation` + `OrientedPixel*`）已验证正确，勿改。

## 骨架屏三态展示 — 方法论
- `ThumbnailPresenter` + `ThumbnailLoadState` 三态，`ThumbnailState` 是唯一数据源。**取消 ≠ 失败**，必须回落 Loading。
- WinUI 3 XAML 无 `EventTrigger`/`BeginStoryboard` → 状态驱动动画选「模板化控件 + VisualState」；`RepeatBehavior=Forever` 动画容器回收后不自停，`Unloaded` 里回静态态。
- **`{TemplateBinding}` 是一次性求值**，运行期会变的属性须依赖属性回调写入；DataTemplate 重构为 ControlTemplate 时绑定机制静默改变，须逐个复核。
- **虚拟化容器回收复用不会重新应用模板**：`Unloaded` 改过的状态要在 `Loaded` 对称恢复；"上一次 X" 字段须在 `DataContextChanged` 清除。
- **「内容可绘制」与「数据已就绪」是两个时刻**：可靠信号是 Image **控件级** `ImageOpened`（透明探针 Image 拿信号 + ImageBrush 显示）；探针就绪后纹理可能晚一帧，淡入再等 `CompositionTarget.Rendering` 一帧。
- 动画异常排查顺序：① 就绪信号级别 → ② 状态被重置 → ③ 跃迁判据（**正向枚举**）→ ④ 二次换源 → ⑤ 帧间隔 → ⑥ 形状/位置 → ⑦ 播放时机 → ⑧ 时长/缓动。**先问"什么时候播"再问"怎么播"**。小元素动画 250ms 实测不足（本项目定 500ms）。

## NuGet 漏洞审计（NU19xx）
- 常规构建对 NU19xx 用 `WarningsNotAsErrors` 豁免，审计流水线 `-p:AuditPipeline=true` 才升级为错误（官方推荐分离）；`auditSources` 需 NuGet 6.12 / .NET 9 SDK，本机（6.11 / SDK 8.0.424）不支持。

## 产品/技术决策（已定，勿反复）
- 定位相册浏览器 → 砍掉 MagicScaler，Win2D 降为可选；优先「查看器两级加载 + 磁盘缩略图缓存」。基线测量优先于选型；Windows 照片应用不可对标（闭源管线），可对标 ImageGlass / FlyPhotos。
- **扫描器不写 `MediaItem.Width/Height`**（EXIF 覆盖率与扫描耗时）→ 尺寸一律走 `GetDimensionsAsync` 预取。
- 排序为 `MediaSortKey` × `SortDirection` 两维；随机排序用 `RandomSeed` 取模保证同种子分页稳定，不能用 SQL `RANDOM()`。
- 删除文件走回收站（`RecycleBinHelper`，SHFileOperation + FOF_ALLOWUNDO）并同步清索引；「从索引移除」仅删记录。

## 已知未做项
- 磁盘缩略图缓存目录已定义但零引用 → 冷启动全量重解码。
- 图片查看器绕过 `ThumbnailService`（全分辨率加载）：首帧慢、内存高，是当前最大洼地。
- 内存缓存 `SizeLimit=2000` 是条数而非字节数，存在 OOM 隐患。HEIC/AVIF 依赖 WIC 编解码器扩展。
- 背景图固定 `light.jpg` 不随主题切换：深色主题下文字对比度不足（`dark.jpg` 已在 Assets 待用）。
- 历史遗留 `Diagnostics.Log`：`DBLTAP`（GalleryPage）、`VIEWER`（ImageViewerViewModel）、`THUMB` 统计（ThumbnailService）。
