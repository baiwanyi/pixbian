# 长期记忆

> 只收规范、稳定事实与可复用方法论；不收代码定义、具体数值、一次性排障流水（那些进当日日志）。
> 2026-09-06 精简重写：合并同类项、剔除冗余表述，技术结论与判别式全部保留。

## 项目与开发环境
- Pixbian：WinUI 3 本地相册浏览器（非编辑器）。WASDK 2.4.0 元包（WinUI 实为 2.3.6）+ `net8.0-windows10.0.26100.0`，最低 17763。测试基线 181（Core 136 / WebServer 33 / Imaging 12）。
- `dotnet` 不在 PATH，用 `C:\Program Files\dotnet\dotnet.exe`；包管理一律 pnpm；构建须 `-warnaserror`（0 警告）；缩进 4 空格；文件头 3–8 行中文模块说明。
- 硬件：C: SSD；D: 机械盘，媒体库 `D:\Downloads\*`，余量长期偏低 → 查「慢/卡」前先看 D: 余量；HDD 随机读 1MB ≈105ms，性能结论须在此盘实测。
- OneDrive 工作区：新产物落盘可能被锁（重建后约 30s 内启动会闪退）→ 一键脚本用「显式 build + Start-Process」两段式；构建前确认应用未运行（MSB3026）。
- 诊断脚本放 `C:\Temp\`；含中文 `.ps1` 须 UTF-8 with BOM；**终端传入含中文的命令会语法错误**（诊断命令与 commit 信息一律纯英文或 `git commit -F <UTF-8 文件>`）；GBK 乱码 ≠ 程序字符串有误。
- 系统还原通道失效 → 系统级变更前 `pnputil /export-driver` 导出驱动包。嵌套 `powershell -Command` 吞噬内层 `$var`/`$_`，提权脚本 stdout 不回传 → 写成 `.ps1` 并在脚本内落日志。

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
- **查 API 是否存在一律读包内二进制**：WinRT 投影 `microsoft.windows.sdk.net.ref/<ver>/winmd/`；WinUI 组件 `Microsoft.WinUI.dll`（配套 `.xml` 的 `T:`/`P:`/`M:` 索引最精确）；主题键与模板默认值读 `Themes/generic.xaml`。Learn 的 WinRT 页会写错；超长页面勿用 web_fetch；winmd 不可 `Assembly.LoadFrom`。
- **按行号批量改多个区间必须降序（从后往前）**，否则前一组插入/删除让后续区间整体错位；撤销脚本同样易错、会二次扩大损坏 → 机械重排优先整文件重写（先备份）。
- 工具事实：WAL 库用 `SqliteOpenMode.ReadWrite` 可与运行中应用并发读；`search_content` 的 `glob` 不支持 `!` 取反（用 `git check-ignore -v`）；查 MSBuild 属性 `dotnet msbuild x.csproj -getProperty:名`；`dotnet-stack report` 打运行中进程托管栈；`dotnet-dump analyze` 对大转储极慢。

## WinUI 3 / WASDK 关键事实
- XAML 编译器路径须用 `PkgMicrosoft_WindowsAppSDK_WinUI`（旧键静默失败 → 全项目 CS0103）；TFM 升 26100 不需装 SDK 26100。
- **C# 错误会连锁引发 XAML "Unknown type" 假错误**，先修 CS；VS Code 的 `.g.i.cs` 误报 CS0103 属固有限制，**以 `dotnet build` 为准**；勿动 `BaseIntermediateOutputPath`、勿删 `obj\`。
- XAML 编译期**不校验颜色字面量**（`##RRGGBB` 能 0 警告构建、运行时崩 `0xC000027B`）；「构建成功+启动崩溃」先 `git status` 全量排查；颜色须 8 位 `#AARRGGBB` 才有透明度。
- 命名空间事实：颜色常量在 **`Microsoft.UI.Colors`**（不存在 `Windows.UI.Colors`）；无 `Microsoft.UI.Core`（虚拟键状态用 `Windows.UI.Core.CoreVirtualKeyStates` + `Microsoft.UI.Input.InputKeyboardSource`）；`WinUIEx` 已移除 → `AppWindow.SetIcon(string)`；Picker 用 `Microsoft.Windows.Storage.Pickers`（构造传 `WindowId`）；无 `RenderOptions.BitmapInterpolationMode`；缩放比取 `XamlRoot.RasterizationScale`。
- `StaticResource` 引用不存在的资源**启动即崩**且须类型匹配；**StaticResource 无法解析 ThemeDictionaries 内资源** → 业务画刷一律 `ThemeResource`。
- **`SoftwareBitmapSource` 实测不可用**（UI 亲和 → `Microsoft.UI.Xaml.dll` fail-fast `0xC000027B`，绕过托管异常处理 → 查事件日志 ID 1000）。`BitmapImage` 是唯一稳定显示管线。
- unpackaged 应用要 Win11 圆角只能靠 `MicaBackdrop`（2.3.6 无 `TransparentBackdrop`）；**PRI 不索引 `<Content>` 项** → 资源按 `AppContext.BaseDirectory` 磁盘路径加载。
- `ThemeShadow` + `Translation`：z 是投影唯一输入；`Translation` 不参与布局；`Border.CornerRadius` 会裁掉子内容投影 → 圆角图片交给 `Border.Background` 的 `ImageBrush`。
- 持有 `SemaphoreSlim`/`CTS` 等可释放字段的类型必须实现 `IDisposable`（CA1001 在 `-warnaserror` 下是错误）；`DispatcherQueueTimer` 不在此列。
- **延伸标题栏（`ExtendsContentIntoTitleBar=true`）后，系统最小化/最大化/关闭按钮前景色不随应用主题更新**：必须显式设 `AppWindow.TitleBar.Button{Foreground,Background,Inactive*}Color`，并在「设置切换」与 `ActualThemeChanged` 两条路径各刷一次。
- 元素外观若需「运行时覆盖 + 退出还原」，初值必须写在 **Style Setter** 而非元素本地值，退出时 `ClearValue` 回落（直接赋值会压掉 `ThemeResource` 而丢失主题随动）。
- **绝不在运行时把页面宿主（`PageHost`）搬进另一个容器**：搬运触发 `Page.Unloaded`，播放器页的 `Unloaded` 负责释放 `MediaPlayer`，等于一进入播放就销毁播放器。

## 虚拟化（结论已实测，勿再试错）
- **唯一公开扩展点是 `ItemsRepeater` + `VirtualizingLayout`。** `IScrollInfo` 未公开 → 自定义 `VirtualizingPanel` 作 GridView.ItemsPanel 不可行。
- 本版本**无 `SelectionModel`** → 换 `ItemsRepeater` 必须自建选择服务（最大成本）。
- **方向相反，切勿套错**：GridView **不能**外层包 ScrollViewer；ItemsRepeater **必须**外层包 ScrollViewer（靠它算 `RealizationRect`）。
- `VirtualizingLayoutContext` 可用：`ItemCount`、`RealizationRect`、`VisibleRect`、可写 `LayoutOrigin`、`RecommendedAnchorIndex`、`GetItemAt(i)`、`GetOrCreateElementAt(i,opts)`、`RecycleElement(el)`；可重写 `MeasureOverride`/`ArrangeOverride`/`InitializeForContextCore`/`OnItemsChangedCore`。
- 变高布局（Justified）虚拟化先建「行偏移表 + 每行起始索引」，二分查可见行区 → O(log n)；未测量行用估算行高参与 Extent。
- `ItemsWrapGrid` 支持 `ItemWidth`/`ItemHeight`；本项目方形视图用自绘 `SquarePanel`（行填满）、自适应视图用 `JustifiedPanel`，均不做虚拟化，靠分页增量控制规模。

## 图库列表性能（定论）
- 「随条目数变卡」**主因是解码提交数随页数线性放大**：`pending` 取全集合 × 位图按索引瘦身置空 = 自激循环（翻 10 页一次提交约 1900 条）。
- 三条铁律：**容器数恒定**（≤ 视口+2 屏）；**解码请求数 = O(视口)**；**内存按 LRU/字节回收**而非按索引（被淘汰须与「从未加载」区分，否则释放/重解循环）。
- 反模式：`ContainerFromItem` 全集合扫描取消 = O(n²)；measure 内发起解码或重建订阅集合 = O(n)；解码管线上的同步日志（`File.AppendAllText` + 全局锁）会串行化所有解码线程。
- **`ContainerFromItem` 不能单独作可见性判据**（「从未进视口」与「回收后」都返回 null）→ 须额外记录「是否曾生成过容器」。
- 完整方案见 `docs/图库列表性能优化方案.md`。对标：**主参考 Windows 照片应用**（同栈同版式）；**不参考 Lightroom**（其性能建立在「导入期生成智能预览」的预算上）。

## 控件与布局约束
- 分组 ListViewBase 配自定义 ItemsPanel 时排列的是 `GroupItem`；无内置 Justified 布局与 GridLength 动画；自定义标题栏用 `InputNonClientPointerSource` Passthrough（矩形为物理像素须乘 `RasterizationScale`，布局/激活变化后重注册）。
- ItemsPanelTemplate 内不能 x:Bind 页面属性、不能 ElementName 跨 namescope；面板读子项数据走 `FrameworkElement.DataContext`。**x:Bind 默认 OneTime**（会变的须 `Mode=OneWay`）；OneWay 与 `x:Load` 只支持 Page/UserControl，Window 层联动走代码后置 INPC 转发。
- ItemContainerStyle 模板内 x:Bind 根是模板化控件；页面属性须经 PageProxy（`Data="{x:Bind}"` 放 Page.Resources）。**模板内 x:Bind 禁配 StaticResource Converter**（运行时 NRE、编译期 0 警告）→ 条件显隐用 VisualState；**VisualState Setter 优先级高于本地绑定值**（故「常态透明 + hover」只能靠 `.Resources` 覆盖 `ButtonBackground`/`PointerOver`/`Pressed` 三键，元素上写 `Background="Transparent"` 会让 hover 失效）。
- 改控件外观优先覆盖主题资源（键名 `Xxx`/`XxxPointerOver`/`XxxFocused`/`XxxDisabled`），勿重写模板；给 `MenuFlyoutItem` 自定义模板会触发旋转忙碌光标；主题键覆盖勿放 `Style.Resources`。圆角两档：4（控件）/ 8（表面）。
- `MenuFlyout` 从 `Application.Current.Resources` 取出是共享单例，重复 `ShowAt` 抛 `E_INVALIDARG` → 可重复弹出的菜单用工厂方法每次 `new`。`CommandBar` 动态溢出有未修 bug（issue #6450）。
- 切换 `SelectionMode` 会重置选择 → 先抓快照再恢复；间距由面板 `Spacing` 承担、子项模板零 Margin；嵌套 ScrollViewer 内的列表须禁用自身垂直滚动；多实例 GridView 选择聚合须经实例列表（Loaded/Unloaded 登记）。
- `Page.KeyboardAccelerators` 会污染页面内所有 ToolTip（官方 by design）→ 用代码后置 KeyDown；菜单项 `KeyboardAcceleratorTextOverride` 是豁免用法。
- 符号字体码点（离屏渲染实证）：空心文件夹 `\uED25` / 实心 `\uE8B7`；线星 `\uE734` / 实心 `\uE735`；空心爱心 `\uEB51` / 实心 `\uEB52`（`Symbol.Favorite` 是爱心非星）；鼠标 `\uE962`、照片 `\uE8B9`。查码点用 PowerShell + WPF `RenderTargetBitmap` 离屏渲染 PNG 目检（`New-Object -ArgumentList` 不解析 `[Type]::Member` 字符串，须先求值到变量）。
- `Expander` 嵌卡片须在 `.Resources` 把三个 `Expander*BorderBrush` 与两个 BorderThickness 归零、Background 指 `SubtleFillColorTransparentBrush`（默认描边恰为 `CardStrokeColorDefaultBrush`）。
- `ToggleSwitch` 默认 `MinWidth=154px`；`Border` 是 `Decorator` 只能一个 `Child`（多项并列报 `WMC0035`）；Grid `Auto` 列内子元素默认左对齐；XAML 注释内不得出现连续 `--`；WinUI 3 的 `Grid` 支持 `Padding`；`NumberBox` 清空时 `Value` 为 `NaN`。

## 动画与交互
- Storyboard 优先代码后置现场创建并以元素对象为目标（Resources 里 XAML Storyboard 的 `TargetName` 解析失败即静默无动画）；`FillBehavior` 默认 `HoldEnd` → 每轮动画前复位起始值。
- 数据驱动动画须防重复触发；快速连发时上一轮 Completed 可能清掉下一轮的源，需容忍缺失。
- **「控件自动隐藏」一律用 `DispatcherQueue.CreateTimer()`**（Tick 在 UI 线程），绝不订阅 `CompositionTarget.Rendering`。

## 卡死 / 冻结排查（判别式）
- **先分辨「慢」还是「冻结」**，判据优先级：心跳中断 → 日志产出 → CPU。三态：① CPU 单核 100% + 日志停滞 = 布局死循环；② CPU 高 + 日志持续增长 = 业务慢；③ CPU 增量 0 + 日志停滞 + 全线程 Wait = 渲染停摆。死循环时托管栈为空、`crash.log` 常无痕。
- 绝不让「位图尺寸」参与任何驱动布局的属性（解码→比例抖动→重排→回写 的环）。
- 订阅 `CompositionTarget.Rendering` 等渲染帧属高危（合成停摆、布局 pass 死亡、hover 无反应）——**【已根治】图库点击冻结即此因**，方法与调用已移除；两条已推翻的旧假设：旧 Intel 驱动、IO/磁盘瓶颈。
- **【已根治】LayoutCycle 真因**：loading 覆盖层与内容 GridView 同格时测量互相失效（结构已移除）；覆盖层须在窗口层（PageHost 兄弟位），隐藏时复位 `IsIndeterminate=false`。
- 取证：`dotnet-stack report` 判 UI 死活；TICK 心跳间隙扫描判同步阻塞；diag.log 判管线进度。布局/上屏与 DispatcherQueue 定时器是两条生命周期，判死须分别取证；多嫌疑用叠加减法实验逐轮排除。
- 「视觉死但日志活」= 布局系统坏死而非进程死；概率性缺陷被性能优化引爆是常态，不要回滚优化，去找被掩盖的根因。

## 异步与线程
- `ConfigureAwait(true)` 不是「回到 UI 线程」；跨线程回 UI 唯一可靠手段：注入 DispatcherQueue + `TryEnqueue` + TCS（`RunContinuationsAsynchronously`）桥接；async void 回调异常须收口到任务源。
- 绝不能 `EnqueueAsync(async () => await Xxx())` 而 Xxx 内部又 `EnqueueAsync`（自我死锁）；批量加载必须 `await Task.WhenAll`；UI 状态赋值统一放进 `EnqueueAsync` 块；单条 IO 必须有超时（`WaitAsync`）。
- 「任务正常完成」≠「有效工作」（WhenAll 完成但产出 0 = 全员静默失败），服务层 catch 加取证日志（类型 + HResult）。
- 页面内裸 `DispatcherQueue.GetForCurrentThread()` 会解析到 `DependencyObject.DispatcherQueue` 实例属性（CS0176）→ 用 `Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread()`（须全限定，`using Windows.System` 会抢先解析）。

## 验证手段 / 入口排查
- **验证 UI 一律用截屏，不靠 UIA 文本探测**（WinUI `TextBlock` 不把 `Text` 暴露为 UIA `Name`）。链路：`Start-Process` → `Interaction.AppActivate(pid)` → `Graphics.CopyFromScreen` 存 PNG → 目检；按钮可用 UIA `InvokePattern`（中文名用 `[char]0xXXXX` 拼接）；查控件类名用 `Inspect.exe`。
- 「点了没反应」先查入口是否存在（跳转常是「按 Tag 查导航项 → 找不到静默 return」）；`git log -S '<Tag>'` 为空 = 功能从未接入。
- 验证「设置即时生效」类功能**必须走真实 UI 路径**（改设置→观察），不能用「改配置文件 + 重启」替代——后者只验证持久化，会掩盖广播订阅缺失（曾因此漏掉 `SettingsViewModel.SettingsChanged` 未订阅的缺陷）。

## 图片显示与缩略图管线
- WinUI 3 的 `Image`/`ImageBrush` 插值不可控且无 mipmap → 唯一手段是位图物理像素≈显示区物理像素。排查顺序：显示尺寸 → DPI → 位图来源 → 插值算法 → 显示端插值。
- 请求尺寸语义统一为「显示区最长边」；档位量化先乘 `RasterizationScale` 再量化；缩小 `Fant`、放大 `Cubic`（上限 2 倍）；升级加载只升不降 + 容差。**`BitmapImage` 绝不能同时设 `DecodePixelWidth` 与 `DecodePixelHeight`**。
- 尺寸探测：图片 `BitmapDecoder.OrientedPixel*`（含 EXIF）；视频 `GetVideoPropertiesAsync()` + 按旋转标记交换宽高。
- 异步管线按线程亲和性切开：中间产物 `byte[]`，CPU 段线程池限流，只在最后一跳回 UI 线程构造 `BitmapImage`。
- 读性能日志先看计时起点（`THUMB|elapsedMs` 含量子排队）；「分辨率」与「宽高比」优先级不可共用；色彩链路（`ColorManageToSRgb` + `RespectExifOrientation` + `OrientedPixel*`）已验证，勿改。
- 磁盘缓存命中时会对源文件 stat（HDD 上 200 条不可忽略），索引库已存 `file_size`/`modified_utc` 可替代。

## 骨架屏三态
- `ThumbnailPresenter` + `ThumbnailLoadState` 三态，`ThumbnailState` 是唯一数据源。**取消 ≠ 失败**，必须回落 Loading。
- WinUI 3 XAML 无 `EventTrigger`/`BeginStoryboard` → 状态驱动动画选「模板化控件 + VisualState」；`RepeatBehavior=Forever` 容器回收后不自停，`Unloaded` 里回静态态。
- `{TemplateBinding}` 一次性求值，运行期会变的属性须依赖属性回调写入；容器回收复用不会重新应用模板（`Unloaded` 改过的状态在 `Loaded` 对称恢复）。
- 「内容可绘制」与「数据已就绪」是两个时刻，可靠信号是 Image **控件级** `ImageOpened`。
- 动画异常排查顺序：就绪信号级别 → 状态被重置 → 跃迁判据 → 二次换源 → 帧间隔 → 形状/位置 → 播放时机 → 时长/缓动；小元素 250ms 不足，本项目 500ms。

## 窗口视觉分层（含主题）
- 自下而上：全窗口 `Image` 背景（`UniformToFill`，`RowSpan=2`；随主题换 `light.jpg`/`dark.jpg`，由 `UpdateWallpaper` + `Content.ActualThemeChanged` 驱动）→ 透明标题栏与 NavigationView → 内容区半透明卡片。
- 透出背景：改 `{Default,Expanded,Top}PaneBackground` 与 `NavigationViewContentBackground` 为 Transparent（死键：`NavigationViewPaneBackground`、`NavigationViewBackground`）。
- 容器级圆角须归零（`NavigationViewContentGridCornerRadius`）否则裁掉卡片投影；Pane 与内容区竖线来自 `ContentGrid.BorderThickness.Left`；左栏圆角来自模板 `RightCornerRadiusFilterConverter`，元素级覆盖无效。
- `PixbianContentCard*` 两刷子在 ThemeDictionaries（浅色磨砂白 / 深色磨砂深灰，描边两主题取同 alpha 对称）；不随主题的画刷（压在内容上的角标 `#80000000`、播放态黑底）放主题字典之外。
- NavigationView 纵向：Row0 `ContentTopPadding` → Row1 `HeaderContent`（MinHeight=36，可 `AlwaysShowHeader="False"` 消掉）→ Row2 `ContentPresenter`。

## 视频播放器
- 页面为 DI 单例；`MediaPlayer` 惰性创建（构造期创建会 `0xC000027B`），`Unloaded` 必须释放。
- 顶/底控制条为**覆盖层**（不占布局行），隐藏时画面真正撑满内容区；自动隐藏用 `DispatcherQueueTimer`（3s），指针移入舞台 / 单击画面唤出，指针停在控制条或倍速下拉展开期间暂停计时，离开页面停表复位。
- 播放态是**窗口级独立分支**：`PlayerRoot`（`RowSpan=2`）覆盖标题栏行与内容行，播放时把 `TitleBar` 与 `NavigationView` 切 `Collapsed`，画面顶到窗口最上沿；分支切换统一收口在 `MainWindow.ApplyViewerChrome`（`OnChromeVisibilityChanged`）。内容卡片外观全程不被改写，`IsViewerVisible` 仅视频路径为 true（图片走独立窗口）。
- 播放器页走专属宿主 `VideoHost`，与常态 `PageHost` 是两个容器、互不争抢父容器（页面是单例）。**装载必须先让容器可见再赋 Content**（往 `Collapsed` 容器塞内容不触发 `Loaded`，页面初始化被整段跳过）；**卸载必须置空 Content**（只把容器切 `Collapsed` 不触发 `Unloaded`，`MediaPlayer` 不释放 → 解码器不回收、反复进出内存增长）。
- 顶栏落在系统标题栏那 48px（非客户区）：其中的返回按钮必须登记 Passthrough 才收得到点击；**只放行按钮、不放行整条顶栏**，否则顶栏空白区失去拖拽窗口能力；可见性变化（控制条自动隐藏）后矩形失效，须延一帧（`DispatcherQueue.TryEnqueue`）重算。顶栏本身收不到 `PointerEntered/Exited`，悬停保持只能靠底栏与画面区域。
- 播放态舞台恒为暗色 → 系统标题栏按钮前景**恒取白色**（不能按主题取黑，否则浅色主题下黑字压黑底不可见）；顶栏 CommandBar 右留 140px 避让系统按钮，背景由外层 Border 铺满整宽。
- **【性能】「播放视频 CPU 高」的定论与判别法（2026-09-06 实测）**：先用任务管理器 GPU 页看 **Video Decode 引擎**占用——0% 即在软解；再与系统「电影和电视」播同一文件对照（须同屏看整机 CPU）。本机 UHD 630 上某 4K 文件软解，我们应用 CPU 91%、系统播放器 94%，**不代表应用侧无解**（两者同走 Media Foundation，持平不等于无解——PotPlayer 自带 FFmpeg 解码器 + DXVA/D3D11 硬解，明显更快）；处置三条（代价递增）：装/换「HEVC 视频扩展」（仅 HEVC 有效、零代码；Windows 默认不提供 HEVC 解码器）→ `MediaSource.CreateFromUri` 替换 `CreateFromStorageFile` 做 A/B（1 行）→ 引入 FFmpegInteropX 换解码后端（只换解码、呈现与界面不变，代价是包体 +数十 MB 与 LGPL 合规）。教训：未证实前勿把「覆盖层打断硬件覆盖」当根因——移除全屏透明命中层后 CPU 并无变化（该假设证伪），但移除本身保留（结构更简，单击改由舞台根经冒泡接收）。
- 播放态优化保留项：视频画面之上不叠铺满的层、被完全遮挡的全窗壁纸一并隐藏、窗口根给不透明底（避免全窗 alpha 合成，争取硬件覆盖直通）。
- **FFmpegInteropX 解码后端（2026-09-06 落地）**：`FFmpegMediaSource.CreateFromStreamAsync(stream)` → `CreateMediaPlaybackItem()` 交给同一个 MediaPlayer，呈现与界面完全不变；失败回退 `MediaSource.CreateFromStorageFile`。四条硬约束：①`FFmpegMediaSource` 必须字段强引用（GC 回收会中断播放，官方警告）；②项目**必须有 RID**（`RuntimeIdentifier=win-x64`），否则包的 `runtimes/<rid>/native/` 下 `FFmpegInteropX.dll` 与 `avcodec/avformat/avutil` 等不会被复制 → 运行期静默回退系统解码；③RID 使产物落到 `...\win-x64\` 子目录，`Pixbian.ps1` 的硬编码 exe 路径须同步；④引入 winmd 后 CsWinRT 重新生成投影，需 `CsWinRTWindowsMetadata` 指向**本机已装**的 SDK 版本（本机仅 10.0.19041.0，默认取 TFM 的 26100 → 报 Platform.xml 缺失），并会新报 `CsWinRT1028`（要求自定义 WinRT 类型加 partial，未启用 AOT 可豁免）。
- 许可：FFmpegInteropX 为 Apache-2.0，FFmpeg 为 LGPL-2.1-or-later（动态链接、可替换、须署名）→ 声明见 `docs/THIRD-PARTY-NOTICES.md` 与设置页「关于」。
- **FFmpegInteropX 默认不会走 FFmpeg 软解**：`VideoDecoderMode` 默认 `AutomaticSystemDecoder`（优先系统解码器），系统解码器"能播"就不会用 FFmpeg——AV1 在本机正是如此，故换后端后 CPU 不变。要真正吃到 dav1d 必须对目标编码显式 `ForceFFmpegSoftwareDecoder`；其余编码保持自动以免丢掉硬解。配置在**聚合类的子对象**上：`MediaSourceConfig.Video`（`VideoConfig`）才含 `VideoDecoderMode` / `MaxDecoderThreads`（后者是 `uint`，FFmpeg 默认不按核心数放开线程，须显式设置）。排查是否真的走了 FFmpeg：播放时看 `VIDEODEC|codec=..|mode=..` 日志（已埋点于 `VideoPlayerViewModel`）。

## 索引、元数据与展示取数
- 索引两阶段：扫描只写文件属性，宽高时长后台分批回填；失败必须落「已失败」否则反复捞取；写回只覆盖尺寸/时长列，用户数据用 `COALESCE` 保护。
- 展示的大小/日期/时长全部来自索引库，不实时读文件系统（仅缺宽高时读文件头探测一次）；查看器 EXIF 面板例外。
- 库里 `taken_utc` 是文件系统时间，**不是 EXIF 拍摄时间**；EXIF `TakenAt` 只在查看器解析，未展示也未回写。

## 产品 / 技术决策（已定）
- 定位相册浏览器 → 砍 MagicScaler，Win2D 降可选；优先「查看器两级加载 + 磁盘缩略图缓存」；基线测量优先于选型。
- 排序为 `MediaSortKey` × `SortDirection` 两维；随机排序用固定序列（`random_rank`）保证分页稳定，不用 SQL `RANDOM()`。
- 删除走回收站（`RecycleBinHelper`）并同步清索引；「从索引移除」仅删记录。EXIF 拍摄时间暂不回填。
- 图库列表性能改造阶段：P0 止损 → P1a 方形视图虚拟化 → P1b 调度器+LRU → P2 ItemsRepeater（含自建选择服务）→ P3 稀疏数据源（可选）；文档 `docs/图库列表性能优化方案.md`。

## 已落地项 / 未做项定论
- 落地：缩略图磁盘缓存（两级哈希分桶、条目头指纹、LRU、temp+原子 Move，IO 失败全静默）、统一解码档位、统计异步化、排序键索引 + 随机游标分页。
- **缓存两层语义必须分开**：`Invalidate` = 内容真失效（清内存+磁盘），`Release` = 仅释放内存位图；误用会删光磁盘缓存。
- 未做（勿重复评估）：非随机排序游标；机会性预取。新查询用 `EXPLAIN QUERY PLAN` 复核。
- **已推翻的旧定论（2026-09-06）**：① `_items` 滑动窗口「触发条件苛刻」不成立（翻 10 页约 1900 条解码，线性放大成立）；② 埋点「发布前统一删」改为「异步化 + 默认关闭」（`diag.log` 是唯一线上取证手段）。

## 杂项
- NuGet 审计：常规构建用 `WarningsNotAsErrors` 豁免 NU19xx，`-p:AuditPipeline=true` 才升级为错误。
- XamlCompiler 缓存旧类型元数据：改 VM 属性类型报 CS1503 时 `dotnet clean` 即解（OneDrive 下删 obj 会被拦截）。HEIC/AVIF 依赖 WIC 编解码器扩展。
