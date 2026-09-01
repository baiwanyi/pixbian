# 长期记忆

> 只收规范、稳定事实与可复用方法论；不收代码定义、具体数值、一次性排障流水（那些进当日日志）。

## 项目与开发环境
- Pixbian：WinUI 3 桌面**相册浏览器**（非编辑器）。WASDK 2.4.0 + `net8.0-windows10.0.26100.0`，最低基线 17763。
- 本机 `dotnet` 不在 PATH，须用 `C:\Program Files\dotnet\dotnet.exe`；包管理一律 `pnpm`。
- 已启用 `ImplicitUsings`；CI 用 `-warnaserror`（改动须 0 警告）；缩进 4 空格；文件头 3–8 行中文 JSDoc 模块说明。
- 测试基线：`dotnet test` 共 166 个（Core 121 / WebServer 33 / Imaging 12）。「发现」功能已全链路删除。
- 工作区在 OneDrive：新产物落盘后立即启动可能被同步/杀软瞬时锁定；一键脚本用「显式 build + Start-Process」两段式绕开；构建前确认应用未在运行（exe 被持有报 MSB3026）。

## 分层与依赖方向（改动前必查）
- `Pixbian.Core` 最底层、**零项目引用**、TFM 纯 `net8.0`；`Data`/`Imaging`/`Media`/`WebServer` 单向引用 Core，`Pixbian`(UI) 引用全部。
- **Core 不得使用 WIC / `Windows.Graphics.Imaging`**（会把 Core 绑到 windows TFM，破坏 Core.Tests 基线）。需要时：Core 定抽象 + UI 层注入实现。

## 编码与工具规范
- 含中文的 `.ps1` 必须 UTF-8 with BOM；**终端传入含中文的命令会语法错误**，诊断命令一律纯英文；终端中文 GBK 乱码不等于程序字符串有误。
- 禁用 eslint-disable、禁用 TS 双重断言（改类型收窄/泛型修正）；import 走 `import-x/order`；禁用已弃用 API。
- 敏感信息禁止硬编码；API 响应须 DTO 白名单过滤；日志脱敏。
- 单次改动 > 5 文件须先弹窗确认是否 commit（Conventional Commits）；> 2 文件的大改要先取得用户确认方案。
- 每次修改须记 `.codebuddy/memory/YYYY-MM-DD.md`（超 1000 行分卷 `YYYY-MM-DD_01.md`）。非用户要求不主动写记忆；整理时剔除代码定义与操作步骤。

## 工具/验证手段（可靠结论）
- `search_content` 的 `glob` **不支持 `!` 取反**（静默 0 结果）；判断忽略只能用 `git check-ignore -v <path>`。
- **查 API 是否存在一律读包内二进制，不凭记忆/文档**：WinRT 投影在 `microsoft.windows.sdk.net.ref/<ver>/winmd/`；**WinUI 组件是托管程序集 `lib/net6.0-windows10.0.17763.0/Microsoft.WinUI.dll`（无 winmd）**，其同名 `.xml` 成员索引（`T:`/`P:`/`M:` 前缀）比搜二进制更精确。winmd 不能 `Assembly.LoadFrom`（.NET 8 报 0x80131515）。
- 控件模板/主题资源默认值读 `<包>/.../Microsoft.WinUI/Themes/generic.xaml`（只有 17763 这一份）。
- **Microsoft Learn 的 WinRT 页与中文图标表都会写错**（方法名、枚举值、码点），须以包内二进制逐项 diff；超长页面勿用 web_fetch（会截断）。查 MSBuild 属性：`dotnet msbuild xxx.csproj -getProperty:属性名`。

## WinUI 3 / WASDK 关键事实
- **2.x 的 `PkgMicrosoft_WindowsAppSDK` 已失效**，XAML 编译器路径须用 `PkgMicrosoft_WindowsAppSDK_WinUI`；沿用旧属性会 MarkupCompilePass1 静默失败，症状是全项目爆 `CS0103`。
- dotnet 宿主下 **exe 模式 XAML 编译器是唯一可用路径**（进程内 Task 在 .NET 8 SDK 下 `MSB4062`）。TFM 升 26100 不需装 SDK 26100。
- **C# 错误会连锁引发 XAML "Unknown type" 假错误**，先修 CS 再查 XAML。VS Code 的 `.g.i.cs` 误报 CS0103 是固有限制，**以 `dotnet build` 为准**；勿动 `BaseIntermediateOutputPath`、勿删 `obj\`。
- **XAML 编译期不校验颜色字面量**：`##RRGGBB` 能 0 警告构建、运行时才崩（stowed exception `0xC000027B`）。「构建成功 + 启动崩溃」先 `git status` 全量排查，再查 Windows 应用日志。
- `WinUIEx` 已移除，用 `AppWindow.SetIcon(string)`；Picker 换 `Microsoft.Windows.Storage.Pickers`（构造传 `WindowId`）。
- WinUI 3 无 `RenderOptions.BitmapInterpolationMode`；`SoftwareBitmapSource` 不可用（用 `WriteableBitmap`）；缩放比取 `XamlRoot.RasterizationScale`。
- **unpackaged 应用要 Win11 圆角只能靠 `MicaBackdrop`**（WASDK 2.3.6 无 `TransparentBackdrop`）；材质可被不透明背景覆盖而不影响圆角；Mica 仅 Win11 生效。
- **`Page.KeyboardAccelerators` 会污染页面内所有 ToolTip**（官方 by design，`PlacementMode="Hidden"` 实测无效）→ 只能用代码后置 KeyDown；菜单项 `KeyboardAcceleratorTextOverride` 是豁免用法。
- `ThemeShadow` + `Translation`：**z 是投影唯一输入**，z=0 几乎不可见（官方档位出处 generic.xaml 33807 = `32`）；`Translation` 在合成层、不参与布局。
- **持有 `SemaphoreSlim`/`CTS` 等可释放字段的类型必须实现 `IDisposable`**（CA1001 在 `-warnaserror` 下是错误）。

## WinUI 3 平台约束（Pixbian 实战）
- 分组 ListViewBase 配自定义 ItemsPanel 时排列的是 `GroupItem`；无内置 Justified 布局与 GridLength 动画。自定义标题栏用 `InputNonClientPointerSource` Passthrough。
- ItemsPanelTemplate 内不能 x:Bind 页面属性、不能 ElementName 跨 namescope；面板读子项数据走 `FrameworkElement.DataContext`。**x:Bind 默认 OneTime**，会变的须 `Mode=OneWay`。
- **ItemContainerStyle 模板内 x:Bind 根是模板化控件**；页面属性须经 PageProxy（`Data="{x:Bind}"` 放 Page.Resources）用传统 Binding。**模板内 x:Bind 禁配 StaticResource Converter**（`LookupConverter` 运行时 NRE，编译期 0 警告、延迟数秒~数十秒崩溃）→ 条件显隐一律用 VisualState 状态机。
- **VisualState Setter 优先级高于本地绑定值**：基础值走绑定、hover 用 Setter 覆盖，可纯 XAML 表达条件显示。
- **`Border.CornerRadius` 会裁剪子内容（含投影）** → 圆角图片交给 `Border.Background` 的 `ImageBrush`（无 `Image.CornerRadius`、`RectangleGeometry` 无 RadiusX/Y）。
- 间距由面板 `Spacing` 承担、子项模板零 Margin；嵌套 ScrollViewer 内的 GridView 须禁用自身垂直滚动；多实例 GridView 选择聚合须经实例列表（Loaded/Unloaded 登记）。
- **改控件外观优先覆盖主题资源而非重写模板**，键名规律 `Xxx`/`XxxPointerOver`/`XxxFocused`/`XxxDisabled`；给 `MenuFlyoutItem` 自定义模板会触发旋转忙碌光标；主题键覆盖勿放 `Style.Resources`。圆角两档：4（控件）/8（表面）。
- **ProgressRing 模板动画参与布局测量**，与同格大重排同帧会概率性 `LayoutCycleException`（启动即崩、进程仍活）。换指示器不能根治——循环源是覆盖层与 GridView 同容器交替失效，根治靠**把 loading 覆盖层放窗口层（PageHost 兄弟位）**。取证靠二分 + 连启观察（至少 8 秒 × 多次，6 秒窗口不够）。`x:Load` / x:Bind OneWay 均只支持 Page/UserControl（非 Window），Window 层联动走代码后置 INPC 转发。
- **符号字体码点（离屏渲染实证）**：空心文件夹 `\uED25`；`\uE8B7` 在 Fluent 是实心 FolderFill、MDL2 是文件+书签；线性星 `\uE734` / 实心星 `\uE735`；空心爱心 `\uEB51` / 实心 `\uEB52`（Symbol.Favorite 是爱心非星形）。查码点用 PowerShell + WPF `RenderTargetBitmap` 离屏渲染 PNG 目检（白底）。
- **`MenuFlyout` 从 `Application.Current.Resources` 取出的是共享单例**，重复 `ShowAt` 抛 `E_INVALIDARG` → 可重复弹出的菜单必须工厂方法每次 `new`。菜单改「数据定义 + 工厂方法 + 每次 Opening 重建」可消掉一整类勾选同步代码。
- **unpackaged 应用的 PRI 不索引 `<Content>` 项**，`ms-appx://` 解析不到 → 资源一律按 `AppContext.BaseDirectory` 磁盘路径加载；默认 Content glob 不含 `.jpg`，须显式声明（重复声明报 NETSDK1022）。
- **`StaticResource` 无法解析 `ThemeDictionaries` 内的资源**；业务侧 StaticResource 的画刷须定义在 App.xaml 顶层、主题字典之外。
- **`CommandBar` 动态溢出有未修 bug（issue #6450 not planned）**：触发溢出后 Flyout 永久异常 → 带 Flyout 的工具栏溢出只能手动实现（AdaptiveTrigger + VisualState，「更多」菜单每次 Opening 重建）。
- **切换 ListViewBase 的 `SelectionMode` 会重置选择**：保留选择的切换须先抓快照、归零后再恢复。

## 窗口视觉分层（背景图 + 玻璃卡片）
- 自下而上：全窗口 `Image` 背景（`Grid.RowSpan` 覆盖标题栏行+内容行，`UniformToFill`）→ 透明标题栏与 NavigationView → 内容区半透明卡片（`Border` + `ThemeShadow` + `Translation` z 抬高）。
- 透出背景：`NavigationView{Default,Expanded,Top}PaneBackground` 与 `NavigationViewContentBackground` 全改 `Transparent`，NavigationView 自身 `Background` 也要透明。**Pane 展开态走 `NavigationViewExpandedPaneBackground`**，改 `Default` 无效；死键：`NavigationViewPaneBackground`/`ContentBackground`/`Background`。
- 容器级圆角必须归零（`NavigationViewContentGridCornerRadius`）否则裁掉卡片投影；卡片圆角自己声明（`8,0,0,0` 为默认，浅色主题须在 App.xaml 显式声明）。
- 卡片贴边 = `Margin="24,24,0,0"` + `CornerRadius="12,0,0,0"` + `BorderThickness="1,1,0,0"`，贴边两边归零。
- **Pane 与内容区之间的竖线来自 `ContentGrid` 的 `BorderThickness`(默认 1,1,0,0) 的 Left=1**，非 Pane 自身 Border；覆盖归零即可，勿动 `NavigationViewItemSeparatorForeground`。左栏圆角来自模板 `RightCornerRadiusFilterConverter`，元素级覆盖 `OverlayCornerRadius` 无效。
- **内容区紧贴左栏时，「内容区左上圆角」与「左栏右上深色圆弧」是同一几何事实**，无法同时消除，要兼得只能留缝。
- NavigationView 纵向：Row0 `ContentTopPadding` → Row1 `HeaderContent`(MinHeight=36，可 `AlwaysShowHeader="False"` 消掉) → Row2 `ContentPresenter`。

## 自动化验证（本机实测结论）
- **模拟鼠标完全不可用**（`SetCursorPos`/`mouse_event` 触发不了 Pointer 事件；UIA 的 `Select()` 只改选择状态）。
- 可靠替代：① `VisualStateManager.GoToState` 直接激活状态；② 代码后置赋值 VM 属性 + 通知处打日志；③ 运行时 dump 真实属性（比截图精确）。
- 截图采样须 `SetProcessDPIAware` + `ShowWindow(SW_RESTORE)` + `SetForegroundWindow` + 校验前台句柄与主色，否则采到别的窗口。
- WinUI 应用的 `$p.MainWindowHandle` 常为 0，取句柄用 UIA 的 `NativeWindowHandle`；WinUI 的 `TextBlock`/`Image` 不暴露 UIA 节点。
- 本机会话中 PowerShell 删除文件受 Safe-Delete 钩子拦截；顶层工作区文件夹无法在会话内重命名。

## 图片显示与缩略图管线（稳定结论）
- **WinUI 3 的 `Image`/`ImageBrush` 插值不可控且无 mipmap** → 唯一手段是让位图物理像素尽量等于显示区物理像素。排查顺序：显示尺寸 → DPI → 位图来源 → 插值算法 → 显示端插值（不可控）。
- 请求尺寸语义统一为「显示区最长边」；档位量化**先乘 `RasterizationScale` 再量化**；缩小 `Fant`、放大 `Cubic`（上限 2 倍）；升级加载**只升不降 + 容差**。
- **`BitmapImage` 绝不能同时设 `DecodePixelWidth` 与 `DecodePixelHeight`**（语义是拉伸），只设最长边一维。
- 自定义面板缩放过子项必须回写实际尺寸（`IDisplaySizeAware`）。尺寸探测：图片取 `BitmapDecoder.OrientedPixel*`；视频取 `GetVideoPropertiesAsync()` 并按旋转交换宽高（旋转还原错了比不探测更糟）。
- **异步管线按线程亲和性切开**：中间产物用 `byte[]`，CPU 段限流放线程池，只在最后一跳回 UI 线程构造 `BitmapImage`。**批量写回 UI 线程**；**绝不能 `EnqueueAsync(async () => await Xxx())` 而 Xxx 又 `EnqueueAsync`**（自我死锁）；**批量加载循环必须 `await Task.WhenAll`**（await 即背压）。UI 状态赋值统一放进 `EnqueueAsync` 块（其延续落在线程池线程）。
- `DataReader.ReadBytes` 填充调用方缓冲区；写用 `DataWriter`（`WriteBytes` → `StoreAsync` → **`DetachStream`**）。
- 异步加载治理：**信号量只控并发数，不控「该不该做」**；可见性判定用 `ContainerFromItem(item) is null`；滚走取消 + 滚回重触发。**删除「整页提交」兜底是高危操作**（曾误删致图片全不显示）。
- **「分辨率」是绝对像素、「宽高比」是相对值，取值优先级不可共用**（分辨率忌用缩略图位图像素）。色彩链路（`ColorManageToSRgb` + `RespectExifOrientation` + `OrientedPixel*`）已验证正确，勿改。

## 骨架屏三态展示 — 方法论
- `ThumbnailPresenter` + `ThumbnailLoadState` 三态，`ThumbnailState` 是唯一数据源。**取消 ≠ 失败**，必须回落 Loading。
- WinUI 3 XAML 无 `EventTrigger`/`BeginStoryboard` → 状态驱动动画选「模板化控件 + VisualState」；`RepeatBehavior=Forever` 容器回收后不自停，`Unloaded` 里回静态态。
- **`{TemplateBinding}` 是一次性求值**，运行期会变的属性须依赖属性回调写入；DataTemplate 重构为 ControlTemplate 时绑定机制静默改变，须逐个复核。
- **虚拟化容器回收复用不会重新应用模板**：`Unloaded` 改过的状态要在 `Loaded` 对称恢复；「上一次 X」字段须在 `DataContextChanged` 清除。
- **「内容可绘制」与「数据已就绪」是两个时刻**：可靠信号是 Image **控件级** `ImageOpened`（透明探针取信号 + ImageBrush 显示）；探针就绪后纹理可能晚一帧，淡入再等 `CompositionTarget.Rendering` 一帧。
- 动画异常排查顺序：① 就绪信号级别 → ② 状态被重置 → ③ 跃迁判据（**正向枚举**）→ ④ 二次换源 → ⑤ 帧间隔 → ⑥ 形状/位置 → ⑦ 播放时机 → ⑧ 时长/缓动。**先问「什么时候播」再问「怎么播」**。小元素动画 250ms 实测不足（本项目定 500ms）。

## 索引与元数据回填
- 索引分**两阶段**：扫描只写文件属性（秒级，界面立即可浏览）；宽高与时长由后台回填服务分批补齐。界面预取只对「索引无宽高」的条目发起，回填完成后降为零文件 IO。
- 探测状态三态设计的关键：**失败必须落为「已失败」**，否则损坏文件每轮被反复捞取、占满批次额度，回填永不收敛。
- **后台常驻任务的并发度必须显著低于前台预取**：与用户正在浏览的缩略图解码争抢 IO 会直接拖慢页面，宁可慢也不能抢。
- 元数据写回只覆盖尺寸/时长列；收藏、评分、分类属用户数据，一律 `COALESCE` 保护、不得触碰。

## NuGet 漏洞审计（NU19xx）
- 常规构建用 `WarningsNotAsErrors` 豁免 NU19xx，审计流水线 `-p:AuditPipeline=true` 才升级为错误（官方推荐分离）；`auditSources` 需 NuGet 6.12 / .NET 9 SDK，本机（6.11 / SDK 8.0.424）不支持。

## 产品/技术决策（已定，勿反复）
- 定位相册浏览器 → 砍掉 MagicScaler，Win2D 降为可选；优先「查看器两级加载 + 磁盘缩略图缓存」。**基线测量优先于选型**；Windows 照片应用不可对标（闭源管线），可对标 ImageGlass / FlyPhotos。
- 排序为 `MediaSortKey` × `SortDirection` 两维；随机排序用 `RandomSeed` 取模保证同种子分页稳定，不能用 SQL `RANDOM()`。
- 删除文件走回收站（`RecycleBinHelper`，SHFileOperation + FOF_ALLOWUNDO）并同步清索引；「从索引移除」仅删记录。
- **EXIF 拍摄时间暂不回填**：本轮只补尺寸/时长。是否把排序键从 `modified_utc` 换成真实拍摄时间属产品语义决策，尚未拍板。

## 已知未做项
- 磁盘缩略图缓存目录已定义但零引用 → 冷启动全量重解码。缓存键**必须带 `modified_utc` + `file_size`**（现有按路径的键在文件被同名替换后会返回旧图，是现存正确性缺陷）。
- 图片查看器绕过 `ThumbnailService`（全分辨率加载）：首帧慢、内存高，是当前最大洼地。
- 内存缓存 `SizeLimit=2000` 按条数而非字节数计，存在 OOM 隐患。HEIC/AVIF 依赖 WIC 编解码器扩展。
- SQL 排序用 CASE 表达式导致索引失效、`directory LIKE` 前缀与 BINARY 排序规则不匹配、`OFFSET` 深翻页——三项均**待 `EXPLAIN QUERY PLAN` 实测确认**后才可动手。
- 背景图固定 `light.jpg` 不随主题切换：深色主题下文字对比度不足（`dark.jpg` 已在 Assets 待用）。
- 历史遗留 `Diagnostics.Log`：`DBLTAP`、`VIEWER`、`THUMB`，以及本轮基线测量新增的 `LOAD`/`STAT`/`PROBE`/`THUMBWAIT`/`LOADTOTAL`。
