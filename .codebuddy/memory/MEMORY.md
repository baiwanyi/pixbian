# 长期记忆

> 只收规范、稳定事实与可复用方法论；不收代码定义、具体数值、一次性排障流水（那些进当日日志）。

## 项目与开发环境
- Pixbian：WinUI 3 桌面**相册浏览器**（非编辑器）。WASDK 2.4.0 + `net8.0-windows10.0.26100.0`，基线 17763。测试基线 `dotnet test` 181 个（Core 136 / WebServer 33 / Imaging 12）。
- `dotnet` 不在 PATH，须用 `C:\Program Files\dotnet\dotnet.exe`；包管理一律 `pnpm`。CI 用 `-warnaserror`（须 0 警告）；缩进 4 空格；文件头 3–8 行中文模块说明。
- 硬件：C: 三星 SSD，D: 机械盘（`ST1000DM003`）。媒体库 `D:\Downloads\*`（2.5 万条、平均 2.9 MB）。**D: 余量长期偏低**——HDD 空间不足会放大碎片与寻道延迟，查「慢/卡」前先看余量。HDD 随机读 1MB ≈105ms（SSD <1ms），**性能结论必须在这块盘上实测**。
- 工作区在 OneDrive：新产物落盘后立即启动可能被同步/杀软锁定 → 一键脚本用「显式 build + Start-Process」两段式；构建前确认应用未运行（exe 被持有报 MSB3026）。
- 诊断脚本放工作区外（`C:\Temp\...`）：含中文的 `.ps1` 必须 UTF-8 with BOM；**终端传入含中文的命令会语法错误**（诊断命令一律纯英文）；终端 GBK 乱码不等于程序字符串有误。
- **本机系统还原创建通道失效**（`Checkpoint-Computer` 与 WMI 均返回成功却不生成还原点）→ 系统级变更前不依赖还原点，改用 `pnputil /export-driver` 导出驱动包回退（须先 `New-Item -Force` 建目标目录）。
- 嵌套 `powershell -Command "..."` 会吞噬内层 `$var`/`$_`；提权脚本（`Start-Process -Verb RunAs`）的 stdout 不回传 → 统一写 `.ps1` 文件执行并在脚本内落日志。

## 分层与依赖方向（改动前必查）
- `Pixbian.Core` 最底层、**零项目引用**、TFM 纯 `net8.0`；`Data`/`Imaging`/`Media`/`WebServer` 单向引用 Core，`Pixbian`(UI) 引用全部。
- Core 不得使用 WIC / `Windows.Graphics.Imaging`（会把 Core 绑到 windows TFM，破坏 Core.Tests 基线）→ Core 定抽象 + UI 层注入实现。
- 跨层数据交换统一走 `Pixbian.Core.Models`，禁止向上泄漏 `SqliteDataReader` 等基础设施类型。

## 编码与协作规范
- 敏感信息禁止硬编码；API 响应须 DTO 白名单过滤；日志脱敏。
- 改动 > 5 文件须先弹窗确认是否 commit（Conventional Commits）；> 2 文件的大改先取得用户确认方案。
- 每次修改须记 `.codebuddy/memory/YYYY-MM-DD.md`（超 1000 行分卷）。非用户要求不主动写记忆；整理时剔除代码定义与操作步骤。

## 通用工程方法论
- **性能定位顺序：先测真实数据规模 → 再测单点耗时 → 最后改代码**。用户口述规模必须实测。
- 后台任务**让出比例比绝对时长更关键**（批次 2.5s 时节流 ≥1.5s）并设批次数上限；常驻任务**节流 + 排他**两条护栏缺一不可，多入口收口到同一把锁。排他优先 `Interlocked.CompareExchange`（持 CTS 字段触发 CA1001，`-warnaserror` 下是错误）。
- **查 API 是否存在一律读包内二进制，不凭记忆/文档**：WinRT 投影在 `microsoft.windows.sdk.net.ref/<ver>/winmd/`；WinUI 组件在 `Microsoft.WinUI.dll`（`.xml` 成员索引 `T:`/`P:`/`M:` 更精确）；模板默认值读 `Microsoft.WinUI/Themes/generic.xaml`。Learn 的 WinRT 页会写错；超长页面勿用 web_fetch（截断）。winmd 不能 `Assembly.LoadFrom`（.NET 8 报 0x80131515）。
- 工具事实：连 WAL 库用 `SqliteOpenMode.ReadWrite` 可与运行中的应用并发读；`search_content` 的 `glob` 不支持 `!` 取反（用 `git check-ignore -v`）；查 MSBuild 属性用 `dotnet msbuild x.csproj -getProperty:名`；`dotnet-stack report` 可直接对运行中进程打托管栈，`dotnet-dump analyze` 对数百 MB 转储耗时极长。

## WinUI 3 / WASDK 关键事实
- 2.x 的 XAML 编译器路径须用 `PkgMicrosoft_WindowsAppSDK_WinUI`（旧键 MarkupCompilePass1 静默失败，全项目爆 `CS0103`）；dotnet 宿主下 exe 模式编译器是唯一可用路径（进程内 Task 在 .NET 8 SDK 下 `MSB4062`）。TFM 升 26100 不需装 SDK 26100。
- **C# 错误会连锁引发 XAML "Unknown type" 假错误**，先修 CS 再查 XAML；VS Code 的 `.g.i.cs` 误报 CS0103 是固有限制，**以 `dotnet build` 为准**；勿动 `BaseIntermediateOutputPath`、勿删 `obj\`。
- XAML 编译期**不校验颜色字面量**：`##RRGGBB` 能 0 警告构建、运行时才崩（stowed exception `0xC000027B`）。「构建成功 + 启动崩溃」先 `git status` 全量排查。
- 颜色必须写 8 位 `#AARRGGBB` 才有透明度，只写 6 位等价于完全不透明（`CC`=80%、`80`=50%、`33`=20%）。
- 改 XAML 资源引用前须确认「存在 + 类型匹配」：`StaticResource` 引用不存在的资源会**启动即崩**，Style 名填进 `Foreground`（需 Brush）属类型错误。`StaticResource` 无法解析 `ThemeDictionaries` 内的资源 → 业务画刷定义在 App.xaml 顶层、主题字典之外。
- `WinUIEx` 已移除 → `AppWindow.SetIcon(string)`；Picker 用 `Microsoft.Windows.Storage.Pickers`（构造传 `WindowId`）。无 `RenderOptions.BitmapInterpolationMode`；缩放比取 `XamlRoot.RasterizationScale`。
- **`SoftwareBitmapSource` 实测不可用**：文档标注 Agile，但作为 XAML DependencyObject 实际有 UI 亲和——线程池创建挂视图树、乃至 UI 线程 `SetBitmapAsync` 都触发 `Microsoft.UI.Xaml.dll` fail-fast `0xC000027B`（绕过托管 UnhandledException，crash.log 无记录，去 Windows 事件日志 ID 1000 查故障模块）。`BitmapImage` 是唯一稳定显示管线。**动手前先查本记忆既有结论，文档元数据不可信。**
- unpackaged 应用要 Win11 圆角只能靠 `MicaBackdrop`（2.3.6 无 `TransparentBackdrop`），Mica 仅 Win11 生效；**PRI 不索引 `<Content>` 项** → 资源按 `AppContext.BaseDirectory` 磁盘路径加载。
- `ThemeShadow` + `Translation`：**z 是投影唯一输入**，z=0 几乎不可见；`Translation` 不参与布局。`Border.CornerRadius` 会裁剪子内容（含投影）→ 圆角图片交给 `Border.Background` 的 `ImageBrush`。
- 持有 `SemaphoreSlim`/`CTS` 等可释放字段的类型必须实现 `IDisposable`（CA1001 在 `-warnaserror` 下是错误）。

## WinUI 3 平台约束（实战）
- 分组 ListViewBase 配自定义 ItemsPanel 时排列的是 `GroupItem`；无内置 Justified 布局与 GridLength 动画。自定义标题栏用 `InputNonClientPointerSource` Passthrough。
- ItemsPanelTemplate 内不能 x:Bind 页面属性、不能 ElementName 跨 namescope；面板读子项数据走 `FrameworkElement.DataContext`。**x:Bind 默认 OneTime**，会变的须 `Mode=OneWay`；x:Bind OneWay 与 `x:Load` 只支持 Page/UserControl（非 Window），Window 层联动走代码后置 INPC 转发。
- ItemContainerStyle 模板内 x:Bind 根是模板化控件；页面属性须经 PageProxy（`Data="{x:Bind}"` 放 Page.Resources）用传统 Binding。**模板内 x:Bind 禁配 StaticResource Converter**（`LookupConverter` 运行时 NRE，编译期 0 警告、延迟数秒~数十秒崩溃）→ 条件显隐一律用 VisualState 状态机；**VisualState Setter 优先级高于本地绑定值**。
- 改控件外观优先覆盖主题资源（键名规律 `Xxx`/`XxxPointerOver`/`XxxFocused`/`XxxDisabled`），勿重写模板；给 `MenuFlyoutItem` 自定义模板会触发旋转忙碌光标；主题键覆盖勿放 `Style.Resources`。圆角两档：4（控件）/8（表面）。
- `MenuFlyout` 从 `Application.Current.Resources` 取出的是共享单例，重复 `ShowAt` 抛 `E_INVALIDARG` → 可重复弹出的菜单须工厂方法每次 `new`。
- `CommandBar` 动态溢出有未修 bug（issue #6450 not planned）→ 带 Flyout 的工具栏溢出只能手动实现（AdaptiveTrigger + VisualState）。
- 切换 ListViewBase 的 `SelectionMode` 会重置选择 → 先抓快照再恢复。间距由面板 `Spacing` 承担、子项模板零 Margin；嵌套 ScrollViewer 内的 GridView 须禁用自身垂直滚动；多实例 GridView 选择聚合须经实例列表（Loaded/Unloaded 登记）。
- `Page.KeyboardAccelerators` 会污染页面内所有 ToolTip（官方 by design）→ 用代码后置 KeyDown；菜单项 `KeyboardAcceleratorTextOverride` 是豁免用法。
- 符号字体码点（离屏渲染实证）：空心文件夹 `\uED25`、实心 FolderFill `\uE8B7`；线性星 `\uE734`/实心星 `\uE735`；空心爱心 `\uEB51`/实心 `\uEB52`（`Symbol.Favorite` 是爱心非星形）。查码点用 PowerShell + WPF `RenderTargetBitmap` 离屏渲染 PNG 目检。

## 卡死 / 冻结排查（判别式）
- **先分辨「慢」还是「冻结」**：若心跳（UI 线程）正常、日志持续增长、CPU 与线程池空闲 → 是慢不是卡死，此时查驱动/GPU/死锁全是浪费。判据优先级：先看心跳有无中断 → 再看日志有无产出 → 最后看 CPU。
- **三态判别式**：① CPU 单核 100% + 日志停滞 = 布局死循环；② CPU 高 + 日志持续增长 = 业务慢；③ CPU 增量 0 + 日志完全停滞 + 全线程 Wait + 窗口 Hung=False = 数据早已就绪而渲染停摆（③ 与 ① 处理方向相反，务必先看 CPU 增量）。死循环时托管堆栈为空、`crash.log` 常不留痕迹。
- **绝不让「缩略图/降采样位图的尺寸」参与任何驱动布局的属性**：位图按档位量化解码，宽高比有微小偏差，一旦覆盖已有准确值就形成「解码 → 比例抖动 → 重排 → 回写 → 再解码」的环。位图尺寸只能作兜底且优先级排最后。「宽高比是相对值所以用位图无害」是错误判断。
- **订阅/等待 `CompositionTarget.Rendering` 等渲染帧属高危**：会令合成呈现停摆（UI 线程存活、布局 pass 永久死亡、画面冻结在最后一帧、hover 无反应），错峰一律用 DispatcherQueue 优先级。
- **【已根治】图库点击冻结的根因（2026-09-02 定案）**：`GalleryViewModel.WaitForNextRenderFrameAsync` 订阅 `CompositionTarget.Rendering` 等一帧，该订阅与渲染 tick 抢占执行窗 → 合成呈现停摆。三组叠加减法实验实锤（掐断缩略图仍冻 → 降到 20 条仍冻 → 摘掉渲染帧等待即愈），该方法与两处调用已永久移除。**分水岭修正：是引入该机制的 `84c9e74`，不是此前误判的元数据回填提交 `e75be83`**（当时「连启 4 次稳定」属小样本误判）。期间另有两条被推翻的旧假设，勿再据此排查：旧 Intel 驱动、IO/磁盘瓶颈。
- **【已根治】LayoutCycle 事故的真因**：页面内 loading 覆盖层与内容 GridView **同格**时两者测量互相失效（该结构已移除），与窗口级 indeterminate 动画无关，详见下条。
- **loading 覆盖层约束**：必须在**窗口层（PageHost 兄弟位）**，不得与内容 GridView 同格；满足该前提时可安全使用 indeterminate 进度条；隐藏时必须把 `IsIndeterminate` 复位为 false。旧结论「窗口级 indeterminate 动画是布局刺激源」**已被实测推翻，勿再据此禁用**——包内模板的动画目标是 RenderTransform，不触发 Measure/Arrange；真因曾是「覆盖层与 GridView 同格」，该结构已移除。
- 取证手段：`dotnet-stack report` 抓托管栈判 UI 死/活；TICK 心跳间隙扫描判同步阻塞；diag.log 业务时序判管线进度（LOADTOTAL 出现而面板测量停止 = 加载全绿而布局死）。**布局/上屏跑在渲染 tick，与 DispatcherQueue 定时器是两条生命周期，判死必须分别取证**。多嫌疑时用**叠加减法实验**逐轮排除（每轮单变量）；时间戳对齐是循环取证核心。
- 「视觉死但日志活」= 布局系统坏死而非进程死，不能凭「进程 Responding」判断界面可用；「加载完成但长时间骨架屏」是缩略图并发排队（整页 N 条 ÷ 并发度 × 单条耗时可估算），不是卡死。
- 概率性缺陷被性能优化引爆是常态：不要回滚优化，去找被掩盖的根因；「连启 N 次稳定」对概率性触发属小样本误判。

## 异步与线程（稳定结论）
- **`ConfigureAwait(true)` 不是"回到 UI 线程"**：它恢复的是各 await 点当时捕获的上下文，中途任一 await 用 false 脱离后 `SynchronizationContext.Current` 变 null，后续 true 切不回（实测在线程池创建 BitmapImage 抛 0x8001010E，整页缩略图静默全灭）。跨线程回 UI 的唯一可靠手段：**服务经构造注入 DispatcherQueue + `TryEnqueue` + TaskCompletionSource（RunContinuationsAsynchronously）桥接**，async void 回调内异常必须收口到任务源。
- **绝不能 `EnqueueAsync(async () => await Xxx())` 而 Xxx 内部又 `EnqueueAsync`（自我死锁）**；批量加载循环必须 `await Task.WhenAll`；UI 状态赋值统一放进 `EnqueueAsync` 块。
- **"任务正常完成"≠"有效工作"**：WhenAll 完成但产出 0 = 全员静默失败。服务层 catch 加取证日志（异常类型 + **HResult**：WinRT 的 COMException 常无 Message），一次测试即可定案；单条 IO 必须有超时（`WaitAsync`），否则挂死任务占死信号量槽位令整条管线静默死亡。

## 验证手段与导航入口排查（可复用）
- **验证 UI 结果一律用截屏，不要靠 UIA 文本探测**：WinUI `TextBlock` 不把 `Text` 暴露为 UIA `Name`，按文本 `FindFirst` 恒定失败，会误判成「页面没打开」。可靠链路：`Start-Process` → `Interaction.AppActivate(pid)` 置前 → `Graphics.CopyFromScreen` 存 PNG → 读图目检；按钮点击可用 UIA `InvokePattern`（控件有 `AutomationProperties.Name` 时可按名定位，中文名在脚本里用 `[char]0xXXXX` 拼接以避开终端中文语法错误）。
- **「点了没反应」先查入口是否存在，再查事件与后台**：跳转常是「按 Tag 查找导航项 → 找不到就静默 return」。判据 `git log -S '<关键 Tag>'` 为空 = 功能从未接入（非回归）；若 XAML 关掉了内置入口（如 `IsSettingsVisible="False"`），自定义入口必须落在被查找的那个集合内。

## 图片显示与缩略图管线
- **WinUI 3 的 `Image`/`ImageBrush` 插值不可控且无 mipmap** → 唯一手段是让位图物理像素尽量等于显示区物理像素。排查顺序：显示尺寸 → DPI → 位图来源 → 插值算法 → 显示端插值（不可控）。
- 请求尺寸语义统一为「显示区最长边」；档位量化**先乘 `RasterizationScale` 再量化**；缩小 `Fant`、放大 `Cubic`（上限 2 倍）；升级加载只升不降 + 容差。**`BitmapImage` 绝不能同时设 `DecodePixelWidth` 与 `DecodePixelHeight`**（语义是拉伸），只设最长边一维。
- 尺寸探测：图片取 `BitmapDecoder.OrientedPixel*`（已计入 EXIF 方向）；视频取 `GetVideoPropertiesAsync()` 并按旋转标记交换宽高。自定义面板缩放过子项必须回写实际显示尺寸。
- **异步管线按线程亲和性切开**：中间产物用 `byte[]`，CPU 段限流放线程池，只在最后一跳回 UI 线程构造 `BitmapImage`，批量写回。整页提交时若续体轮番占用 UI 线程，会表现为「加载已完成但界面仍卡死」。
- 信号量只控并发数、不控「该不该做」；滚走取消 + 滚回重触发；删除「整页提交」兜底是高危操作。
- **`ContainerFromItem` 不能单独用作可见性判据**：虚拟化下「从未进入视口」与「曾进入视口后被回收」都返回 null，据此取消整页预取会让条目因在途标记已置位而拒绝重新发起 → 缩略图永不出现，表现为界面冻结且 CPU≈0。必须额外记录「是否曾生成过容器」（`ContainerContentChanging` 置位）。
- **读性能日志前先看计时起点**：`THUMB|...|elapsedMs` 含信号量排队（stopwatch 在 `_decodeGate.WaitAsync` 之前启动），200 条 ÷ 并发 4 ≈ 50 批排队，中位数可达 5s+；`THUMBWAIT` 显示 0ms 未必异常（整页提交时 `EnsureThumbnailAsync` 会因在途标记提前 return）。用这些值判「解码慢」常误判为算法问题，实际多为并发排队或磁盘 IO。
- 「分辨率」是绝对像素、「宽高比」是相对值，取值优先级不可共用。色彩链路（`ColorManageToSRgb` + `RespectExifOrientation` + `OrientedPixel*`）已验证正确，勿改。
- **磁盘缩略图缓存命中时会对源文件 stat**（取 mtime+size 比对条目头指纹）：它不是「展示信息」的取数，但每条未命中内存缓存的缩略图都会发生一次，在 HDD 上 200 条不可忽略。索引库已存 `file_size`/`modified_utc`，理论上可替代（权衡：库值在文件被外部修改后会过期）。

## 骨架屏三态展示
- `ThumbnailPresenter` + `ThumbnailLoadState` 三态，`ThumbnailState` 是唯一数据源。**取消 ≠ 失败**，必须回落 Loading。
- WinUI 3 XAML 无 `EventTrigger`/`BeginStoryboard` → 状态驱动动画选「模板化控件 + VisualState」；`RepeatBehavior=Forever` 容器回收后不自停，`Unloaded` 里回静态态。
- **`{TemplateBinding}` 是一次性求值**，运行期会变的属性须依赖属性回调写入；DataTemplate 重构为 ControlTemplate 时绑定机制静默改变，须逐个复核。
- **虚拟化容器回收复用不会重新应用模板**：`Unloaded` 改过的状态要在 `Loaded` 对称恢复；「上一次 X」字段须在 `DataContextChanged` 清除。
- **「内容可绘制」与「数据已就绪」是两个时刻**：可靠信号是 Image **控件级** `ImageOpened`。
- 动画异常排查顺序：就绪信号级别 → 状态被重置 → 跃迁判据（正向枚举）→ 二次换源 → 帧间隔 → 形状/位置 → 播放时机 → 时长/缓动。**先问「什么时候播」再问「怎么播」**；小元素动画 250ms 不足（本项目定 500ms）。

## 窗口视觉分层（背景图 + 玻璃卡片）
- 自下而上：全窗口 `Image` 背景（`Grid.RowSpan` 覆盖标题栏行+内容行，`UniformToFill`）→ 透明标题栏与 NavigationView → 内容区半透明卡片（`Border` + `ThemeShadow` + `Translation`）。
- 透出背景：NavigationView 的 `{Default,Expanded,Top}PaneBackground`、`NavigationViewContentBackground` 与自身 `Background` 全改 `Transparent`。死键：`NavigationViewPaneBackground`/`ContentBackground`/`Background`。
- 容器级圆角必须归零（`NavigationViewContentGridCornerRadius`）否则裁掉卡片投影；卡片圆角自己声明。卡片贴边 = `Margin="24,24,0,0"` + `CornerRadius="12,0,0,0"` + `BorderThickness="1,1,0,0"`。
- Pane 与内容区之间的竖线来自 `ContentGrid.BorderThickness.Left`；左栏圆角来自模板 `RightCornerRadiusFilterConverter`，元素级覆盖 `OverlayCornerRadius` 无效。内容区紧贴左栏时，「内容区左上圆角」与「左栏右上深色圆弧」是同一几何事实，要兼得只能留缝。
- NavigationView 纵向：Row0 `ContentTopPadding` → Row1 `HeaderContent`（MinHeight=36，可 `AlwaysShowHeader="False"` 消掉）→ Row2 `ContentPresenter`。

## 索引、元数据与展示取数
- 索引分两阶段：扫描只写文件属性（秒级，界面立即可浏览）；宽高与时长由后台回填服务分批补齐。探测状态三态的关键：失败必须落「已失败」，否则损坏文件每轮被反复捞取、回填永不收敛。写回只覆盖尺寸/时长列，收藏/评分/分类等用户数据一律 `COALESCE` 保护。
- **内容区展示的大小/日期/时长全部来自索引库**，不实时读文件系统；尺寸也以库中的宽高为准，仅当库缺宽高时才读文件头探测一次（回填完成后该集合为空）。代价：文件在库外被改动后界面显示的是上次扫描的快照，直到重新扫描。查看器的 EXIF 面板例外，走实时读文件。
- 库里的 `taken_utc` 是扫描时的文件系统时间（created 与 modified 中较早者），**不是 EXIF 拍摄时间**；EXIF 的 `TakenAt` 只在查看器解析，当前既未在界面展示、也未回写索引库。

## 产品/技术决策（已定，勿反复）
- 定位相册浏览器 → 砍掉 MagicScaler，Win2D 降为可选；优先「查看器两级加载 + 磁盘缩略图缓存」。**基线测量优先于选型**；可对标 ImageGlass / FlyPhotos，不可对标 Windows 照片应用（闭源管线）。
- 排序为 `MediaSortKey` × `SortDirection` 两维；随机排序用固定序列（`random_rank`）保证分页稳定，不能用 SQL `RANDOM()`。
- 删除文件走回收站（`RecycleBinHelper`，SHFileOperation + FOF_ALLOWUNDO）并同步清索引；「从索引移除」仅删记录。
- EXIF 拍摄时间暂不回填（本轮只补尺寸/时长）；排序键是否换成真实拍摄时间属产品语义决策，尚未拍板。
- 查看器绕过 `ThumbnailService` 全分辨率加载的问题已由**两级加载**解决（512 预览亚秒垫场 + 全图替换 + 装载序号防翻页串图）。

## 已落地项
- 缩略图磁盘缓存：两级哈希分桶（65536 桶）、条目头 16B 指纹（源 mtime.Ticks + size，读时校验、失配即删）、LRU 2GB（内存表启动扫描以缓存文件 LastWriteTimeUtc 重建访问序、命中节流 touch）、temp + 原子 Move、IO 失败全静默。已知项：百万级条目时内存表约 150MB，需紧凑化。
- **缓存两层语义必须分开**：`Invalidate` = 内容真失效（清内存 + 清磁盘），`Release` = 仅释放内存位图（保留磁盘）。切换视图/列表瘦身误用 `Invalidate` 会把磁盘缓存删光，缓存形同虚设（实测 5 轮切换清空全部条目）。
- 统一解码档位 512（请求档位 ≤512 一律按 512 解码/缓存/落盘）；分帧提交 + 撤层提前（首屏 60 条就绪即撤覆盖层，积压条后台渐进，每批校验 loadSequence）。
- 统计异步化（后台 COUNT 并行 + 代数校验）；排序键索引（Schema v3）与随机固定序列游标分页（Schema v4 `random_rank`）。

## 未做项评估定论（勿重复评估）
- A3 非随机排序游标：不做——2.5 万条下 OFFSET 是索引遍历（几十 ms），触发器为深翻实测 >200ms。
- E `_items` 滑动窗口：暂缓——触发条件是实测 `_items>2000` 且追加停顿可感（LOADTOTAL 埋点 items=N 可监控）；位图大头已由头部瘦身 300 条控制。
- S0 埋点清理（DISK/BITMAP/VIEWER/STAT 等）：发布前统一做，`Diagnostics.cs` 头部已注明。
- 机会性预取：永久搁置——写路径已覆盖浏览路径；全库预生成 3.5GB 超 LRU 上限且 HDD 磨损。
- SQL `OFFSET` 深翻页已由游标分页取代；新查询用 `EXPLAIN QUERY PLAN` 复核。

## 杂项
- 背景图固定 `light.jpg` 不随主题切换：深色主题下文字对比度不足（`dark.jpg` 已在 Assets 待用）。
- NuGet 审计：常规构建用 `WarningsNotAsErrors` 豁免 NU19xx，`-p:AuditPipeline=true` 的审计流水线才升级为错误。
- XamlCompiler 生成代码（.g.cs 的 x:Bind 方法签名）缓存旧类型元数据：改 VM 属性类型后报 CS1503 时 `dotnet clean` 即解（OneDrive 下删 obj 会被安全删除工具拦截）。
- HEIC/AVIF 依赖 WIC 编解码器扩展。
