# 长期记忆

> 只收规范、稳定事实与可复用方法论；不收代码定义、具体数值、一次性排障流水（那些进当日日志）。

## 项目与开发环境
- Pixbian：WinUI 3 本地相册浏览器（非编辑器）。WASDK 2.4.0 元包（WinUI 实为 2.3.6）+ `net8.0-windows10.0.26100.0`，最低 17763。测试基线 181（Core 136 / WebServer 33 / Imaging 12）。
- `dotnet` 不在 PATH，用 `C:\Program Files\dotnet\dotnet.exe`；包管理一律 pnpm；构建须 `-warnaserror`（0 警告）；缩进 4 空格；文件头 3–8 行中文模块说明。
- 硬件：C: SSD；D: 机械盘，媒体库 `D:\Downloads\*`。D: 余量长期偏低 → 查「慢/卡」前先看余量；HDD 随机读 1MB ≈105ms，**性能结论须在此盘实测**。
- OneDrive 工作区：新产物落盘后可能被锁定 → 一键脚本用「显式 build + Start-Process」两段式；构建前确认应用未运行（MSB3026）。
- 诊断脚本放 `C:\Temp\`；含中文 `.ps1` 须 UTF-8 with BOM；**终端传入含中文的命令会语法错误**（诊断命令一律纯英文）；GBK 乱码 ≠ 程序字符串有误。
- 系统还原通道失效 → 系统级变更前 `pnputil /export-driver` 导出驱动包回退。嵌套 `powershell -Command` 吞噬内层 `$var`/`$_`，提权脚本 stdout 不回传 → 写成 `.ps1` 并在脚本内落日志。

## 分层与依赖方向（改动前必查）
- Core 最底层、**零项目引用**、纯 `net8.0`；`Data`/`Imaging`/`Media`/`WebServer` 单向引用 Core，`Pixbian`(UI) 引用全部。
- Core 禁用 WIC / `Windows.Graphics.Imaging`（会绑 windows TFM，破坏 Core.Tests）→ Core 定抽象 + UI 注入实现；跨层数据走 `Pixbian.Core.Models`，禁泄漏基础设施类型。

## 编码与协作规范
- 敏感信息禁止硬编码；API 响应 DTO 白名单过滤；日志脱敏（WinRT 异常记 **HResult**）。
- 改动 > 5 文件须先弹窗确认是否 commit（Conventional Commits）；> 2 文件的大改先取得用户确认方案。
- 每次修改须记当日 `.codebuddy/memory/YYYY-MM-DD.md`（超 1000 行分卷）。非用户要求不主动写记忆。

## 通用工程方法论
- **性能定位顺序：先测真实数据规模 → 再测单点耗时 → 最后改代码**（用户口述规模必须实测）。
- 后台任务**让出比例比绝对时长更关键**（批次 2.5s 时节流 ≥1.5s）并设批次数上限；常驻任务须**节流 + 排他**，多入口收口到同一把锁；排他优先 `Interlocked.CompareExchange`（持 CTS 字段触发 CA1001，`-warnaserror` 下是错误）。
- **查 API 是否存在一律读包内二进制**：WinRT 投影 `microsoft.windows.sdk.net.ref/<ver>/winmd/`；WinUI 组件 `Microsoft.WinUI.dll`（`.xml` 的 `T:`/`P:`/`M:` 索引最精确）；主题键与模板默认值读 `Themes/generic.xaml`。Learn 的 WinRT 页会写错；超长页面勿用 web_fetch；winmd 不可 `Assembly.LoadFrom`。
- 工具事实：WAL 库用 `SqliteOpenMode.ReadWrite` 可与运行中应用并发读；`search_content` 的 `glob` 不支持 `!` 取反（用 `git check-ignore -v`）；查 MSBuild 属性 `dotnet msbuild x.csproj -getProperty:名`；`dotnet-stack report` 打运行中进程托管栈；`dotnet-dump analyze` 对大转储极慢。

## WinUI 3 / WASDK 关键事实
- XAML 编译器路径须用 `PkgMicrosoft_WindowsAppSDK_WinUI`（旧键静默失败 → 全项目 CS0103）；TFM 升 26100 不需装 SDK 26100。
- **C# 错误会连锁引发 XAML "Unknown type" 假错误**，先修 CS；VS Code 的 `.g.i.cs` 误报 CS0103 属固有限制，**以 `dotnet build` 为准**；勿动 `BaseIntermediateOutputPath`、勿删 `obj\`。
- XAML 编译期**不校验颜色字面量**（`##RRGGBB` 能 0 警告构建、运行时崩 `0xC000027B`）；「构建成功+启动崩溃」先 `git status` 全量排查；颜色须 8 位 `#AARRGGBB` 才有透明度。
- `StaticResource` 引用不存在的资源**启动即崩**且须类型匹配；无法解析 `ThemeDictionaries` 内资源 → 业务画刷定义在 App.xaml 顶层字典之外。
- `WinUIEx` 已移除 → `AppWindow.SetIcon(string)`；Picker 用 `Microsoft.Windows.Storage.Pickers`（构造传 `WindowId`）；无 `RenderOptions.BitmapInterpolationMode`；缩放比取 `XamlRoot.RasterizationScale`。
- **`SoftwareBitmapSource` 实测不可用**（有 UI 亲和，触发 `Microsoft.UI.Xaml.dll` fail-fast `0xC000027B`，绕过托管异常处理 → 查事件日志 ID 1000）。`BitmapImage` 是唯一稳定显示管线。
- unpackaged 应用要 Win11 圆角只能靠 `MicaBackdrop`（2.3.6 无 `TransparentBackdrop`）；**PRI 不索引 `<Content>` 项** → 资源按 `AppContext.BaseDirectory` 磁盘路径加载。
- `ThemeShadow` + `Translation`：z 是投影唯一输入；`Translation` 不参与布局；`Border.CornerRadius` 会裁掉子内容投影 → 圆角图片交给 `Border.Background` 的 `ImageBrush`。
- 持有 `SemaphoreSlim`/`CTS` 等可释放字段的类型必须实现 `IDisposable`（CA1001 在 `-warnaserror` 下是错误）。

## 控件与布局约束
- 分组 ListViewBase 配自定义 ItemsPanel 时排列的是 `GroupItem`；无内置 Justified 布局与 GridLength 动画；自定义标题栏用 `InputNonClientPointerSource` Passthrough。
- ItemsPanelTemplate 内不能 x:Bind 页面属性、不能 ElementName 跨 namescope；面板读子项数据走 `FrameworkElement.DataContext`。**x:Bind 默认 OneTime**（会变的须 `Mode=OneWay`）；OneWay 与 `x:Load` 只支持 Page/UserControl，Window 层联动走代码后置 INPC 转发。
- ItemContainerStyle 模板内 x:Bind 根是模板化控件；页面属性须经 PageProxy（`Data="{x:Bind}"` 放 Page.Resources）用传统 Binding。**模板内 x:Bind 禁配 StaticResource Converter**（运行时 NRE、编译期 0 警告）→ 条件显隐一律用 VisualState；**VisualState Setter 优先级高于本地绑定值**。
- 改控件外观优先覆盖主题资源（键名 `Xxx`/`XxxPointerOver`/`XxxFocused`/`XxxDisabled`），勿重写模板；给 `MenuFlyoutItem` 自定义模板会触发旋转忙碌光标；主题键覆盖勿放 `Style.Resources`。圆角两档：4（控件）/8（表面）。
- `MenuFlyout` 从 `Application.Current.Resources` 取出是共享单例，重复 `ShowAt` 抛 `E_INVALIDARG` → 可重复弹出的菜单用工厂方法每次 `new`。`CommandBar` 动态溢出有未修 bug（issue #6450）→ 带 Flyout 的工具栏溢出手动实现（AdaptiveTrigger + VisualState）。
- 切换 `SelectionMode` 会重置选择 → 先抓快照再恢复。间距由面板 `Spacing` 承担、子项模板零 Margin；嵌套 ScrollViewer 内的列表须禁用自身垂直滚动；多实例 GridView 选择聚合须经实例列表（Loaded/Unloaded 登记）。
- `Page.KeyboardAccelerators` 会污染页面内所有 ToolTip（官方 by design）→ 用代码后置 KeyDown；菜单项 `KeyboardAcceleratorTextOverride` 是豁免用法。
- 符号字体码点（离屏渲染实证）：空心文件夹 `\uED25` / 实心 `\uE8B7`；线星 `\uE734` / 实心 `\uE735`；空心爱心 `\uEB51` / 实心 `\uEB52`（`Symbol.Favorite` 是爱心非星）。查码点用 PowerShell + WPF `RenderTargetBitmap` 离屏渲染 PNG 目检。
- `Expander` 嵌卡片须在 `.Resources` 里把三个 `Expander*BorderBrush` 与两个 BorderThickness 归零、Background 指 `SubtleFillColorTransparentBrush`（默认描边恰为 `CardStrokeColorDefaultBrush`，嵌套必现「卡中卡」）。
- `ToggleSwitch` 默认 `MinWidth=154px` → 纯开关贴右须显式 `MinWidth="0"`；`OffContent`/`OnContent` 固定显示在开关右侧，状态文字要在左侧就另放 `TextBlock`。
- `Border` 是 `Decorator` 只能有一个 `Child`（多项并列报 `WMC0035`）；Grid `Auto` 列内子元素默认左对齐 → 行末右对齐须显式 `Right`；Button 想「常态透明+有 hover」不能写元素上的 `Background="Transparent"`（本地值压过 VisualState Setter）→ 在其 `.Resources` 覆盖 `ButtonBackground`/`PointerOver`/`Pressed` 三键。
- XAML 注释内不得出现连续 `--`；WinUI 3 的 `Grid` 支持 `Padding`；`NumberBox` 清空时 `Value` 为 `NaN`，写回设置前须拦截。

## 动画与交互
- Storyboard 优先代码后置现场创建并以元素对象为目标（Resources 里 XAML Storyboard 的 `TargetName` 解析失败即静默无动画）；`FillBehavior` 默认 `HoldEnd` → 每轮动画前必须复位起始值。
- 数据驱动动画须防重复触发；快速连发时上一轮 Completed 可能清掉下一轮的源，需容忍缺失。

## 卡死 / 冻结排查（判别式）
- **先分辨「慢」还是「冻结」**，判据优先级：心跳中断 → 日志产出 → CPU。三态：① CPU 单核 100% + 日志停滞 = 布局死循环；② CPU 高 + 日志持续增长 = 业务慢；③ CPU 增量 0 + 日志停滞 + 全线程 Wait + Hung=False = 渲染停摆（与 ① 相反，先看 CPU 增量）。死循环时托管栈为空、`crash.log` 常无痕。
- 绝不让「位图尺寸」参与任何驱动布局的属性（解码→比例抖动→重排→回写 的环），只能兜底且优先级排最后。
- 订阅 `CompositionTarget.Rendering` 等渲染帧属高危（合成停摆、布局 pass 死亡、hover 无反应），错峰一律用 DispatcherQueue 优先级。**【已根治】图库点击冻结即此因**（方法与调用已移除）；两条已推翻的旧假设勿再据此排查：旧 Intel 驱动、IO/磁盘瓶颈。
- **【已根治】LayoutCycle 真因**：loading 覆盖层与内容 GridView 同格时测量互相失效（该结构已移除）；覆盖层须在窗口层（PageHost 兄弟位），隐藏时把 `IsIndeterminate` 复位 false。
- 取证：`dotnet-stack report` 判 UI 死活；TICK 心跳间隙扫描判同步阻塞；diag.log 判管线进度。布局/上屏与 DispatcherQueue 定时器是两条生命周期，判死须分别取证；多嫌疑用叠加减法实验逐轮排除。
- 「视觉死但日志活」= 布局系统坏死而非进程死；「加载完成但长时间骨架屏」= 缩略图并发排队。概率性缺陷被性能优化引爆是常态，不要回滚优化，去找被掩盖的根因。

## 异步与线程
- `ConfigureAwait(true)` 不是「回到 UI 线程」（中途任一 await 用 false 后 `SynchronizationContext.Current` 变 null，后续切不回；实测线程池创建 BitmapImage 抛 0x8001010E，整页缩略图静默全灭）。跨线程回 UI 唯一可靠手段：注入 DispatcherQueue + `TryEnqueue` + TCS（`RunContinuationsAsynchronously`）桥接；async void 回调异常须收口到任务源。
- 绝不能 `EnqueueAsync(async () => await Xxx())` 而 Xxx 内部又 `EnqueueAsync`（自我死锁）；批量加载必须 `await Task.WhenAll`；UI 状态赋值统一放进 `EnqueueAsync` 块。
- 「任务正常完成」≠「有效工作」（WhenAll 完成但产出 0 = 全员静默失败）；服务层 catch 加取证日志（类型 + HResult）；单条 IO 必须有超时（`WaitAsync`），否则挂死任务占死信号量槽位令整条管线静默死亡。

## 验证手段 / 入口排查
- **验证 UI 一律用截屏，不靠 UIA 文本探测**（WinUI `TextBlock` 不把 `Text` 暴露为 UIA `Name`）。链路：`Start-Process` → `Interaction.AppActivate(pid)` → `Graphics.CopyFromScreen` 存 PNG → 目检；按钮可用 UIA `InvokePattern`（中文名用 `[char]0xXXXX` 拼接）。
- 「点了没反应」先查入口是否存在（跳转常是「按 Tag 查导航项 → 找不到静默 return」）；`git log -S '<Tag>'` 为空 = 功能从未接入；XAML 关掉内置入口时自定义入口必须落在被查找的集合内。

## 图片显示与缩略图管线
- WinUI 3 的 `Image`/`ImageBrush` 插值不可控且无 mipmap → 唯一手段是位图物理像素≈显示区物理像素。排查顺序：显示尺寸 → DPI → 位图来源 → 插值算法 → 显示端插值。
- 请求尺寸语义统一为「显示区最长边」；档位量化先乘 `RasterizationScale` 再量化；缩小 `Fant`、放大 `Cubic`（上限 2 倍）；升级加载只升不降 + 容差。**`BitmapImage` 绝不能同时设 `DecodePixelWidth` 与 `DecodePixelHeight`**（语义是拉伸）。
- 尺寸探测：图片 `BitmapDecoder.OrientedPixel*`（已计 EXIF）；视频 `GetVideoPropertiesAsync()` + 按旋转标记交换宽高；自定义面板缩放过子项须回写实际显示尺寸。
- 异步管线按线程亲和性切开：中间产物 `byte[]`，CPU 段线程池限流，只在最后一跳回 UI 线程构造 `BitmapImage`。信号量只控并发数不控「该不该做」；滚走取消 + 滚回重触发。
- **`ContainerFromItem` 不能单独用作可见性判据**（虚拟化下「从未进视口」与「回收后」都返回 null → 据此取消会让条目因在途标记已置位而拒绝重发，缩略图永不出现）→ 须额外记录「是否曾生成过容器」。
- 读性能日志先看计时起点（`THUMB|elapsedMs` 含量子排队；`THUMBWAIT` 0ms 未必异常），据此判「解码慢」常误判，实际多为并发排队或磁盘 IO。
- 「分辨率」是绝对像素、「宽高比」是相对值，优先级不可共用；色彩链路（`ColorManageToSRgb` + `RespectExifOrientation` + `OrientedPixel*`）已验证正确，勿改。
- 磁盘缓存命中时会对源文件 stat（HDD 上 200 条不可忽略），索引库已存 `file_size`/`modified_utc` 可替代（权衡：外部修改后库值过期）。

## 骨架屏三态
- `ThumbnailPresenter` + `ThumbnailLoadState` 三态，`ThumbnailState` 是唯一数据源。**取消 ≠ 失败**，必须回落 Loading。
- WinUI 3 XAML 无 `EventTrigger`/`BeginStoryboard` → 状态驱动动画选「模板化控件 + VisualState」；`RepeatBehavior=Forever` 容器回收后不自停，`Unloaded` 里回静态态。
- `{TemplateBinding}` 一次性求值，运行期会变的属性须依赖属性回调写入；DataTemplate 重构为 ControlTemplate 时绑定机制静默改变，须逐个复核。
- 容器回收复用不会重新应用模板：`Unloaded` 改过的状态在 `Loaded` 对称恢复；「上一次 X」字段在 `DataContextChanged` 清除。
- 「内容可绘制」与「数据已就绪」是两个时刻，可靠信号是 Image **控件级** `ImageOpened`。
- 动画异常排查顺序：就绪信号级别 → 状态被重置 → 跃迁判据 → 二次换源 → 帧间隔 → 形状/位置 → 播放时机 → 时长/缓动；先问「什么时候播」再问「怎么播」（小元素 250ms 不足，本项目 500ms）。

## 窗口视觉分层
- 自下而上：全窗口 `Image` 背景（`UniformToFill`，`Grid.RowSpan` 覆盖标题栏行+内容行）→ 透明标题栏与 NavigationView → 内容区半透明卡片（`Border` + `ThemeShadow` + `Translation`）。
- 透出背景：改 `{Default,Expanded,Top}PaneBackground`、`NavigationViewContentBackground` 与自身 `Background` 为 Transparent（死键：`NavigationViewPaneBackground`/`ContentBackground`/`Background`）。
- 容器级圆角须归零（`NavigationViewContentGridCornerRadius`）否则裁掉卡片投影；卡片圆角自己声明。Pane 与内容区竖线来自 `ContentGrid.BorderThickness.Left`；左栏圆角来自模板 `RightCornerRadiusFilterConverter`，元素级覆盖 `OverlayCornerRadius` 无效。
- NavigationView 纵向：Row0 `ContentTopPadding` → Row1 `HeaderContent`（MinHeight=36，可 `AlwaysShowHeader="False"` 消掉）→ Row2 `ContentPresenter`。

## 索引、元数据与展示取数
- 索引两阶段：扫描只写文件属性（秒级可浏览），宽高时长由后台分批回填。探测三态关键：失败必须落「已失败」，否则损坏文件反复捞取、回填永不收敛；写回只覆盖尺寸/时长列，用户数据用 `COALESCE` 保护。
- 内容区展示的大小/日期/时长全部来自索引库，不实时读文件系统（仅缺宽高时读文件头探测一次）；查看器 EXIF 面板例外。
- 库里 `taken_utc` 是扫描时的文件系统时间（created/modified 较早者），**不是 EXIF 拍摄时间**；EXIF `TakenAt` 只在查看器解析，既未展示也未回写索引库。

## 产品 / 技术决策（已定）
- 定位相册浏览器 → 砍 MagicScaler，Win2D 降可选；优先「查看器两级加载 + 磁盘缩略图缓存」；基线测量优先于选型。
- 排序为 `MediaSortKey` × `SortDirection` 两维；随机排序用固定序列（`random_rank`）保证分页稳定，不用 SQL `RANDOM()`。
- 删除走回收站（`RecycleBinHelper`）并同步清索引；「从索引移除」仅删记录。EXIF 拍摄时间暂不回填（是否改排序键未拍板）。

## 已落地项 / 未做项定论
- 落地：缩略图磁盘缓存（两级哈希分桶、条目头指纹、LRU、temp+原子 Move，IO 失败全静默）、统一解码档位、统计异步化、排序键索引 + 随机游标分页。
- **缓存两层语义必须分开**：`Invalidate` = 内容真失效（清内存+磁盘），`Release` = 仅释放内存位图；误用 `Invalidate` 会删光磁盘缓存。
- 未做（勿重复评估）：非随机排序游标（2.5 万条下 OFFSET 是索引遍历几十 ms）；`_items` 滑动窗口（触发条件苛刻，LOADTOTAL 可监控）；埋点清理（发布前统一做）；机会性预取（永久搁置：全库预生成超 LRU 上限且磨损 HDD）。新查询用 `EXPLAIN QUERY PLAN` 复核。

## 杂项
- 背景图固定 `light.jpg`（`dark.jpg` 待用）；压在背景图上的卡片须用 `CardBackgroundFillColorDefaultBrush` 一类 ThemeResource 随主题反转。
- NuGet 审计：常规构建用 `WarningsNotAsErrors` 豁免 NU19xx，`-p:AuditPipeline=true` 才升级为错误。
- XamlCompiler 缓存旧类型元数据：改 VM 属性类型报 CS1503 时 `dotnet clean` 即解（OneDrive 下删 obj 会被拦截）。HEIC/AVIF 依赖 WIC 编解码器扩展。
