# 长期记忆

> 只收规范、稳定事实、判别式与可复用方法论；不收代码定义、具体数值与一次性排障流水（那些进当日日志）。
> 2026-09-10 三度精简：同族合并、删除论证过程，只留结论；新增位图 DPI 尺寸约定与 Pillow 工具事实。

## 项目与开发环境
- Pixbian：WinUI 3 本地相册浏览器。WASDK 2.4.0 元包（WinUI 实为 2.3.6）+ `net8.0-windows10.0.26100.0`（最低 17763）。测试基线 234（Core 189 / WebServer 33 / Imaging 12）。
- `dotnet` 不在 PATH，用 `C:\Program Files\dotnet\dotnet.exe`；包管理一律 pnpm；构建须 `-warnaserror`（0 警告）；缩进 4 空格；文件首部 3–8 行中文模块说明。
- 硬件：C SSD；D 机械盘（媒体库 `D:\Downloads\*`，余量长期偏低，查「慢/卡」先看余量）；HDD 随机读 1MB ≈105ms，性能结论须此盘实测。
- OneDrive 工作区：产物落盘可能被锁（重建后 30s 内启动会闪退）→ 一键脚本用「显式 build + Start-Process」两段式；构建前确认应用未运行（MSB3026）。
- 终端：含中文 `.ps1` 须 UTF-8 with BOM；**传含中文命令会语法错误**（中文 commit 须 `git commit -F <UTF-8 文件>`）；GBK 乱码 ≠ 字符串有误。诊断脚本放 `C:\Temp\`。
- 系统还原通道失效 → 系统级变更前 `pnputil /export-driver`。嵌套 `powershell -Command` 吞噬内层 `$var`/`$_`，提权脚本 stdout 不回传 → 写成 `.ps1` 并落日志。
- 工具事实：WAL 库用 `SqliteOpenMode.ReadWrite` 可与运行中应用并发读；`search_content` 的 glob 不支持 `!` 取反；查 MSBuild 属性 `dotnet msbuild x.csproj -getProperty:名`；`dotnet-stack report` 打托管栈；`dotnet-dump analyze` 对大转储极慢。
- Python 3.14 + Pillow 12 可用（无 numpy、无 ImageMagick、无 SVG 光栅化库）。**Pillow 12 已移除 `ImageChops.divide`** → 含 alpha 缩放改用「预乘（`ImageChops.multiply`）→ Lanczos → 逐像素反预乘」。
- PowerShell 5.1 三个坑：①`New-Object Type (a, b)` 会被解析成数组参数 → 一律用 `[Type]::new()`；②XML 适配器把无属性纯文本元素（如 SVG 的 `<style>`）投影为 `String` → `.InnerText` 为空，须按 `-is [XmlNode]` 分别取值；③`@($null).Count -eq 1`，判空要过滤 `$null`。
- **验证 ICO 不能用 `System.Drawing.Icon`**（GDI+ 不支持 PNG-in-ICO，`ToBitmap()` 抛「请求的范围扩展超过了数组的结尾」）→ 用 Win32 `LoadImage(IMAGE_ICON + LR_LOADFROMFILE)` 按尺寸逐个加载判定。
- 按行号批量改多区间必须降序；机械重排优先整文件重写（先备份）。

## 分层与依赖方向（改动前必查）
- Core 最底层、零项目引用、纯 `net8.0`；`Data`/`Imaging`/`Media`/`WebServer` 单向引用 Core，`Pixbian`(UI) 引用全部。跨层数据走 `Pixbian.Core.Models`。
- Core 禁用 WIC / `Windows.Graphics.Imaging`（绑 windows TFM 会破坏 Core.Tests）→ Core 定抽象 + UI 注入实现。
- **FFmpegInteropX 只被 UI 项目引用**（`Pixbian.Media` 刻意不引入）→ 复用解码策略的工厂只能放 UI 层。
- **页面需要主窗口时经 `App.Services` 按需解析**，不要注入（窗口持有页面 → 循环依赖）。
- **媒体查看三条平行链路**：双击图片 → ImageViewerWindow；双击视频 → VideoPlayerPage（主窗口播放态）；幻灯片 → SlideShowWindow（图片定时器 + 视频 MediaEnded 驱动）。无边框全屏宿主能力在 `FullscreenWindowBase`（全屏↔窗口双形态，样式位须对称增删）。
- **放映浮动 UI** 收进单一 `OverlayLayer`（进入即显示、3s 淡出、点击 toggle、悬停暂停计时）；放映内设置改动经注入 `SettingsViewModel` setter 落盘广播回流；`ApplySettings` 须补 OnPropertyChanged；Flyout 程序化赋 `IsOn` 会触发 Toggled，须防重入。

## 编码与协作规范
- 敏感信息禁止硬编码；API 响应 DTO 白名单过滤；日志脱敏（WinRT 异常记 HResult）。
- 改动 > 5 文件须先弹窗确认是否 commit（Conventional Commits，中文内容）；> 2 文件的大改先取得用户确认方案。
- 每次修改须记当日 `.codebuddy/memory/YYYY-MM-DD.md`（超 1000 行分卷）。非用户要求不主动写记忆。
- **【强制】功能是否正常一律由用户手工验证并反馈或截图**，AI 不得用自启 + 截屏 + UIA 脚本代替人工验收；自启/截图仅可用于崩溃排查取证，不得据此宣称已修复。
- **【强制】会话中途磁盘文件可能被用户改动**：读到与先前不一致的尺寸/内容时须复核再动手，别以旧快照判断。

## 通用工程方法论
- **性能定位顺序：先测真实数据规模 → 再测单点耗时 → 最后改代码**（口述规模必须实测）。
- 后台任务让出比例比绝对时长更关键（批次 2.5s 时节流 ≥1.5s）并设批次数上限；常驻任务须节流 + 排他，多入口收口同一把锁；排他优先 `Interlocked.CompareExchange`（持 CTS/`SemaphoreSlim` 字段触发 CA1001，`-warnaserror` 下是错误）。
- **查 API 是否存在一律读包内二进制**：WinRT 投影 `microsoft.windows.sdk.net.ref/<ver>/winmd/`；WinUI 组件 `Microsoft.WinUI.dll`（配套 `.xml` 的 `T:`/`P:`/`M:` 索引最精确）；主题键与模板默认值读 `Themes/generic.xaml`。Learn 的 WinRT 页会写错；超长页面勿用 web_fetch；winmd 不可 `Assembly.LoadFrom`。

## 虚拟化：ItemsRepeater（已实测，勿再试错）
- **复用残留根治方案只有一条：让数据走 CollectionChanged（集合实例不变、原地 `Clear()` + 逐条 `Add()`）**。代价：切目录 1 次通知变 1+N 次（200 条约 10ms，无感）。
- **已验证无效（勿再引入）**：`ItemsSource=null → UpdateLayout → 赋新值`、`ForceCreate`（还会留旧元素在视觉树）、切换后手动清 `DataContext`、延一拍赋新集合。根因：置空只标 `idx=-1` 不立刻卸下；Repeater 保留 child 0 锚点，对「索引不变但条目变了」不重设 DataContext。方形 GridView 有 `ContainerContentChanging` 每帧兜底故不受影响。
- **自定义 VirtualizingLayout 必须自己收拢陈旧元素**：存上一 pass 映射，Arrange 前把不在本 pass 的元素 `Arrange(default)`（零矩形）——否则停旧矩形继续命中测试（幽灵槽位）。**不得改用 Visibility**（运行期改必 fail-fast）。
- **等高视图取命中条目一律用 `JustifiedRepeater.GetElementIndex(element)` 反查**，不能用元素 DataContext（复用期停留旧条目，既选错图也污染选择集合）。
- **自建选择服务的「交集陷阱」**：`SelectedItems = 选中集 ∩ 当前集合`，混入游离条目则交集恒空 → 误判「无选中」。增量写前须剔除游离条目。
- **除页面交互外的任何选中集合变更（删除联动等）都必须触发 `SelectionChanged`**，否则页面缓存变幽灵条目（DEL / Ctrl+C / F2 静默失效）。
- **页面级 UI 状态在「数据集合替换」前必须清理**（选择模式 / 工具栏展开 / 侧栏开合）：残留会在替换中途触发布局变化。切目录 / 筛选 / 搜索入口一律先归零再换集合。
- 唯一公开扩展点是 `ItemsRepeater` + `VirtualizingLayout`；`IScrollInfo` 未公开 → 自定义 VirtualizingPanel 作 GridView.ItemsPanel 不可行；本版本无 `SelectionModel` → 换 ItemsRepeater 须自建选择服务（最大成本）。
- 方向切勿套错：GridView **不能**外包 ScrollViewer；ItemsRepeater **必须**外包（靠它算 `RealizationRect`）。
- `VirtualizingLayoutContext` 可用：`ItemCount`、`RealizationRect`、`VisibleRect`、可写 `LayoutOrigin`、`RecommendedAnchorIndex`、`GetItemAt`、`GetOrCreateElementAt`、`RecycleElement`；可重写 Measure/Arrange/InitializeForContextCore/OnItemsChangedCore。
- 变高布局先建「行偏移表 + 每行起始索引」，二分查可见行区；未测量行用估算行高参与 Extent。
- `ElementClearing` 不能用于清 DataContext（任何 recycle 都触发）；`ElementPrepared` 在布局 pass 内同步触发，其中改绑定属性会 fail-fast，实际工作须 `TryEnqueue`。

## WinUI 3 / WASDK 关键事实
- XAML 编译器路径须用 `PkgMicrosoft_WindowsAppSDK_WinUI`（旧键静默失败 → 全项目 CS0103）；TFM 升 26100 不需装 SDK 26100。
- **C# 错误会连锁引发 XAML "Unknown type" 假错误，先修 CS**；VS Code `.g.i.cs` 误报 CS0103 属固有限制，**以 `dotnet build` 为准**；勿动 `BaseIntermediateOutputPath`、勿删 `obj\`。
- XAML 编译期不校验颜色字面量（`##RRGGBB` 能 0 警告构建、运行时崩 `0xC000027B`）→ 「构建成功 + 启动崩溃」先 `git status` 全量排查；颜色须 8 位 `#AARRGGBB` 才有透明度。
- **无 WPF 专有成员**：`Style.Resources` 不存在（WMC0011）→ 主题键覆盖只能放页面级或元素级 `.Resources`。
- `x:Bind` 绑定链无属性通知时写 `Mode=OneWay` 报 WMC1506 → 恒定值一律 `OneTime`。
- **ScrollViewer 默认 `IsTabStop=False`，`Focus(Programmatic)` 静默失败** → 交还焦点前必须先设 IsTabStop=True。
- **`FocusManager.GetFocusedElement()` 在键处理栈内返回 null**，不可靠；判焦点是否在本页须订阅 `FocusManager.GotFocus`（`EventHandler<FocusManagerGotFocusEventArgs>`）记录最近获焦元素，随 Loaded/Unloaded 订退。
- 命名空间：颜色常量在 `Microsoft.UI.Colors`；无 `Microsoft.UI.Core`（虚拟键用 `Windows.UI.Core.CoreVirtualKeyStates` + `Microsoft.UI.Input.InputKeyboardSource`）；`WinUIEx` 已移除 → `AppWindow.SetIcon(string)`；Picker 用 `Microsoft.Windows.Storage.Pickers`（构造传 `WindowId`）；无 `RenderOptions.BitmapInterpolationMode`；缩放比取 `XamlRoot.RasterizationScale`。
- `StaticResource` 引用不存在资源启动即崩且须类型匹配；无法解析 ThemeDictionaries 内资源 → 业务画刷一律 `ThemeResource`。
- **元素级 `Resources` 里禁止放 `{ThemeResource}`**（尤其 ItemsRepeater 的 DataTemplate）：元素在布局 pass 内 realize 时解析会 fail-fast（0xc000027b，无托管堆栈）。配色覆盖一律放**页面级 ThemeDictionaries**（Default + Dark 各一份 hex）。
- **ContentDialog**：① Content 不可用仍挂视觉树上的元素（双父级 → 抛「already the child of another element」，App 层吞异常表现为「点击无效」）→ 先查 crash.log；② 复用前须 `Content = null`；③ 弹层主题不跟随 root → `RequestedTheme = ActualTheme`；④ Content 若 XAML 里 Collapsed 须弹出时手动置 Visible；⑤ 跨页共享样式放 App.xaml 级。
- **x:Bind TwoWay 绑 Selector.SelectedValue + 值类型 VM 属性是雷**（置 null → 回写拆箱 NRE）→ 一律 OneWay + SelectionChanged 手动回写；`SelectedIndex` 绑 int 可用 TwoWay。
- **无堆栈崩溃（0xc000027b）定位**：`Start-Process` + `Get-Process` 判存活（15s）+ timing.log 最后探针，每轮 ≤1 分钟的消融实验；或 `git stash` 跑 HEAD 对照 + 逐块回退二分。构建后等 30s 再启动。判活优先级：进程存活 > `overlay-hidden` 探针 > 截屏。已知诱因：模板元素挂 `PointerEntered/Exited`、模板内 x:Bind 到悬停派生属性、运行期改 Repeater 元素 `Visibility`。安全做法：挂冒泡 `PointerMoved` + `GetParent` 上溯、改动全部 `TryEnqueue`、只写渲染属性。
- **`SoftwareBitmapSource` 实测不可用**（UI 亲和 → fail-fast）；`BitmapImage` 是唯一稳定显示管线。
- unpackaged：Win11 圆角只能靠 `MicaBackdrop`（2.3.6 无 `TransparentBackdrop`）；**PRI 不索引 `<Content>` 项 → 资源按 `AppContext.BaseDirectory` 磁盘路径加载**。
- `ThemeShadow` + `Translation`：z 是投影唯一输入；`Translation` 不参与布局；`Border.CornerRadius` 会裁掉子内容投影 → 圆角图片交给 `Border.Background` 的 `ImageBrush`。
- 延伸标题栏后系统按钮前景色不随主题更新：须显式设 `AppWindow.TitleBar.Button{Foreground,Background,Inactive*}Color`，在「设置切换」与 `ActualThemeChanged` 两路径各刷一次。
- 元素外观「运行时覆盖 + 退出还原」：初值写 Style Setter，退出 `ClearValue` 回落（本地值压掉 ThemeResource 丢主题随动）。
- 绝不在运行时把页面宿主（PageHost）搬进另一容器：触发 `Page.Unloaded`，播放器页会销毁 `MediaPlayer`。
- **NavigationView 动态子项**：子项扁平进同一列表，只在父项 `IsExpanded` **值变化**时重算 → 动态填充后须先 false 再 true 强制触发。
- **NavigationView 展开交互**：①点行与点箭头都切换展开（无属性可关）；②点箭头不抛 `ItemInvoked`，点行才抛；③切换与 `ItemInvoked` 先后不定，撤销须覆盖两条路径；④**在 `ItemInvoked`/`Expanding`/`Collapsed` 回调里同步改 `IsExpanded` 会 fail-fast**（唯一线索 diag.log 心跳中断）→ 改写一律 `TryEnqueue`；⑤区分点箭头/点行只能按 `PointerPressed` 落点（`AddHandler(handledEventsToo: true)`，箭头在行右端约 44px）；⑥`Expanding`/`Collapsed` 是 NavigationView 级事件，挂到 Item 上报 WMC0011。

## 控件与布局约束
- 分组 ListViewBase 配自定义 ItemsPanel 时排列的是 `GroupItem`；无内置 Justified 布局与 GridLength 动画；自定义标题栏用 `InputNonClientPointerSource` Passthrough（矩形为物理像素须乘 `RasterizationScale`，布局/激活变化后重注册）。
- ItemsPanelTemplate 内不能 x:Bind 页面属性、不能 ElementName 跨 namescope；面板读子项走 `FrameworkElement.DataContext`。OneWay 与 `x:Load` 只支持 Page/UserControl，Window 层联动走代码后置 INPC 转发。
- ItemContainerStyle 模板内 x:Bind 根是模板化控件；页面属性须经 PageProxy。**模板内 x:Bind 禁配 StaticResource Converter**（运行时 NRE、编译期 0 警告）→ 条件显隐用 VisualState（Setter 优先级高于本地绑定值）。
- 改控件外观优先覆盖主题资源（键名 `Xxx`/`XxxPointerOver`/`XxxFocused`/`XxxDisabled`），勿重写模板；给 `MenuFlyoutItem` 自定义模板触发忙碌光标；圆角两档：4（控件）/ 8（表面）。
- `MenuFlyout` 从 `Application.Current.Resources` 取是共享单例，重复 `ShowAt` 抛 `E_INVALIDARG` → 可重复弹出用工厂方法每次 `new`。`CommandBar` 动态溢出有未修 bug（issue #6450）。
- 切换 `SelectionMode` 会重置选择 → 先抓快照再恢复；间距由面板 `Spacing` 承担、子项模板零 Margin；嵌套 ScrollViewer 内的列表须禁用自身垂直滚动；多实例 GridView 选择聚合须经实例列表（Loaded/Unloaded 登记）。
- `Page.KeyboardAccelerators` 污染页面内所有 ToolTip（by design）→ 用代码后置 KeyDown。
- **键盘事件自焦点元素向上冒泡**，焦点在页面时不下传到子级网格 → 页面级快捷键须订阅在页面自身。
- 覆盖层按钮必须就地拦截 `Tapped`（`e.Handled = true`）：Button 不吞 Tapped，会冒泡触发父容器逻辑。
- 符号码点（离屏渲染实证）：空心文件夹 `\uED25` / 实心 `\uE8B7`；线星 `\uE734` / 实心 `\uE735`；空心爱心 `\uEB51` / 实心 `\uEB52`（`Symbol.Favorite` 是爱心非星）；鼠标 `\uE962`、照片 `\uE8B9`、音乐 `\uE189`。查码点用 PowerShell + WPF `RenderTargetBitmap` 离屏渲染 PNG 目检。
- `Expander` 嵌卡片须在 `.Resources` 把三个 `Expander*BorderBrush` 与两个 BorderThickness 归零、Background 指 `SubtleFillColorTransparentBrush`。
- `ToggleSwitch` 默认 `MinWidth=154px`；`Border` 只能一个 `Child`（多项并列报 WMC0035）；Grid `Auto` 列内子元素默认左对齐；XAML 注释内不得出现连续 `--`；WinUI 3 的 `Grid` 支持 `Padding`；`NumberBox` 清空时 `Value` 为 `NaN`。

## 动画与交互
- Storyboard 优先代码后置现场创建并以元素对象为目标（XAML TargetName 解析失败即静默无动画）；`FillBehavior` 默认 `HoldEnd` → 每轮前复位起始值。
- 数据驱动动画须防重复触发；快速连发时上一轮 Completed 可能清掉下一轮的源，需容忍缺失。
- 「控件自动隐藏」一律用 `DispatcherQueue.CreateTimer()`，绝不订阅 `CompositionTarget.Rendering`。

## 性能与卡死（判别式）
- 「随条目数变卡」主因是解码提交数随页数线性放大（`pending` 取全集合 × 位图按索引置空 = 自激循环）。
- 三条铁律：容器数恒定（≤ 视口 + 2 屏）；解码请求数 = O(视口)；内存按 LRU/字节回收（淘汰须与「从未加载」区分）。
- 反模式：`ContainerFromItem` 全集合扫描取消 = O(n²)；measure 内发起解码或重建订阅 = O(n)；解码管线上的同步日志串行化解码线程。`ContainerFromItem` 不能单独作可见性判据 → 须额外记录「是否曾生成过容器」。
- 先分辨「慢」还是「冻结」，判据优先级：心跳中断 → 日志产出 → CPU。① 单核 100% + 日志停滞 = 布局死循环；② CPU 高 + 日志持续增长 = 业务慢；③ CPU 增量 0 + 日志停滞 + 全线程 Wait = 渲染停摆。死循环时托管栈为空、`crash.log` 常无痕。
- 绝不让「位图尺寸」参与任何驱动布局的属性（解码 → 比例抖动 → 重排 → 回写的环）。订阅 `CompositionTarget.Rendering` 属高危（图库点击冻结即此因）。
- LayoutCycle 真因（已根治）：loading 覆盖层与内容 GridView 同格互相失效；覆盖层须在窗口层（PageHost 兄弟位），隐藏时复位 `IsIndeterminate=false`。
- 取证：`dotnet-stack report` 判 UI 死活；TICK 心跳间隙判同步阻塞；diag.log 判管线进度。布局/上屏与 DispatcherQueue 定时器是两条生命周期，判死须分别取证；多嫌疑用叠加减法实验逐轮排除。
- 「视觉死但日志活」= 布局系统坏死而非进程死；概率性缺陷被性能优化引爆是常态，不要回滚优化，去找被掩盖的根因。
- 完整方案见 `docs/图库列表性能优化方案.md`。对标：Windows 照片应用；不参考 Lightroom。

## 异步与线程
- **MediaPlayer 的 `Source`/`Play`/`Pause` 必须在 UI 线程**：跨线程 `Play()` 会**同步挂起**（不返回、不抛异常）→ 操作 UI 亲和对象的 await 链一律不加 `ConfigureAwait(false)`。
- **「执行流无声消失」排查法**：步骤级埋点逐步逼近；fire-and-forget 里的异常是黑洞，被调方须自己 try/catch 留痕；留痕须前置到「事实成立」那一刻。
- `ConfigureAwait(true)` 不是「回到 UI 线程」；跨线程回 UI 唯一可靠手段：注入 DispatcherQueue + `TryEnqueue` + TCS（`RunContinuationsAsynchronously`）；async void 回调异常须收口到任务源。
- 绝不能 `EnqueueAsync(async () => await Xxx())` 而 Xxx 内部又 `EnqueueAsync`（自我死锁）；批量加载必须 `await Task.WhenAll`；UI 状态赋值统一放进 `EnqueueAsync`；单条 IO 必须有超时（`WaitAsync`）。
- 「任务正常完成」≠「有效工作」（WhenAll 完成但产出 0 = 全员静默失败），服务层 catch 加取证日志（类型 + HResult）。
- 页面内裸 `DispatcherQueue.GetForCurrentThread()` 会解析到实例属性（CS0176）→ 用 `Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread()` 全限定。

## 图片显示与缩略图管线
- WinUI 3 的 `Image`/`ImageBrush` 插值不可控且无 mipmap → 唯一手段是位图物理像素 ≈ 显示区物理像素。排查顺序：显示尺寸 → DPI → 位图来源 → 插值算法 → 显示端插值。
- 请求尺寸语义统一为「显示区最长边」；档位量化先乘 `RasterizationScale`；缩小 `Fant`、放大 `Cubic`（上限 2 倍）；升级加载只升不降 + 容差。**`BitmapImage` 绝不能同时设 `DecodePixelWidth` 与 `DecodePixelHeight`**。
- **静态图标/品牌图按「目标 DIP × 最大 DPI」预生成位图**，不要运行时降采样（1000px → 16 DIP 是 62.5 倍，细笔画必糊），也不要只做 1× 位图（高 DPI 反向放大同样糊）。矢量源优先（SVG 可直接作 `Image.Source`）；无矢量时由大图 Lanczos 导出并配预乘 alpha。
- 尺寸探测：图片 `BitmapDecoder.OrientedPixel*`（含 EXIF）；视频 `GetVideoPropertiesAsync()` + 按旋转标记交换宽高。
- 异步管线按线程亲和性切开：中间产物 `byte[]`，CPU 段线程池限流，只在最后一跳回 UI 线程构造 `BitmapImage`。
- 读性能日志先看计时起点；「分辨率」与「宽高比」优先级不可共用；色彩链路（`ColorManageToSRgb` + `RespectExifOrientation` + `OrientedPixel*`）已验证，勿改。
- 磁盘缓存命中时会对源文件 stat（HDD 上 200 条不可忽略），索引库已存 `file_size`/`modified_utc` 可替代。
- **视频封面取帧**：系统 `GetThumbnailAsync` 快但**取帧位置不可控**（长视频片头常黑场）→ 指定时间点须 `MediaClip.CreateFromFileAsync` + `MediaComposition.GetThumbnailAsync(pos,w,h,NearestFrame)`；只用于超门槛长视频并先判时长，失败静默回退系统缩略图 + 取证日志。
- 磁盘缓存两层语义必须分开：`Invalidate` = 内容真失效（清内存 + 磁盘），`Release` = 仅释放内存位图；误用会删光磁盘缓存。

## 骨架屏三态
- `ThumbnailPresenter` + `ThumbnailLoadState` 三态，`ThumbnailState` 是唯一数据源。**取消 ≠ 失败**，必须回落 Loading。
- WinUI 3 XAML 无 `EventTrigger`/`BeginStoryboard` → 状态驱动动画选「模板化控件 + VisualState」；`RepeatBehavior=Forever` 容器回收后不自停，`Unloaded` 里回静态态。
- `{TemplateBinding}` 一次性求值，运行期会变的属性须依赖属性回调写入；容器回收复用不会重新应用模板（`Unloaded` 改过的状态在 `Loaded` 对称恢复）。
- 「内容可绘制」与「数据已就绪」是两个时刻，可靠信号是 Image 控件级 `ImageOpened`。
- 动画异常排查顺序：就绪信号级别 → 状态被重置 → 跃迁判据 → 二次换源 → 帧间隔 → 形状/位置 → 播放时机 → 时长/缓动；小元素用 500ms。

## 窗口视觉分层（含主题）
- 自下而上：全窗口 `Image` 背景（`UniformToFill`，`RowSpan=2`，随主题换 `light.jpg`/`dark.jpg`）→ 透明标题栏与 NavigationView → 内容区半透明卡片。
- 透出背景：改 `{Default,Expanded,Top}PaneBackground` 与 `NavigationViewContentBackground` 为 Transparent（死键：`NavigationViewPaneBackground`、`NavigationViewBackground`）。
- 容器级圆角须归零（`NavigationViewContentGridCornerRadius`）否则裁掉卡片投影；Pane 与内容区竖线来自 `ContentGrid.BorderThickness.Left`；左栏圆角来自模板 `RightCornerRadiusFilterConverter`，元素级覆盖无效。
- `PixbianContentCard*` 两刷子在 ThemeDictionaries（浅色磨砂白 / 深色磨砂深灰）；不随主题的画刷放主题字典之外。
- NavigationView 纵向：Row0 `ContentTopPadding` → Row1 `HeaderContent`（MinHeight=36，`AlwaysShowHeader="False"` 可消掉）→ Row2 `ContentPresenter`。

## 视频播放（播放器页 + 短片页）
- 页面为 DI 单例；`MediaPlayer` 惰性创建（构造期创建会 `0xC000027B`），`Unloaded` 必须释放，否则解码器不回收。
- 播放态是窗口级独立分支：`PlayerRoot`（`RowSpan=2`）覆盖标题栏与内容行，切换收口在 `MainWindow.ApplyViewerChrome`；`IsViewerVisible` 仅视频路径为 true（图片走独立窗口）。
- 播放器页走专属宿主 `VideoHost`，与常态 `PageHost` 互不争抢。装载必须先让容器可见再赋 Content（往 Collapsed 容器塞内容不触发 Loaded）；卸载必须置空 Content。
- 顶栏在系统标题栏 48px 内：交互控件必须登记 Passthrough；只放行按钮不放整条顶栏；可见性变化后须延一帧重算矩形。顶栏收不到 `PointerEntered/Exited`。播放态舞台恒暗 → 系统按钮前景恒取白色；顶栏 CommandBar 右留 140px 避让系统按钮。
- **控制条/覆盖层**：顶底控制条为覆盖层（不占布局行），自动隐藏 3s。**画面之上不得叠铺满的层**（打断硬件覆盖 → 每帧合成，4K 代价显著）；被完全遮挡的全窗壁纸一并隐藏；窗口根给不透明底以争取直通。
- **「播放视频 CPU 高」判别法**：先看任务管理器 GPU 页 Video Decode 占用——0% 即软解；与「电影和电视」同文件对照。处置（代价递增）：装「HEVC 视频扩展」→ `CreateFromUri` 替换 `CreateFromStorageFile` → 引入 FFmpegInteropX。教训：未证实前勿把「覆盖层打断硬件覆盖」当根因（已证伪）。
- **FFmpegInteropX 要点**：`CreateFromStreamAsync` → `CreateMediaPlaybackItem()`，失败回退系统解码。硬约束：①`FFmpegMediaSource` 必须字段强引用（GC 回收中断播放）；②项目必须有 RID（`win-x64`）否则 native dll 不复制 → 静默回退；③RID 使产物落 `...\win-x64\`，`Pixbian.ps1` 硬编码路径须同步；④需 `CsWinRTWindowsMetadata` 指向本机已装 SDK（19041），并新报 `CsWinRT1028`（未启用 AOT 可豁免）。
- 默认 `VideoDecoderMode=AutomaticSystemDecoder`（系统解码器能播就不用 FFmpeg，AV1 即此）；要吃 dav1d 须显式 `ForceFFmpegSoftwareDecoder`；配置在 `MediaSourceConfig.Video`，线程数须显式设 `Environment.ProcessorCount`。排查看 `VIDEODEC|codec=..|mode=..` 日志。解码策略由 `IVideoPlaybackItemFactory` 统一供给。
- 许可：FFmpegInteropX Apache-2.0，FFmpeg LGPL-2.1-or-later（动态链接、须署名）→ 见 `docs/THIRD-PARTY-NOTICES.md` 与设置页「关于」。
- **短片页（Short）**：无传输控制条，单击/空格播放暂停，方向键切换；三按钮覆盖层。**背景音乐与视频严格联动**（同步暂停、片段结束即结束、切换即换曲、随机选曲）；静音同时作用于两个 player。片段策略集中在 `ShortClipPlanner`（≤60s 整段；60–100s 自 20s 截到片尾；≥100s 长度 40–80s 随机、起点不早于 20s）。

## 音乐库 / 索引 / 取数
- **音乐库独立于图库**：曲目落 `music_tracks`，**不进 `media_items`**（图库 `Kind=null` 会查出音频被反复探测）。代价：不能用 `LibraryWatcherService` → 设置变更时重扫 + 启动时从库恢复（机械盘全量扫盘须 `Task.Run`）；音频分类器与 `MediaFileClassifier` 分开。
- 索引两阶段：扫描只写文件属性，宽高时长后台分批回填；失败必须落「已失败」否则反复捞取；写回只覆盖尺寸/时长列，用户数据用 `COALESCE` 保护。
- 展示的大小/日期/时长全部来自索引库，不实时读文件系统（仅缺宽高时读文件头一次）；查看器 EXIF 面板例外。
- 库里 `taken_utc` 是文件系统时间，不是 EXIF 拍摄时间；EXIF `TakenAt` 只在查看器解析，未展示也未回写。
- 按类型 + 随机排序取候选可直接用既有 `QueryAsync`（`Kind` + `SortKey=Random` + 换种子），无需新增仓储方法。

## 产品 / 技术决策（已定）
- 定位相册浏览器 → 砍 MagicScaler，Win2D 降可选；优先「查看器两级加载 + 磁盘缩略图缓存」；基线测量优先于选型。
- 排序为 `MediaSortKey` × `SortDirection` 两维；随机排序用固定序列（`random_rank`）保证分页稳定，不用 SQL `RANDOM()`。
- 删除走回收站（`RecycleBinHelper`）并同步清索引；「从索引移除」仅删记录。EXIF 拍摄时间暂不回填。
- 图库列表性能改造阶段：P0 止损 → P1a 方形视图虚拟化 → P1b 调度器 + LRU → P2 ItemsRepeater（含自建选择服务）→ P3 稀疏数据源（可选）。
- 落地：缩略图磁盘缓存（两级哈希分桶、条目头指纹、LRU、temp + 原子 Move，IO 失败全静默）、统一解码档位、统计异步化、排序键索引 + 随机游标分页。
- 未做（勿重复评估）：非随机排序游标；机会性预取。新查询用 `EXPLAIN QUERY PLAN` 复核。
- 已推翻旧定论：① `_items` 滑动窗口「触发条件苛刻」不成立；② 埋点「发布前统一删」改为「异步化 + 默认关闭」（diag.log 是唯一线上取证手段）。

## 验证手段 / 入口排查
- 验证 UI 用截屏 + UIA 枚举 ListItem（Name + 坐标）；WinUI `TextBlock` 不把 Text 暴露为 UIA Name，按钮可用 `InvokePattern`；查控件类名用 `Inspect.exe`。链路：`Start-Process` → `AppActivate(pid)` → `CopyFromScreen`。
- 「点了没反应」先查入口是否存在（跳转常是「按 Tag 查导航项 → 找不到静默 return」）；`git log -S '<Tag>'` 为空 = 功能从未接入。
- 验证「设置即时生效」类功能必须走真实 UI 路径，不能「改配置文件 + 重启」替代（会掩盖广播订阅缺失）。
- 加过 RID 后产物移到 `win-x64` 子目录，**旧 exe 仍留在原目录且可双击启动** → 排查「改动没生效」先确认 `(Get-Process Pixbian).Path`，再确认 ffmpeg 原生库加载。

## 杂项
- NuGet 审计：常规构建用 `WarningsNotAsErrors` 豁免 NU19xx，`-p:AuditPipeline=true` 才升级为错误。
- XamlCompiler 缓存旧类型元数据：改 VM 属性类型报 CS1503 时 `dotnet clean` 即解（OneDrive 下删 obj 会被拦截）。HEIC/AVIF 依赖 WIC 编解码器扩展。
