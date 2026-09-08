# 长期记忆

> 只收规范、稳定事实与可复用方法论；不收代码定义、具体数值、一次性排障流水（那些进当日日志）。
> 2026-09-06 三次精简：合并同类、压缩表述，技术结论与判别式全保留。

## 项目与开发环境
- Pixbian：WinUI 3 本地相册浏览器。WASDK 2.4.0 元包（WinUI 实为 2.3.6）+ `net8.0-windows10.0.26100.0`（最低 17763）。测试基线 234（Core 189 / WebServer 33 / Imaging 12）。
- `dotnet` 不在 PATH，用 `C:\Program Files\dotnet\dotnet.exe`；包管理一律 pnpm；构建须 `-warnaserror`（0 警告）；缩进 4 空格；文件头 3–8 行中文模块说明。
- 硬件：C: SSD；D: 机械盘（媒体库 `D:\Downloads\*`，余量长期偏低，查「慢/卡」先看余量）；HDD 随机读 1MB ≈105ms，性能结论须此盘实测。
- OneDrive 工作区：新产物落盘可能被锁（重建后约 30s 内启动会闪退）→ 一键脚本用「显式 build + Start-Process」两段式；构建前确认应用未运行（MSB3026）。
- 终端：含中文 `.ps1` 须 UTF-8 with BOM；**传含中文命令会语法错误**（一般命令仍纯英文；commit 信息按用户 2026-09-08 要求**必须用中文**，经 `git commit -F <UTF-8 文件>` 提交）；GBK 乱码 ≠ 字符串有误。诊断脚本放 `C:\Temp\`。
- 系统还原通道失效 → 系统级变更前 `pnputil /export-driver` 导出驱动包。嵌套 `powershell -Command` 吞噬内层 `$var`/`$_`，提权脚本 stdout 不回传 → 写成 `.ps1` 并落日志。

## 分层与依赖方向（改动前必查）
- Core 最底层、零项目引用、纯 `net8.0`；`Data`/`Imaging`/`Media`/`WebServer` 单向引用 Core，`Pixbian`(UI) 引用全部。
- Core 禁用 WIC / `Windows.Graphics.Imaging`（绑 windows TFM 会破坏 Core.Tests）→ Core 定抽象 + UI 注入实现；跨层数据走 `Pixbian.Core.Models`。
- **FFmpegInteropX 只被 UI 项目引用**（`Pixbian.Media` 刻意不引入）→ 复用解码策略的工厂只能放 UI 层，不能下沉到 Media。
- **页面需要主窗口时经 `App.Services` 按需解析**，不要注入——窗口持有页面，注入会形成循环依赖。
- **媒体查看三条平行链路**（2026-09-07 定型）：双击图片 → ImageViewerWindow（纯查看）；双击视频 → VideoPlayerPage（主窗口播放态）；幻灯片 → SlideShowWindow（独立放映窗口，图片定时器驱动 + 视频 MediaEnded 驱动）。查看器内放映按钮是「移交」入口，查看器不再持有放映定时器；无边框全屏窗口宿主能力（含 2026-09-08 增加的全屏↔窗口双形态切换，样式位必须对称增删）在 `FullscreenWindowBase`（亚克力背景留在 ImageViewerWindow）。
- **放映浮动 UI（2026-09-08 重构）**：遮罩/退出/左右翻页/工具栏五元素收进单一 `OverlayLayer` 容器统一显隐（进入即显示、3s 淡出、点击画面 toggle、悬停暂停计时）；放映内设置改动经注入 `SettingsViewModel` 属性 setter 落盘广播回流，不直接碰设置服务；`ApplySettings` 须对驱动 UI 的状态补 OnPropertyChanged；Flyout 开关程序化赋 `IsOn` 会触发 Toggled，须防重入标志。

## 编码与协作规范
- 敏感信息禁止硬编码；API 响应 DTO 白名单过滤；日志脱敏（WinRT 异常记 HResult）。
- 改动 > 5 文件须先弹窗确认是否 commit（Conventional Commits）；> 2 文件的大改先取得用户确认方案。
- 每次修改须记当日 `.codebuddy/memory/YYYY-MM-DD.md`（超 1000 行分卷）。非用户要求不主动写记忆。

## 通用工程方法论
- **ItemsRepeater 的复用残留（首格显示上一列表内容）唯一可靠根治方案：让数据走 CollectionChanged（集合实例不变、原地 `Clear()` + 逐条 `Add()`）**，与 GridView 的 `ContainerContentChanging` 路径对齐，由框架保证元素与条目一一对应。**已验证无效的补丁路径（勿再引入）**：`ItemsSource=null → UpdateLayout → 赋新值`、`ElementRealizationOptions.ForceCreate`、切换后清/对齐子元素 `DataContext`、延一拍赋新集合 —— 这些都在与框架的回收机制打架，ForceCreate 甚至会让旧元素留在视觉树上产生新残留。代价：切目录时从 1 次通知变成 1+N 次（200 条约 10ms，无感）。
- **ItemsRepeater 切换 ItemsSource 的「置空 → UpdateLayout → 赋新值」四步**：除了原来的「置空 → UpdateLayout → 赋新值」三步，**赋新值后还要 `ChangeView(null, 0, null, true)` 回顶 + 再 `UpdateLayout()`**。否则 ScrollViewer 保留旧 offset，Repeater 的 RealizationRect 从中间开始，前面的索引永不被 prepare；而且 Repeater 会保留 child 0 锚点，对「索引不变但条目变了」的情况不重设 DataContext——**锚点 child 的 DataContext 必须显式同步**（手动对齐 + 补 `EnsureThumbnailAsync`，与 `ElementPrepared` 等效）。这是「切换列表后首格显示上一个列表的真实条目」类 bug 的双根因。方形 GridView 用 `ContainerContentChanging` 每帧兜底，所以不受影响。
- **页面级 UI 状态在「数据集合替换」之前必须主动清理**：选择模式 / 工具栏展开 / 侧栏开合等状态若跨集合残留，会在替换**中途**触发属性与布局变化（如页头整行替换改变内容区尺寸），把「让出一拍回收 / 重排」这类依赖拍间隔的清理路径打穿，表现为虚拟化列表首项显示上一个集合的内容。切目录 / 筛选 / 搜索入口一律先把这类状态归零（如 `ExitSelectionMode()`），再换集合。
- **性能定位顺序：先测真实数据规模 → 再测单点耗时 → 最后改代码**（口述规模必须实测）。
- 后台任务让出比例比绝对时长更关键（批次 2.5s 时节流 ≥1.5s）并设批次数上限；常驻任务须节流 + 排他，多入口收口同一把锁；排他优先 `Interlocked.CompareExchange`（持 CTS/`SemaphoreSlim` 字段触发 CA1001，`-warnaserror` 下是错误）。
- **查 API 是否存在一律读包内二进制**：WinRT 投影 `microsoft.windows.sdk.net.ref/<ver>/winmd/`；WinUI 组件 `Microsoft.WinUI.dll`（配套 `.xml` 的 `T:`/`P:`/`M:` 索引最精确）；主题键与模板默认值读 `Themes/generic.xaml`。Learn 的 WinRT 页会写错；超长页面勿用 web_fetch；winmd 不可 `Assembly.LoadFrom`。
- 按行号批量改多区间必须降序（从后往前），否则区间错位；机械重排优先整文件重写（先备份）。
- 工具事实：WAL 库用 `SqliteOpenMode.ReadWrite` 可与运行中应用并发读；`search_content` 的 glob 不支持 `!` 取反；查 MSBuild 属性 `dotnet msbuild x.csproj -getProperty:名`；`dotnet-stack report` 打托管栈；`dotnet-dump analyze` 对大转储极慢。

## WinUI 3 / WASDK 关键事实
- XAML 编译器路径须用 `PkgMicrosoft_WindowsAppSDK_WinUI`（旧键静默失败 → 全项目 CS0103）；TFM 升 26100 不需装 SDK 26100。
- **C# 错误会连锁引发 XAML "Unknown type" 假错误，先修 CS**；VS Code 的 `.g.i.cs` 误报 CS0103 属固有限制，**以 `dotnet build` 为准**；勿动 `BaseIntermediateOutputPath`、勿删 `obj\`。
- XAML 编译期不校验颜色字面量（`##RRGGBB` 能 0 警告构建、运行时崩 `0xC000027B`）；「构建成功 + 启动崩溃」先 `git status` 全量排查；颜色须 8 位 `#AARRGGBB` 才有透明度。
- **WinUI 3 没有 WPF 专有成员**：`Style.Resources` 不存在（WMC0011）→ 主题键覆盖只能放页面级或元素级 `.Resources`。
- `x:Bind` 绑定链上无属性通知时写 `Mode=OneWay` 报 WMC1506 → 恒定值一律 `OneTime`。x:Bind 默认即 OneTime，会变的才显式 OneWay。
- 命名空间：颜色常量在 `Microsoft.UI.Colors`；无 `Microsoft.UI.Core`（虚拟键用 `Windows.UI.Core.CoreVirtualKeyStates` + `Microsoft.UI.Input.InputKeyboardSource`）；`WinUIEx` 已移除 → `AppWindow.SetIcon(string)`；Picker 用 `Microsoft.Windows.Storage.Pickers`（构造传 `WindowId`）；无 `RenderOptions.BitmapInterpolationMode`；缩放比取 `XamlRoot.RasterizationScale`。
- `StaticResource` 引用不存在资源启动即崩且须类型匹配；StaticResource 无法解析 ThemeDictionaries 内资源 → 业务画刷一律 `ThemeResource`。
- **ContentDialog.Content 不可用仍挂在页面视觉树上的元素**（双父级 → ShowAsync 抛「already the child of another element」），且 App 层 UnhandledException 吞异常后表现即「点击无效」→ 先查 crash.log；正确做法：弹出前 `Children.Remove(panel)`、finally 归还，x:Bind 仍有效（同 namescope）。跨页共享的设置行样式放 App.xaml 级——嵌入宿主页面的 Page 构造时不在宿主视觉树内，Page 级 StaticResource 不可靠。
- **ContentDialog.Content 若是 XAML 里 `Visibility="Collapsed"` 的面板，须弹出时手动置 Visible**——对话框不会自动展开 Content，否则标题/按钮正常但内容区空白。
- **x:Bind TwoWay 绑 Selector.SelectedValue + 值类型 VM 属性是雷**：ItemsSource 清空/未命中时 Selector 置 null，TwoWay 回写拆箱 null → NRE（在 Dispatcher 回调抛出，还会污染弹层使后续对话框全部失效）。一律 `SelectedValue` OneWay + SelectionChanged 手动回写（`is long` 判空）。`SelectedIndex` 绑 int 无拆箱风险可用 TwoWay。
- **ItemsRepeater 切 ItemsSource 的「置空 → UpdateLayout → 赋新」必须让出一拍**：置空只把元素标成 `idx=-1`（待回收），**不会**立刻从视觉树卸下；同一同步块内赋新集合时 `GetOrCreateElementAt` 取不到这些仍在树上的元素（未进回收池），只能新建并追加 → 旧元素作为「幽灵」占据前几个视觉槽位，新条目被挤到后面，表现为「切换列表后首格显示上一个列表的内容」。正确写法：`ItemsSource=null` 后 `DispatcherQueue.TryEnqueue(...)` 再赋新集合（一帧延迟，肉眼无感）。另：`ElementClearing` 不能用来清 DataContext——任何 recycle（含模式切换/布局变化）都会触发，会把正常列表清空。
- **ItemsRepeater 的 DataTemplate 内，元素级 `Resources` 里禁止放 `{ThemeResource}`**：元素在布局 pass 内 realize，此时解析元素级 Resources 中的 ThemeResource 会触发 XAML fail-fast（0xc000027b，无托管堆栈、crash.log 无记录，实测启动 1 秒内崩）。复选/按钮的配色覆盖一律把同名键放进**页面级 ThemeDictionaries**（Default + Dark 各一份 hex），靠资源查找链命中；动态值用 `Color` 键 + 元素级画刷的写法只适用于 ControlTemplate 作用域（GridView 的 CheckBox 可用，ItemsRepeater 的不可用）。
- **无堆栈崩溃的自启动二分法**：`Start-Process` 启动 + `Get-Process` 判存活（15s）+ timing.log 看最后探针，每轮 ≤1 分钟，可无人值守连做多组消融实验（硬编码 Visibility / 删 Resources / 换资源作用域）一击定位；构建后须等 30s 再启动（OneDrive 锁产物）。判活信号优先级：进程存活 > `overlay-hidden` 探针 > 截屏。
- **复用页面元素作对话框 Content 的两条必要配套**：① 对话框关闭不清空对 Content 的引用（挂到 ContentPresenter 直至 dialog 被 GC）→ 复用前须 `dialog.Content = null` 断开，否则 set_Content 概率性抛「already the child」；② 弹层主题不自动跟随 root.RequestedTheme → `dialog.RequestedTheme = ActualTheme` 显式对齐，否则深浅混合白底白字（文字「不显示」）。
- **`SoftwareBitmapSource` 实测不可用**（UI 亲和 → fail-fast `0xC000027B`）；`BitmapImage` 是唯一稳定显示管线。
- unpackaged 应用要 Win11 圆角只能靠 `MicaBackdrop`（2.3.6 无 `TransparentBackdrop`）；PRI 不索引 `<Content>` 项 → 资源按 `AppContext.BaseDirectory` 磁盘路径加载。
- `ThemeShadow` + `Translation`：z 是投影唯一输入；`Translation` 不参与布局；`Border.CornerRadius` 会裁掉子内容投影 → 圆角图片交给 `Border.Background` 的 `ImageBrush`。
- 延伸标题栏后系统按钮前景色不随主题更新：须显式设 `AppWindow.TitleBar.Button{Foreground,Background,Inactive*}Color`，在「设置切换」与 `ActualThemeChanged` 两路径各刷一次。
- 元素外观需「运行时覆盖 + 退出还原」：初值写 Style Setter 而非本地值，退出 `ClearValue` 回落（本地值会压掉 ThemeResource 丢主题随动）。
- 绝不在运行时把页面宿主（PageHost）搬进另一容器：触发 `Page.Unloaded`，播放器页会因此销毁 `MediaPlayer`。
- **NavigationView 动态子项**：子项扁平进同一列表，只在父项 `IsExpanded` **值变化**时重算——值不变（哪怕子项是后加的）不会插入新子项；动态填充后须先置 false 再置 true 强制触发。
- **NavigationView 展开交互（实测定论）**：①点行内容与点箭头**都会**切换展开，控件无属性可关；②点箭头**不抛** `ItemInvoked`，点行才抛；③切换相对 `ItemInvoked` 的先后不固定，撤销逻辑须两条路径都覆盖；④**在 `ItemInvoked`/`Expanding`/`Collapsed` 回调里同步改 `IsExpanded` 会 fail-fast 退出**（无托管异常、无 crash.log、无事件日志，唯一线索是 diag.log 心跳在点击时刻中断）——改写一律 `DispatcherQueue.TryEnqueue` 延到下一个消息；⑤区分「点箭头 / 点行」只能按 `PointerPressed` 落点（`AddHandler(..., handledEventsToo: true)`，箭头区按下被项内部标记为已处理），箭头位于行右端约 44px 内；⑥`Expanding`/`Collapsed` 是 **NavigationView 级**事件（args 带 `ExpandingItemContainer`/`CollapsedItemContainer`），挂到 NavigationViewItem 上报 WMC0011。

## 控件与布局约束
- 分组 ListViewBase 配自定义 ItemsPanel 时排列的是 `GroupItem`；无内置 Justified 布局与 GridLength 动画；自定义标题栏用 `InputNonClientPointerSource` Passthrough（矩形为物理像素须乘 `RasterizationScale`，布局/激活变化后重注册）。
- ItemsPanelTemplate 内不能 x:Bind 页面属性、不能 ElementName 跨 namescope；面板读子项走 `FrameworkElement.DataContext`。OneWay 与 `x:Load` 只支持 Page/UserControl，Window 层联动走代码后置 INPC 转发。
- ItemContainerStyle 模板内 x:Bind 根是模板化控件；页面属性须经 PageProxy。**模板内 x:Bind 禁配 StaticResource Converter**（运行时 NRE、编译期 0 警告）→ 条件显隐用 VisualState；VisualState Setter 优先级高于本地绑定值（「常态透明 + hover」只能靠 `.Resources` 覆盖 `ButtonBackground`/`PointerOver`/`Pressed` 三键）。
- 改控件外观优先覆盖主题资源（键名 `Xxx`/`XxxPointerOver`/`XxxFocused`/`XxxDisabled`），勿重写模板；给 `MenuFlyoutItem` 自定义模板会触发忙碌光标；圆角两档：4（控件）/ 8（表面）。
- `MenuFlyout` 从 `Application.Current.Resources` 取出是共享单例，重复 `ShowAt` 抛 `E_INVALIDARG` → 可重复弹出用工厂方法每次 `new`。`CommandBar` 动态溢出有未修 bug（issue #6450）。
- 切换 `SelectionMode` 会重置选择 → 先抓快照再恢复；间距由面板 `Spacing` 承担、子项模板零 Margin；嵌套 ScrollViewer 内的列表须禁用自身垂直滚动；多实例 GridView 选择聚合须经实例列表（Loaded/Unloaded 登记）。
- `Page.KeyboardAccelerators` 会污染页面内所有 ToolTip（by design）→ 用代码后置 KeyDown；菜单项 `KeyboardAcceleratorTextOverride` 是豁免用法。
- **键盘事件订阅层级**：键盘事件自焦点元素向上冒泡，焦点在页面时不会下传到子级网格 → 页面级快捷键须订阅在页面自身，不能挂子 Grid。
- 覆盖层按钮必须就地拦截 `Tapped`（`e.Handled = true`）：Button 不吞 Tapped，会冒泡到父级触发父容器的点击逻辑。
- 符号字体码点（离屏渲染实证）：空心文件夹 `\uED25` / 实心 `\uE8B7`；线星 `\uE734` / 实心 `\uE735`；空心爱心 `\uEB51` / 实心 `\uEB52`（`Symbol.Favorite` 是爱心非星）；鼠标 `\uE962`、照片 `\uE8B9`、音乐 `\uE189`。查码点用 PowerShell + WPF `RenderTargetBitmap` 离屏渲染 PNG 目检。
- `Expander` 嵌卡片须在 `.Resources` 把三个 `Expander*BorderBrush` 与两个 BorderThickness 归零、Background 指 `SubtleFillColorTransparentBrush`。
- `ToggleSwitch` 默认 `MinWidth=154px`；`Border` 只能一个 `Child`（多项并列报 `WMC0035`）；Grid `Auto` 列内子元素默认左对齐；XAML 注释内不得出现连续 `--`；WinUI 3 的 `Grid` 支持 `Padding`；`NumberBox` 清空时 `Value` 为 `NaN`。

## 动画与交互
- Storyboard 优先代码后置现场创建并以元素对象为目标（Resources 里 XAML Storyboard 的 TargetName 解析失败即静默无动画）；`FillBehavior` 默认 `HoldEnd` → 每轮动画前复位起始值。
- 数据驱动动画须防重复触发；快速连发时上一轮 Completed 可能清掉下一轮的源，需容忍缺失。
- 「控件自动隐藏」一律用 `DispatcherQueue.CreateTimer()`（Tick 在 UI 线程），绝不订阅 `CompositionTarget.Rendering`。

## 虚拟化（结论已实测，勿再试错）
- 唯一公开扩展点是 `ItemsRepeater` + `VirtualizingLayout`；`IScrollInfo` 未公开 → 自定义 VirtualizingPanel 作 GridView.ItemsPanel 不可行；本版本无 `SelectionModel` → 换 ItemsRepeater 须自建选择服务（最大成本）。
- 方向切勿套错：GridView **不能**外层包 ScrollViewer；ItemsRepeater **必须**外层包 ScrollViewer（靠它算 `RealizationRect`）。
- `VirtualizingLayoutContext` 可用：`ItemCount`、`RealizationRect`、`VisibleRect`、可写 `LayoutOrigin`、`RecommendedAnchorIndex`、`GetItemAt(i)`、`GetOrCreateElementAt(i,opts)`、`RecycleElement(el)`；可重写 Measure/Arrange/InitializeForContextCore/OnItemsChangedCore。
- 变高布局（Justified）虚拟化先建「行偏移表 + 每行起始索引」，二分查可见行区 → O(log n)；未测量行用估算行高参与 Extent。
- 本项目方形视图用自绘 `SquarePanel`、自适应用 `JustifiedPanel`，均不做虚拟化，靠分页增量控制规模。

## 图库列表性能（定论）
- 「随条目数变卡」主因是解码提交数随页数线性放大（`pending` 取全集合 × 位图按索引置空 = 自激循环）。
- 三条铁律：容器数恒定（≤ 视口 + 2 屏）；解码请求数 = O(视口)；内存按 LRU/字节回收而非按索引（淘汰须与「从未加载」区分）。
- 反模式：`ContainerFromItem` 全集合扫描取消 = O(n²)；measure 内发起解码或重建订阅 = O(n)；解码管线上的同步日志（`File.AppendAllText` + 全局锁）会串行化解码线程。
- `ContainerFromItem` 不能单独作可见性判据（「从未进视口」与「回收后」都返回 null）→ 须额外记录「是否曾生成过容器」。
- 完整方案见 `docs/图库列表性能优化方案.md`。对标：主参考 Windows 照片应用；不参考 Lightroom（其性能建立在导入期智能预览预算上）。

## 卡死 / 冻结排查（判别式）
- 先分辨「慢」还是「冻结」，判据优先级：心跳中断 → 日志产出 → CPU。三态：① CPU 单核 100% + 日志停滞 = 布局死循环；② CPU 高 + 日志持续增长 = 业务慢；③ CPU 增量 0 + 日志停滞 + 全线程 Wait = 渲染停摆。死循环时托管栈为空、`crash.log` 常无痕。
- 绝不让「位图尺寸」参与任何驱动布局的属性（解码 → 比例抖动 → 重排 → 回写的环）。
- 订阅 `CompositionTarget.Rendering` 属高危（合成停摆、布局 pass 死亡、hover 无反应）——图库点击冻结即此因，已根治移除；已推翻旧假设：旧 Intel 驱动、IO/磁盘瓶颈。
- LayoutCycle 真因（已根治）：loading 覆盖层与内容 GridView 同格时测量互相失效；覆盖层须在窗口层（PageHost 兄弟位），隐藏时复位 `IsIndeterminate=false`。
- 取证：`dotnet-stack report` 判 UI 死活；TICK 心跳间隙判同步阻塞；diag.log 判管线进度。布局/上屏与 DispatcherQueue 定时器是两条生命周期，判死须分别取证；多嫌疑用叠加减法实验逐轮排除。
- 「视觉死但日志活」= 布局系统坏死而非进程死；概率性缺陷被性能优化引爆是常态，不要回滚优化，去找被掩盖的根因。

## 异步与线程
- **MediaPlayer（Media Foundation 管线）的 `Source`/`Play`/`Pause` 必须在 UI 线程调用**：跨线程（`ConfigureAwait(false)` 后落到线程池线程）调用 `Play()` 会**同步挂起**——不返回、不抛异常，执行流无声消失，与「跨线程 UI 抛 WrongThread」的直觉相反。ViewModel 中操作 UI 亲和对象（MediaPlayer、ObservableProperty 赋值）的 await 链一律不加 `ConfigureAwait(false)`；库/服务内部的保留（外层 await 无标记自然回 UI 线程）。
- **「执行流无声消失」排查法**：步骤级埋点（`step=xxx`）逐步逼近断点；fire-and-forget（`_ = XxxAsync()`）里的异常是绝对黑洞，被调方必须自己保证异常不外泄（独立 try/catch 兜底留痕）；留痕必须前置到「事实成立」那一刻而非流程走完之后。
- `ConfigureAwait(true)` 不是「回到 UI 线程」；跨线程回 UI 唯一可靠手段：注入 DispatcherQueue + `TryEnqueue` + TCS（`RunContinuationsAsynchronously`）桥接；async void 回调异常须收口到任务源。
- 绝不能 `EnqueueAsync(async () => await Xxx())` 而 Xxx 内部又 `EnqueueAsync`（自我死锁）；批量加载必须 `await Task.WhenAll`；UI 状态赋值统一放进 `EnqueueAsync` 块；单条 IO 必须有超时（`WaitAsync`）。
- 「任务正常完成」≠「有效工作」（WhenAll 完成但产出 0 = 全员静默失败），服务层 catch 加取证日志（类型 + HResult）。
- 页面内裸 `DispatcherQueue.GetForCurrentThread()` 会解析到实例属性（CS0176）→ 用 `Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread()` 全限定。

## 验证手段 / 入口排查
- **验证 UI 一律用截屏 + UIA 枚举 ListItem（Name + 坐标）**；WinUI `TextBlock` 不把 Text 暴露为 UIA Name，按钮可用 UIA `InvokePattern`；查控件类名用 `Inspect.exe`。链路：`Start-Process` → `AppActivate(pid)` → `CopyFromScreen` 存 PNG → 目检。
- **【用户强制约束】禁止用自动脚本（Start-Process + CopyFromScreen + UIA 驱动点击/按键）代替人工验证程序功能**：功能是否正常一律由用户手工验证并反馈或截图，AI 不得自行启动应用截图做功能验收。自启/截图脚本仅可用于**排查崩溃与取证定位**（判活、抓崩溃前日志、取窗口矩形等），且不得据此宣称功能已修复；修复结论须以用户手工验证为准。
- 「点了没反应」先查入口是否存在（跳转常是「按 Tag 查导航项 → 找不到静默 return」）；`git log -S '<Tag>'` 为空 = 功能从未接入。
- 验证「设置即时生效」类功能必须走真实 UI 路径，不能「改配置文件 + 重启」替代（会掩盖广播订阅缺失）。
- 加过 RID 后产物移到 `win-x64` 子目录，**旧 exe 仍留在原目录且可双击启动** → 排查「改动没生效」先确认进程路径 `(Get-Process Pixbian).Path`，再确认 ffmpeg 原生库是否加载（Modules 过滤 avcodec/FFmpegInteropX）。

## 图片显示与缩略图管线
- WinUI 3 的 `Image`/`ImageBrush` 插值不可控且无 mipmap → 唯一手段是位图物理像素 ≈ 显示区物理像素。排查顺序：显示尺寸 → DPI → 位图来源 → 插值算法 → 显示端插值。
- 请求尺寸语义统一为「显示区最长边」；档位量化先乘 `RasterizationScale` 再量化；缩小 `Fant`、放大 `Cubic`（上限 2 倍）；升级加载只升不降 + 容差。**`BitmapImage` 绝不能同时设 `DecodePixelWidth` 与 `DecodePixelHeight`**。
- 尺寸探测：图片 `BitmapDecoder.OrientedPixel*`（含 EXIF）；视频 `GetVideoPropertiesAsync()` + 按旋转标记交换宽高。
- 异步管线按线程亲和性切开：中间产物 `byte[]`，CPU 段线程池限流，只在最后一跳回 UI 线程构造 `BitmapImage`。
- 读性能日志先看计时起点；「分辨率」与「宽高比」优先级不可共用；色彩链路（`ColorManageToSRgb` + `RespectExifOrientation` + `OrientedPixel*`）已验证，勿改。
- 磁盘缓存命中时会对源文件 stat（HDD 上 200 条不可忽略），索引库已存 `file_size`/`modified_utc` 可替代。
- **视频封面取帧**：系统 `StorageFile.GetThumbnailAsync` 快且有缓存，但**取帧位置不可控**（长视频片头常是黑场/台标）。要指定时间点必须用 `MediaClip.CreateFromFileAsync` + `MediaComposition.GetThumbnailAsync(position, w, h, VideoFramePrecision.NearestFrame)`；返回的 ImageStream 可直接读字节，与「byte[] → UI 线程 SetSource」管线兼容。抽帧慢 → 只用于超门槛的长视频并先判时长，失败一律静默回退系统缩略图 + 取证日志。

## 骨架屏三态
- `ThumbnailPresenter` + `ThumbnailLoadState` 三态，`ThumbnailState` 是唯一数据源。**取消 ≠ 失败**，必须回落 Loading。
- WinUI 3 XAML 无 `EventTrigger`/`BeginStoryboard` → 状态驱动动画选「模板化控件 + VisualState」；`RepeatBehavior=Forever` 容器回收后不自停，`Unloaded` 里回静态态。
- `{TemplateBinding}` 一次性求值，运行期会变的属性须依赖属性回调写入；容器回收复用不会重新应用模板（`Unloaded` 改过的状态在 `Loaded` 对称恢复）。
- 「内容可绘制」与「数据已就绪」是两个时刻，可靠信号是 Image 控件级 `ImageOpened`。
- 动画异常排查顺序：就绪信号级别 → 状态被重置 → 跃迁判据 → 二次换源 → 帧间隔 → 形状/位置 → 播放时机 → 时长/缓动；本项目小元素用 500ms。

## 窗口视觉分层（含主题）
- 自下而上：全窗口 `Image` 背景（`UniformToFill`，`RowSpan=2`，随主题换 `light.jpg`/`dark.jpg`）→ 透明标题栏与 NavigationView → 内容区半透明卡片。
- 透出背景：改 `{Default,Expanded,Top}PaneBackground` 与 `NavigationViewContentBackground` 为 Transparent（死键：`NavigationViewPaneBackground`、`NavigationViewBackground`）。
- 容器级圆角须归零（`NavigationViewContentGridCornerRadius`）否则裁掉卡片投影；Pane 与内容区竖线来自 `ContentGrid.BorderThickness.Left`；左栏圆角来自模板 `RightCornerRadiusFilterConverter`，元素级覆盖无效。
- `PixbianContentCard*` 两刷子在 ThemeDictionaries（浅色磨砂白 / 深色磨砂深灰，描边两主题同 alpha 对称）；不随主题的画刷放主题字典之外。
- NavigationView 纵向：Row0 `ContentTopPadding` → Row1 `HeaderContent`（MinHeight=36，`AlwaysShowHeader="False"` 可消掉）→ Row2 `ContentPresenter`。

## 视频播放（播放器页 + 短片页）
- 页面为 DI 单例；`MediaPlayer` 惰性创建（构造期创建会 `0xC000027B`），`Unloaded` 必须释放，否则解码器不回收。
- 播放态是窗口级独立分支：`PlayerRoot`（`RowSpan=2`）覆盖标题栏行与内容行，切换统一收口在 `MainWindow.ApplyViewerChrome`；`IsViewerVisible` 仅视频路径为 true（图片走独立窗口）。
- 播放器页走专属宿主 `VideoHost`，与常态 `PageHost` 互不争抢。装载必须先让容器可见再赋 Content（往 Collapsed 容器塞内容不触发 Loaded）；卸载必须置空 Content（只切 Collapsed 不触发 Unloaded）。
- 顶栏在系统标题栏 48px 内：交互控件必须登记 Passthrough 才收得到点击；只放行按钮不放整条顶栏；可见性变化后矩形失效须延一帧重算。顶栏收不到 `PointerEntered/Exited`。播放态舞台恒暗 → 系统按钮前景恒取白色；顶栏 CommandBar 右留 140px 避让系统按钮。
- **控制条/覆盖层**：播放器页顶底控制条为覆盖层（不占布局行），自动隐藏用 `DispatcherQueueTimer`（3s）。**画面之上不得叠铺满的层**（打断硬件覆盖 → 每帧合成，4K 代价显著）；被完全遮挡的全窗壁纸一并隐藏；窗口根给不透明底以争取直通。
- **「播放视频 CPU 高」判别法**：先看任务管理器 GPU 页 Video Decode 占用——0% 即软解；与系统「电影和电视」同文件同屏对照。软解持平不等于无解（系统播放器同走 Media Foundation）；PotPlayer 自带 FFmpeg + DXVA 明显更快。处置三条（代价递增）：装「HEVC 视频扩展」（仅 HEVC、零代码）→ `CreateFromUri` 替换 `CreateFromStorageFile` → 引入 FFmpegInteropX。教训：未证实前勿把「覆盖层打断硬件覆盖」当根因（已证伪，但结构简化保留）。
- **FFmpegInteropX 要点**：`CreateFromStreamAsync` → `CreateMediaPlaybackItem()`，失败回退系统解码。硬约束：①`FFmpegMediaSource` 必须字段强引用（GC 回收中断播放）；②项目必须有 RID（`RuntimeIdentifier=win-x64`）否则 native dll 不复制 → 静默回退；③RID 使产物落 `...\win-x64\`，`Pixbian.ps1` 硬编码路径须同步；④需 `CsWinRTWindowsMetadata` 指向本机已装 SDK 版本（本机仅 10.0.19041.0），并新报 `CsWinRT1028`（未启用 AOT 可豁免）。
- 默认 `VideoDecoderMode=AutomaticSystemDecoder`：系统解码器能播就不用 FFmpeg（AV1 即此）。要吃 dav1d 须显式 `ForceFFmpegSoftwareDecoder`；配置在 `MediaSourceConfig.Video`（`VideoConfig`），线程数须显式设 `Environment.ProcessorCount`。排查看 `VIDEODEC|codec=..|mode=..` 日志。解码策略由 `IVideoPlaybackItemFactory` 统一供给，播放器页与短片页共用，勿各自实现。
- 许可：FFmpegInteropX Apache-2.0，FFmpeg LGPL-2.1-or-later（动态链接、须署名）→ 见 `docs/THIRD-PARTY-NOTICES.md` 与设置页「关于」。
- **短片页（Short）**：无传输控制条，单击/空格播放暂停，左右方向键切换；三按钮覆盖层（右上查看原视频 / 左下静音 / 右下下一个）。**背景音乐与视频严格联动**——同步暂停、片段结束即结束、切换即换曲、随机选曲；静音同时作用于视频与音频两个 player。片段策略集中在 `ShortClipPlanner`（≤60s 整段；60–100s 自 20s 截到片尾；≥100s 长度 40–80s 随机、起点不早于 20s）。

## 音乐库（架构约定）
- **音乐库独立于图库**：曲目落 `music_tracks` 表，**不进 `media_items`**。理由：图库在 `Kind=null`（不过滤类型）时会把音频连同图片视频查出，且会被元数据回填反复探测；规避须改 `MediaKind`/`MediaFileClassifier`/`BuildFilter`/回填服务，触及图库核心链路。
- 代价与替代：不能直接用 `LibraryWatcherService` 监控 → 改为设置变更时重扫 + 启动时从库恢复（机械盘全量扫盘可达秒级，必须 `Task.Run`）。音频分类器与 `MediaFileClassifier` 分开，后者供图库扫描共用，一旦支持音频就会污染图库。

## 索引、元数据与展示取数
- 索引两阶段：扫描只写文件属性，宽高时长后台分批回填；失败必须落「已失败」否则反复捞取；写回只覆盖尺寸/时长列，用户数据用 `COALESCE` 保护。
- 展示的大小/日期/时长全部来自索引库，不实时读文件系统（仅缺宽高时读文件头探测一次）；查看器 EXIF 面板例外。
- 库里 `taken_utc` 是文件系统时间，不是 EXIF 拍摄时间；EXIF `TakenAt` 只在查看器解析，未展示也未回写。
- 按类型 + 随机排序取候选可直接用既有 `QueryAsync`（`Kind` + `SortKey=Random` + 换种子），无需新增仓储方法。

## 产品 / 技术决策（已定）
- 定位相册浏览器 → 砍 MagicScaler，Win2D 降可选；优先「查看器两级加载 + 磁盘缩略图缓存」；基线测量优先于选型。
- 排序为 `MediaSortKey` × `SortDirection` 两维；随机排序用固定序列（`random_rank`）保证分页稳定，不用 SQL `RANDOM()`。
- 删除走回收站（`RecycleBinHelper`）并同步清索引；「从索引移除」仅删记录。EXIF 拍摄时间暂不回填。
- 图库列表性能改造阶段：P0 止损 → P1a 方形视图虚拟化 → P1b 调度器 + LRU → P2 ItemsRepeater（含自建选择服务）→ P3 稀疏数据源（可选）。

## 已落地项 / 未做项定论
- 落地：缩略图磁盘缓存（两级哈希分桶、条目头指纹、LRU、temp + 原子 Move，IO 失败全静默）、统一解码档位、统计异步化、排序键索引 + 随机游标分页。
- **缓存两层语义必须分开**：`Invalidate` = 内容真失效（清内存 + 磁盘），`Release` = 仅释放内存位图；误用会删光磁盘缓存。
- 未做（勿重复评估）：非随机排序游标；机会性预取。新查询用 `EXPLAIN QUERY PLAN` 复核。
- 已推翻旧定论：① `_items` 滑动窗口「触发条件苛刻」不成立；② 埋点「发布前统一删」改为「异步化 + 默认关闭」（diag.log 是唯一线上取证手段）。

## 杂项
- NuGet 审计：常规构建用 `WarningsNotAsErrors` 豁免 NU19xx，`-p:AuditPipeline=true` 才升级为错误。
- XamlCompiler 缓存旧类型元数据：改 VM 属性类型报 CS1503 时 `dotnet clean` 即解（OneDrive 下删 obj 会被拦截）。HEIC/AVIF 依赖 WIC 编解码器扩展。
