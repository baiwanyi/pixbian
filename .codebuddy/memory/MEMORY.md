# 长期记忆

> 只收规范、稳定事实与可复用方法论；不收代码定义、具体数值、一次性排障流水（那些进当日日志）。

## 项目与开发环境
- Pixbian：WinUI 3 桌面**相册浏览器**（非编辑器）。WASDK 2.4.0 + `net8.0-windows10.0.26100.0`，基线 17763。
- `dotnet` 不在 PATH，须用 `C:\Program Files\dotnet\dotnet.exe`；包管理一律 `pnpm`。
- 已启用 `ImplicitUsings`/`Nullable`/`LangVersion 12`；CI 用 `-warnaserror`（须 0 警告）；缩进 4 空格；文件头 3–8 行中文模块说明。
- 测试基线：`dotnet test` 共 181 个（Core 136 / WebServer 33 / Imaging 12）。
- **硬件与数据位置**：C: 是三星 SSD，D: 是机械硬盘（`ST1000DM003`）。媒体库在 `D:\Downloads\*`（2.5 万条、平均 2.9 MB）。**D: 余量长期偏低（曾低至 2.4%）**——HDD 上空间不足会显著放大碎片与寻道延迟，排查任何「慢/卡」前先看 D: 余量。HDD 随机读 1MB 约 105ms（SSD <1ms），**任何性能结论都必须在这块盘上实测**。
- 工作区在 OneDrive：新产物落盘后立即启动可能被同步/杀软锁定 → 一键脚本用「显式 build + Start-Process」两段式；构建前确认应用未运行（exe 被持有报 MSB3026）。
- 诊断脚本放工作区外（`C:\Temp\...`）。含中文的 `.ps1` 必须 UTF-8 with BOM；**终端传入含中文的命令会语法错误**，诊断命令一律纯英文；终端 GBK 乱码不等于程序字符串有误。
- **本机系统还原创建通道失效**：`Checkpoint-Computer` 与 WMI `SystemRestore.CreateRestorePoint` 都返回成功却不生成还原点（已排除 VSS 服务、磁盘空间、卷影存储上限、24h 节流）→ 系统级变更前**不要依赖还原点**，改用 `pnputil /export-driver` 导出驱动包做精确回退（pnputil 不会自动建目标目录，须先 `New-Item -Force`）。
- 嵌套 `powershell -Command "..."` 会吞噬内层 `$var` / `$_`；提权脚本（`Start-Process -Verb RunAs`）的 stdout 不回传且不能配 `-RedirectStandardOutput` → **统一写 `.ps1` 文件执行，并在脚本内落日志文件**。

## 分层与依赖方向（改动前必查）
- `Pixbian.Core` 最底层、**零项目引用**、TFM 纯 `net8.0`；`Data`/`Imaging`/`Media`/`WebServer` 单向引用 Core，`Pixbian`(UI) 引用全部。
- **Core 不得使用 WIC / `Windows.Graphics.Imaging`**（会把 Core 绑到 windows TFM，破坏 Core.Tests 基线）→ Core 定抽象 + UI 层注入实现。
- 跨层数据交换统一走 `Pixbian.Core.Models`，禁止向上泄漏 `SqliteDataReader` 等基础设施类型。

## 编码与协作规范
- 禁用 eslint-disable、禁用 TS 双重断言（改类型收窄/泛型修正）；import 走 `import-x/order`；禁用已弃用 API。
- 敏感信息禁止硬编码；API 响应须 DTO 白名单过滤；日志脱敏。
- 改动 > 5 文件须先弹窗确认是否 commit（Conventional Commits）；> 2 文件的大改先取得用户确认方案。
- 每次修改须记 `.codebuddy/memory/YYYY-MM-DD.md`（超 1000 行分卷）。非用户要求不主动写记忆；整理时剔除代码定义与操作步骤。

## 通用工程方法论
- **性能定位顺序：先测真实数据规模 → 再测单点耗时 → 最后改代码**。用户口述规模必须实测。
- 连 WAL 库用 `SqliteOpenMode.ReadWrite` 可与应用并发读。
- 后台任务**让出比例比绝对时长更关键**（批次 2.5s 时节流 ≥1.5s），并设批次数上限；常驻任务**节流 + 排他**两条护栏缺一不可，多入口必须收口到同一把锁。排他优先 `Interlocked.CompareExchange`（持 CTS 字段触发 CA1001，在 `-warnaserror` 下是错误）。
- 排查「卡死」先分崩溃 vs 无响应：先看 `crash.log` 时间戳，无新增崩溃即排除异常与布局循环，改查后台重活争抢。
- **查 API 是否存在一律读包内二进制，不凭记忆/文档**：WinRT 投影在 `microsoft.windows.sdk.net.ref/<ver>/winmd/`；WinUI 组件是 `Microsoft.WinUI.dll`（`.xml` 成员索引 `T:`/`P:`/`M:` 更精确）；模板默认值读 `Microsoft.WinUI/Themes/generic.xaml`。Microsoft Learn 的 WinRT 页会写错；超长页面勿用 web_fetch（会截断）。winmd 不能 `Assembly.LoadFrom`（.NET 8 报 0x80131515）。
- `search_content` 的 `glob` 不支持 `!` 取反；判断忽略用 `git check-ignore -v`。查 MSBuild 属性：`dotnet msbuild xxx.csproj -getProperty:属性名`。

## WinUI 3 / WASDK 关键事实
- 2.x 的 XAML 编译器路径须用 `PkgMicrosoft_WindowsAppSDK_WinUI`（旧键 MarkupCompilePass1 静默失败，全项目爆 `CS0103`）；dotnet 宿主下 exe 模式编译器是唯一可用路径（进程内 Task 在 .NET 8 SDK 下 `MSB4062`）。TFM 升 26100 不需装 SDK 26100。
- **C# 错误会连锁引发 XAML "Unknown type" 假错误**，先修 CS 再查 XAML；VS Code 的 `.g.i.cs` 误报 CS0103 是固有限制，**以 `dotnet build` 为准**；勿动 `BaseIntermediateOutputPath`、勿删 `obj\`。
- **XAML 编译期不校验颜色字面量**：`##RRGGBB` 能 0 警告构建、运行时才崩（stowed exception `0xC000027B`）。「构建成功 + 启动崩溃」先 `git status` 全量排查。
- `WinUIEx` 已移除 → `AppWindow.SetIcon(string)`；Picker 用 `Microsoft.Windows.Storage.Pickers`（构造传 `WindowId`）。
- 无 `RenderOptions.BitmapInterpolationMode`；缩放比取 `XamlRoot.RasterizationScale`。
- **`SoftwareBitmapSource` 已两轮实测不可用（2026-09-02）**：文档标注 ThreadingModel.Both+Agile，但作为 XAML DependencyObject 实际有 UI 亲和——线程池创建挂视图树、乃至 UI 线程 `SetBitmapAsync` 均触发 `Microsoft.UI.Xaml.dll` fail-fast `0xC000027B`（绕过托管 UnhandledException，crash.log 无记录，去 Windows 事件日志查 ID 1000 拿故障模块）。`BitmapImage` + UI 线程 `SetSourceAsync` 是唯一稳定显示管线。**教训：动手前先查本记忆既有结论，文档元数据不可信。**
- unpackaged 应用要 Win11 圆角只能靠 `MicaBackdrop`（2.3.6 无 `TransparentBackdrop`），Mica 仅 Win11 生效；**PRI 不索引 `<Content>` 项** → 资源按 `AppContext.BaseDirectory` 磁盘路径加载（默认 Content glob 不含 `.jpg`）。
- `Page.KeyboardAccelerators` 会污染页面内所有 ToolTip（官方 by design）→ 用代码后置 KeyDown；菜单项 `KeyboardAcceleratorTextOverride` 是豁免用法。
- `ThemeShadow` + `Translation`：**z 是投影唯一输入**，z=0 几乎不可见；`Translation` 不参与布局。`Border.CornerRadius` 会裁剪子内容（含投影）→ 圆角图片交给 `Border.Background` 的 `ImageBrush`。
- 持有 `SemaphoreSlim`/`CTS` 等可释放字段的类型必须实现 `IDisposable`（CA1001 在 `-warnaserror` 下是错误）。
- `StaticResource` 无法解析 `ThemeDictionaries` 内的资源 → 业务侧画刷定义在 App.xaml 顶层、主题字典之外。

## WinUI 3 平台约束（Pixbian 实战）
- 分组 ListViewBase 配自定义 ItemsPanel 时排列的是 `GroupItem`；无内置 Justified 布局与 GridLength 动画。自定义标题栏用 `InputNonClientPointerSource` Passthrough。
- ItemsPanelTemplate 内不能 x:Bind 页面属性、不能 ElementName 跨 namescope；面板读子项数据走 `FrameworkElement.DataContext`。**x:Bind 默认 OneTime**，会变的须 `Mode=OneWay`。`x:Load`/x:Bind OneWay 只支持 Page/UserControl（非 Window），Window 层联动走代码后置 INPC 转发。
- ItemContainerStyle 模板内 x:Bind 根是模板化控件；页面属性须经 PageProxy（`Data="{x:Bind}"` 放 Page.Resources）用传统 Binding。**模板内 x:Bind 禁配 StaticResource Converter**（`LookupConverter` 运行时 NRE，编译期 0 警告、延迟数秒~数十秒崩溃）→ 条件显隐一律用 VisualState 状态机；**VisualState Setter 优先级高于本地绑定值**。
- 改控件外观优先覆盖主题资源而非重写模板，键名规律 `Xxx`/`XxxPointerOver`/`XxxFocused`/`XxxDisabled`；给 `MenuFlyoutItem` 自定义模板会触发旋转忙碌光标；主题键覆盖勿放 `Style.Resources`。圆角两档：4（控件）/8（表面）。
- `MenuFlyout` 从 `Application.Current.Resources` 取出的是共享单例，重复 `ShowAt` 抛 `E_INVALIDARG` → 可重复弹出的菜单须工厂方法每次 `new`（「数据定义 + 工厂方法 + 每次 Opening 重建」可消掉一整类勾选同步代码）。
- `CommandBar` 动态溢出有未修 bug（issue #6450 not planned）→ 带 Flyout 的工具栏溢出只能手动实现（AdaptiveTrigger + VisualState）。
- 切换 ListViewBase 的 `SelectionMode` 会重置选择 → 先抓快照、归零后再恢复。
- 间距由面板 `Spacing` 承担、子项模板零 Margin；嵌套 ScrollViewer 内的 GridView 须禁用自身垂直滚动；多实例 GridView 选择聚合须经实例列表（Loaded/Unloaded 登记）。
- 符号字体码点（离屏渲染实证）：空心文件夹 `\uED25`；`\uE8B7` 是实心 FolderFill；线性星 `\uE734`/实心星 `\uE735`；空心爱心 `\uEB51`/实心 `\uEB52`（Symbol.Favorite 是爱心非星形）。查码点用 PowerShell + WPF `RenderTargetBitmap` 离屏渲染 PNG 目检。

## LayoutCycle 排查（判别式）
- **CPU 单核 ~100% + 业务日志 0 增长 = 布局/渲染死循环**；CPU 高但日志持续增长 = 业务慢。死循环时托管堆栈为空、`crash.log` 常不留痕迹。
- **绝不要让「缩略图/降采样位图的尺寸」参与任何驱动布局的属性**：位图按档位量化解码，宽高比有微小偏差，一旦覆盖已有准确值就形成 **解码 → 宽高比抖动 → 重排 → 回写 → 再解码** 的环。位图尺寸只能作兜底且优先级排最后。「宽高比是相对值所以用位图无害」是错误判断。
- 覆盖层与内容网格同处一个布局容器时，同帧内既替换整页条目又折叠覆盖层会让两者测量互相失效 → 撤 loading 覆盖层前须让布局落地（**不得用 `CompositionTarget.Rendering` 等帧**，见下条）。
- **【2026-09-02 定案】订阅 `CompositionTarget.Rendering` 等渲染帧会令合成呈现停摆**：UI 线程存活（TICK 规律、栈停消息循环无业务帧）、布局 pass 永久死亡（面板测量停止）、画面冻结在最后一帧、hover 无反应。三组减法实验实锤（掐缩略图仍冻 → 20 条仍冻 → 摘等待即愈），`WaitForNextRenderFrameAsync` 已永久移除。**任何订阅/等待 `CompositionTarget.Rendering` 的机制均属高危**，错峰一律用 DispatcherQueue 优先级。图库冻结真实分水岭是 84c9e74（引入此机制）而非 e75be83；"连启 N 次稳定"对概率性触发属小样本误判。
- **卡死排查三板斧定案序列**：① `dotnet-stack report` 抓托管栈判 UI 死/活（进程全空闲+UI 停消息循环=非线程问题）② TICK 心跳间隙扫描判同步阻塞 ③ diag.log 业务时序判管线进度（LOADTOTAL 出现而面板测量停止=加载全绿而布局死，直指渲染 tick）。**布局/上屏跑在渲染 tick，与 DispatcherQueue 定时器是两条生命周期，判死必须分别取证**。多嫌疑时用**叠加减法实验**逐轮排除（每轮单变量），干净基线复现可同时证伪历史误判。
- **ConfigureAwait(true) 不是"回到 UI 线程"**：它恢复的是**各 await 点当时捕获的上下文**；中途任一 await 用 false 脱离后 `SynchronizationContext.Current` 变 null，后续 true 无法切回（实测在线程池创建 BitmapImage 抛 0x8001010E，整页缩略图静默全灭）。跨线程回 UI 的唯一可靠手段：**服务经构造注入 DispatcherQueue + `TryEnqueue` + TaskCompletionSource（RunContinuationsAsynchronously）桥接**，async void 回调内异常必须收口到任务源。
- **"任务正常完成"≠"有效工作"**：WhenAll 完成但产出 0 = 全员静默失败。给服务层 catch 加取证日志（异常类型 + **HResult**——WinRT 的 COMException 常 无 Message，HResult 是唯一线索），一次测试即可定案；单条 IO 必须有超时（`WaitAsync`），否则挂死任务占死信号量槽位令整条管线静默死亡。
- **窗口级 indeterminate 动画（ProgressBar IsIndeterminate / ProgressRing）本身就是布局刺激源**，即使隔离到窗口层仍每帧搅动布局 pass → **loading 覆盖层一律用无动画静态文本**。
- **「慢」与「冻结」必须先分清再动手**：整套卡死排查（驱动/磁盘/GPU/死锁/渲染停摆）的前提是「应用无响应」。若心跳（UI 线程）正常、日志持续增长、CPU 与线程池空闲，那**不是卡死而是慢**，此时查驱动/GPU/死锁全是浪费。判据优先级：先看心跳有无中断 → 再看日志有无产出 → 最后才看 CPU。
- **「视觉死但日志活」= 布局系统坏死而非进程死**，不能凭「进程 Responding」判断界面可用。同理，**「加载完成但长时间骨架屏」是缩略图并发不足导致的排队，不是卡死**——整页 N 条 ÷ 并发度 × 单条耗时即可估算总时长，勿误判为渲染问题。
- **冻结三态判别式（补齐第三种）**：① CPU 单核 100% + 日志停滞 = 布局死循环；② CPU 高 + 日志持续增长 = 业务慢；③ **CPU 增量 0 + 日志完全停滞 + 全线程 Wait + 窗口 Hung=False = 数据早已绪而渲染停摆**（等待不返回的异步操作或合成管线停摆），此时 `Wait()`/锁/线程池都查不到东西。**务必先看 CPU 增量再决定排查方向**，③ 与 ① 的处理完全相反。
- 排查卡死时 `dotnet-stack report` 可直接对运行中进程打托管栈（无需转储）；`dotnet-dump collect --type Full` 抓转储很快，但 `dotnet-dump analyze` 对数百 MB 转储耗时极长。winmd 不能被 `Add-Type -ReferencedAssemblies` 引用（0x80131047），无法用内联 C# 直接调 WinRT API 做基准。
- 时间戳对齐是循环取证核心：crash 早于业务日志 → 循环发生在数据加载前阶段。自证日志无循环特征可证明该环节无辜、嫌疑上移。
- 概率性缺陷被性能优化引爆是常态：不要回滚优化，去找被掩盖的根因。

## 窗口视觉分层（背景图 + 玻璃卡片）
- 自下而上：全窗口 `Image` 背景（`Grid.RowSpan` 覆盖标题栏行+内容行，`UniformToFill`）→ 透明标题栏与 NavigationView → 内容区半透明卡片（`Border` + `ThemeShadow` + `Translation`）。
- 透出背景：`NavigationView{Default,Expanded,Top}PaneBackground` 与 `NavigationViewContentBackground` 全改 `Transparent`，NavigationView 自身 `Background` 也要透明。死键：`NavigationViewPaneBackground`/`ContentBackground`/`Background`。
- 容器级圆角必须归零（`NavigationViewContentGridCornerRadius`）否则裁掉卡片投影；卡片圆角自己声明。卡片贴边 = `Margin="24,24,0,0"` + `CornerRadius="12,0,0,0"` + `BorderThickness="1,1,0,0"`。
- Pane 与内容区之间的竖线来自 `ContentGrid` 的 `BorderThickness.Left=1`；左栏圆角来自模板 `RightCornerRadiusFilterConverter`，元素级覆盖 `OverlayCornerRadius` 无效。
- 内容区紧贴左栏时，「内容区左上圆角」与「左栏右上深色圆弧」是同一几何事实，要兼得只能留缝。
- NavigationView 纵向：Row0 `ContentTopPadding` → Row1 `HeaderContent`（MinHeight=36，可 `AlwaysShowHeader="False"` 消掉）→ Row2 `ContentPresenter`。

## 图片显示与缩略图管线（稳定结论）
- **WinUI 3 的 `Image`/`ImageBrush` 插值不可控且无 mipmap** → 唯一手段是让位图物理像素尽量等于显示区物理像素。排查顺序：显示尺寸 → DPI → 位图来源 → 插值算法 → 显示端插值（不可控）。
- 请求尺寸语义统一为「显示区最长边」；档位量化**先乘 `RasterizationScale` 再量化**；缩小 `Fant`、放大 `Cubic`（上限 2 倍）；升级加载只升不降 + 容差。
- **`BitmapImage` 绝不能同时设 `DecodePixelWidth` 与 `DecodePixelHeight`**（语义是拉伸），只设最长边一维。
- 自定义面板缩放过子项必须回写实际尺寸（`IDisplaySizeAware`）。尺寸探测：图片取 `BitmapDecoder.OrientedPixel*`；视频取 `GetVideoPropertiesAsync()` 并按旋转交换宽高。
- **异步管线按线程亲和性切开**：中间产物用 `byte[]`，CPU 段限流放线程池，只在最后一跳回 UI 线程构造 `BitmapImage`。批量写回 UI 线程；**绝不能 `EnqueueAsync(async () => await Xxx())` 而 Xxx 又 `EnqueueAsync`**（自我死锁）；批量加载循环必须 `await Task.WhenAll`；UI 状态赋值统一放进 `EnqueueAsync` 块。
- 异步加载治理：**信号量只控并发数，不控「该不该做」**；滚走取消 + 滚回重触发。删除「整页提交」兜底是高危操作。
- **`ContainerFromItem` 不能单独用作可见性判据**：虚拟化下列表对「从未进入视口」与「曾进入视口后被回收」**都返回 null**。据此取消在途解码会把整页预取整批取消，而条目因在途标记已置位又拒绝重新发起 → **缩略图永不出现，表现为界面「冻结」且 CPU 接近 0**。必须额外记录「是否曾生成过容器」（在 `ContainerContentChanging` 置位），只取消「曾生成过容器且现已无容器」的条目。
- **`THUMBWAIT` 显示 0ms 未必异常**：整页提交时若解码已由 `SetDisplaySize` 先行发起，`EnsureThumbnailAsync` 会因在途标记提前 return，`WhenAll` 等到的全是立即完成的空转 Task。真正解码在先发起的那个 Task 里，要查它是否被取消。
- **读性能日志前先看计时起点**：`ThumbnailService` 的 `stopwatch` 在 `_decodeGate.WaitAsync` **之前**启动，故 `THUMB|...|elapsedMs` = 解码 + 信号量排队，不是单条解码耗时。整页 200 条 ÷ 并发 4 ≈ 50 批排队，中位数可达 5s+。**用该值判断「解码慢」会误判为算法问题，实际常是并发排队或磁盘 IO。**
- **缩略图续体一律回 UI 线程**（`ConfigureAwait(true)`，因 `BitmapImage` 是 DependencyObject）：整页提交时上百个续体轮番占用 UI 线程，「加载已完成但界面仍卡死」多源于此，表现为 `THUMBWAIT` 很短而实际长时间无响应。
- 「分辨率」是绝对像素、「宽高比」是相对值，取值优先级不可共用。色彩链路（`ColorManageToSRgb` + `RespectExifOrientation` + `OrientedPixel*`）已验证正确，勿改。

## 骨架屏三态展示 — 方法论
- `ThumbnailPresenter` + `ThumbnailLoadState` 三态，`ThumbnailState` 是唯一数据源。**取消 ≠ 失败**，必须回落 Loading。
- WinUI 3 XAML 无 `EventTrigger`/`BeginStoryboard` → 状态驱动动画选「模板化控件 + VisualState」；`RepeatBehavior=Forever` 容器回收后不自停，`Unloaded` 里回静态态。
- **`{TemplateBinding}` 是一次性求值**，运行期会变的属性须依赖属性回调写入；DataTemplate 重构为 ControlTemplate 时绑定机制静默改变，须逐个复核。
- **虚拟化容器回收复用不会重新应用模板**：`Unloaded` 改过的状态要在 `Loaded` 对称恢复；「上一次 X」字段须在 `DataContextChanged` 清除。
- **「内容可绘制」与「数据已就绪」是两个时刻**：可靠信号是 Image **控件级** `ImageOpened`；探针就绪后纹理可能晚一帧，淡入再等 `CompositionTarget.Rendering` 一帧。
- 动画异常排查顺序：就绪信号级别 → 状态被重置 → 跃迁判据（正向枚举）→ 二次换源 → 帧间隔 → 形状/位置 → 播放时机 → 时长/缓动。**先问「什么时候播」再问「怎么播」**。小元素动画 250ms 不足（本项目定 500ms）。

## 索引与元数据回填
- 索引分两阶段：扫描只写文件属性（秒级，界面立即可浏览）；宽高与时长由后台回填服务分批补齐。
- 探测状态三态设计的关键：**失败必须落为「已失败」**，否则损坏文件每轮被反复捞取、回填永不收敛。
- 元数据写回只覆盖尺寸/时长列；收藏、评分、分类属用户数据，一律 `COALESCE` 保护。

## 产品/技术决策（已定，勿反复）
- 定位相册浏览器 → 砍掉 MagicScaler，Win2D 降为可选；优先「查看器两级加载 + 磁盘缩略图缓存」。**基线测量优先于选型**；可对标 ImageGlass / FlyPhotos，不可对标 Windows 照片应用（闭源管线）。
- 排序为 `MediaSortKey` × `SortDirection` 两维；随机排序用 `RandomSeed` 取模保证同种子分页稳定，不能用 SQL `RANDOM()`。
- 删除文件走回收站（`RecycleBinHelper`，SHFileOperation + FOF_ALLOWUNDO）并同步清索引；「从索引移除」仅删记录。
- EXIF 拍摄时间暂不回填（本轮只补尺寸/时长）；排序键是否换成真实拍摄时间属产品语义决策，尚未拍板。

## 已知未做项
- **缩略图磁盘缓存已落地（2026-09-02，`036efac`）**：`Pixbian.Core/Services/ThumbnailDiskCache.cs`——两级哈希分桶（sha256(path) 前 4 hex，65536 桶按百万级条目管理）、条目头 16B 指纹（源 mtime.Ticks+size，读时 stat 校验、失配即删）、LRU 2GB（内存表启动扫描以文件 LastWriteTimeUtc 重建访问序，命中 60s 节流 touch）、temp+原子 Move、IO 失败全静默。**已知项**：百万级条目时内存表约 150MB，需紧凑化。机会性预取未做。
- **关键教训（Invalidate 语义）**：缓存分两层后 `Invalidate`=内容真失效（清内存+清磁盘）与 `Release`=仅释放内存位图（保留磁盘）必须分开；切换视图/列表瘦身误用 Invalidate 会把磁盘缓存删光，缓存形同虚设（实测 5 轮切换清空全部条目）。
- **统一解码档位 512**：请求档位 ≤512 一律按 512 解码/缓存/落盘（`UnifiedBucket`），显示端缩小——条目数从「档位数×文件数」降为「文件数」，视图切换全量命中；代价低档位位图内存 ×4，由内存 200MB 字节限额 + 头部瘦身 300 条兜底。
- **分帧提交 + 撤层提前**：整页 200 条一次性提交会让 UI 被解码回调钉死（点击排队=卡顿）。首屏 60 条（10 条/批 + 40ms 让出）就绪即撤覆盖层，积压 140 条后台渐进；每批校验 loadSequence。用户已接受该观感。
- 图片查看器绕过 `ThumbnailService`（全分辨率加载）：**已修复（2026-09-02 两级加载）**——512 预览亚秒垫场 + 全图替换 + 装载序号防翻页串图。
- **未做项评估定论（2026-09-02，勿重复评估）**：
  - A3 非随机排序游标：不做——2.5 万条下 OFFSET 是索引遍历（几十 ms），触发器=库 10 万+ 且深翻实测 >200ms。
  - E `_items` 滑动窗口：暂缓——随机模式单会话上限=全库 2.5 万条属罕见；offset 补偿在 JustifiedPanel（行宽自适应）上复杂度高；位图大头已由瘦身 300 条控制。触发器=实测 `_items>2000` 且追加停顿可感（LOADTOTAL 埋点 items=N 可监控）。
  - S0 埋点清理：发布前统一做（DISK/BITMAP/VIEWER/STAT 等，Diagnostics.cs 头部已注明）。
  - 机会性预取：永久搁置——写路径已覆盖浏览路径；全库预生成 3.5GB 超 LRU 上限且 HDD 磨损；随机模式目录切换不可预测。
- 查看器两级加载已落地并提交（`0346521`，2026-09-02 实测过关）。
- A4 统计已异步化（后台 COUNT 并行 + 代数校验）；排序键索引（A1）与随机固定序列游标分页（A2，Schema v4 `random_rank`）已落地。
- 背景图固定 `light.jpg` 不随主题切换：深色主题下文字对比度不足（`dark.jpg` 已在 Assets 待用）。
- NuGet 审计：常规构建用 `WarningsNotAsErrors` 豁免 NU19xx，审计流水线 `-p:AuditPipeline=true` 才升级为错误。
- **XamlCompiler 生成代码（.g.cs 的 x:Bind 方法签名）缓存旧类型元数据**：改 VM 属性类型后报 CS1503 时 `dotnet clean` 即解（OneDrive 下删 obj 会被安全删除工具拦截）。
- HEIC/AVIF 依赖 WIC 编解码器扩展。SQL `OFFSET` 深翻页已由游标分页取代；`EXPLAIN QUERY PLAN` 复核随新查询进行。
