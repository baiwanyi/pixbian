# 长期记忆

> 只收规范、稳定事实与可复用判别式；代码定义、具体数值、一次性排障流水进当日日志。
> 2026-09-10 八度精简：合并同类、剔论证、压缩表述，恢复可完整注入。

## 项目与开发环境
- Pixbian：WinUI 3 本地相册浏览器。WASDK 2.4.0 元包（WinUI 实为 2.3.6）+ `net10.0-windows10.0.26100.0`（最低 17763，LangVersion 13）。测试基线 356。
- `dotnet` 用 `C:\Program Files\dotnet\dotnet.exe`；包管理 pnpm；构建必带 `-warnaserror`（普通构建只报警告会掩盖 CI 红，改测试/WebServer 后须 `-c Release -warnaserror` 复验）；缩进 4 空格；文件首部 3–8 行中文模块说明。
- 硬件：C SSD；D 机械盘（媒体库 `D:\Downloads\*`，余量长期偏低，查「慢/卡」先看余量）。
- OneDrive 工作区产物落盘可能被锁 → 一键脚本用「显式 build + Start-Process」两段式；构建前确认应用未运行（MSB3026）。加 RID 后产物落 `win-x64\`，旧 exe 仍在原目录且可双击启动 → 「改动没生效」先确认 `(Get-Process Pixbian).Path`。
- 终端：**传含中文命令会语法错误/乱码** → 中文 commit 用 `git commit -F <UTF-8 文件>`；含中文 `.ps1` 须 UTF-8 with BOM。诊断脚本放 `C:\Temp\`。
- PowerShell 陷阱：`@($null).Count -eq 1`（判空须过滤 `$null`）；`New-Object T (a,b)` 被当数组参数 → 用 `[T]::new()`；函数返回 `byte[]` 会被流展开成 `object[]` → 调用处须 `[byte[]](...)` 强转，否则重载解析失败**静默不写任何字节**。
- 工具事实：WAL 库用 `SqliteOpenMode.ReadWrite` 可与运行中应用并发读；`search_content` 的 glob 不支持 `!` 取反；查 MSBuild 属性 `dotnet msbuild x.csproj -getProperty:名`；`dotnet-stack report` 打托管栈；`Measure-Object -Line` 读中文 md 会少算行数（改用 `-Encoding UTF8` + `.Count`）。
- Python 3.14 + Pillow 12（无 numpy / ImageMagick / SVG 光栅化）；Pillow 12 移除 `ImageChops.divide` → 含 alpha 缩放走「预乘 → Lanczos → 反预乘」。验证 ICO 不能用 `System.Drawing.Icon` → Win32 `LoadImage(IMAGE_ICON)`。
- **MVVM Toolkit 8.4.0 的 `[ObservableProperty]` partial property 形式仅 `LangVersion=preview` 下生成实现** → `13.0`/`14.0` 报 CS9248；项目保留字段版 + `NoWarn;MVVMTK0045`。诊断：`-p:EmitCompilerGeneratedFiles=true`。
- 按行号批量改多区间必须降序；机械重排优先整文件重写。NuGet 审计用 `WarningsNotAsErrors` 豁免 NU19xx。

## 分层与依赖方向（改动前必查）
- Core 最底层、零项目引用、纯 `net10.0`；Data/Imaging/Media/WebServer 单向引用 Core，UI 引用全部；跨层数据走 `Pixbian.Core.Models`。
- Core 禁用 WIC / `Windows.Graphics.Imaging`（绑 windows TFM 会破坏 Core.Tests）→ Core 定抽象 + UI 注入实现。FFmpegInteropX 只被 UI 引用 → 复用解码策略的工厂只能在 UI 层。
- **页面需要主窗口时经 `App.Services` 按需解析**，不要注入（窗口持有页面 → 循环依赖）。
- 查看三条平行链路：双击图片 → ImageViewerWindow；双击视频 → 主窗口播放态；幻灯片 → SlideShowWindow。轻量预览：图片且主窗口未建时直接开查看器窗口、关闭 `Environment.Exit(0)`；视频仍须走主窗口。

## 编码与协作规范
- 敏感信息禁止硬编码；API 响应 DTO 白名单；日志脱敏（WinRT 异常记 HResult）；禁用 `Trace.WriteLine`（一律 `AppLog`）。
- 改动 > 5 文件须先确认是否 commit（Conventional Commits，中文内容）；> 2 文件的大改先取得用户确认方案；每次修改记当日 `.codebuddy/memory/YYYY-MM-DD.md`（超 1000 行分卷）。
- **【强制】功能是否正常一律由用户手工验证**，AI 不得用自启 + 截屏 + UIA 脚本代替人工验收；自启/截图仅用于崩溃取证。
- **【强制】会话中途磁盘文件可能被用户改动**：读到与先前不一致的内容须复核再动手。

## 通用工程方法论
- 性能定位顺序：先测真实数据规模 → 再测单点耗时 → 最后改代码；**优化前先证伪前提**。
- 后台任务让出比例比绝对时长更关键（批次 2.5s 时节流 ≥1.5s）并设批次数上限；常驻任务须节流 + 排他；排他优先 `Interlocked.CompareExchange`（持 CTS/`SemaphoreSlim` 字段触发 CA1001）。
- **查 API 是否存在一律读包内二进制**：WinRT 投影 `microsoft.windows.sdk.net.ref/<ver>/winmd/`；WinUI 读 `Microsoft.WinUI.dll` 配套 `.xml` 的 `T:`/`P:`/`M:` 索引；主题键与模板默认值读 `Themes/generic.xaml`。winmd 不可 `Assembly.LoadFrom`。
- 「慢」/「冻结」判别：①单核 100% + 日志停滞 = 布局死循环（托管栈空、crash.log 无痕）；②CPU 高 + 日志增长 = 业务慢；③CPU 增量 0 + 全线程 Wait = 渲染停摆。多嫌疑用叠加减法逐轮排除。
- 概率性缺陷被性能优化引爆是常态：不要回滚优化，去找被掩盖的根因。
- **把「为什么不做」的实测证据写进配置注释**（而非只写报告）。
- 巨型文件拆分（partial，已实测四例）：提成员签名行号 → 划职责分区 → 分段精读 → 各 partial 整文件写入 → 主文件重写 → 构建迭代修 using。纯搬运零行为变更，以 `-warnaserror` + 全量测试为护栏；类声明与基类列表只留主文件。
- 死代码审计判据：编译器恒 0 警告，须人工核对引用计数——整类查实例化 + DI 注册 + 订阅；方法查调用点；字段查「只写不读」；`x:Name` 无人用 ≠ 控件未用。

## 虚拟化：ItemsRepeater（已实测，勿再试错）
- **复用残留根治只有一条：让数据走 CollectionChanged（集合实例不变、原地 `Clear()` + 逐条 `Add()`）**。已验证无效：`ItemsSource=null → UpdateLayout → 赋新值`、`ForceCreate`、手动清 `DataContext`、延一拍赋新集合。
- 自定义 VirtualizingLayout 必须自己收拢陈旧元素（Arrange 前把不在本 pass 的元素 `Arrange(default)`）；不得改用 `Visibility`（运行期改必 fail-fast）。
- 等高视图取命中条目用 `JustifiedRepeater.GetElementIndex(element)`，不能用 DataContext（复用期停留旧条目）。
- 选择服务的「交集陷阱」：`SelectedItems = 选中集 ∩ 当前集合` 混入游离条目则恒空；除页面交互外的任何选中集合变更都必须触发 `SelectionChanged`。
- 页面级 UI 状态（选择模式/工具栏展开/侧栏开合）在集合替换前必须归零。GridView 不能外包 ScrollViewer；ItemsRepeater 必须外包。
- `ElementClearing` 不能清 DataContext；`ElementPrepared` 在布局 pass 内同步触发，改绑定属性会 fail-fast → 实际工作须 `TryEnqueue`。
- 缩略图调度器队列纪律：窗口外条目不得滞留待解队列；收编是窗口解码的唯一权威入口。

## WinUI 3 / WASDK 关键事实
- XAML 编译器路径须用 `PkgMicrosoft_WindowsAppSDK_WinUI`（旧键静默失败 → 全项目 CS0103）；net10 下 XAML 编译器 CWD 断裂须经包装脚本 `cd` 到项目目录；勿动 `BaseIntermediateOutputPath`、勿删 `obj\`。
- **C# 错误会连锁引发 XAML "Unknown type" 假错误，先修 CS**；XAML 报 `WMC0001`/`WMC9999` 先 grep C# 的 CS 错误；VS Code `.g.i.cs` 误报 CS0103 属固有限制，以 `dotnet build` 为准。
- **删除类文件前确认其内是否夹带被别处实现的契约**；删「某层唯一的包使用者」后要检查下游是否靠传递引用获得该包。
- XAML 编译期不校验颜色字面量 → 「构建成功 + 启动崩溃」先 `git status` 全量排查；颜色须 8 位 `#AARRGGBB`。无 WPF 专有成员；`x:Bind` 无通知链写 OneWay 报 WMC1506 → 恒定值一律 `OneTime`。
- **`TransformToVisual(目标)` 的结果不含目标自身的 RenderTransform**（结果为 `M目标⁻¹ · M调用者`）→ 变换挂在元素自身时，以它为参照求出的包围盒/指针坐标描述不了该变换；指针坐标与平移钳制范围必须以**无变换的祖先**为参照，或改用解析式计算。同理 `GetCurrentPoint(该元素)` 返回的坐标不受该元素自身变换影响。
- `ScrollViewer` 默认 `IsTabStop=False`，`Focus(Programmatic)` 静默失败；`FocusManager.GetFocusedElement()` 在键处理栈内返回 null → 须订阅 `FocusManager.GotFocus` 记录最近获焦元素。
- 命名空间：颜色常量在 `Microsoft.UI.Colors`；无 `Microsoft.UI.Core`（虚拟键用 `Windows.UI.Core.CoreVirtualKeyStates` + `Microsoft.UI.Input.InputKeyboardSource`）；Picker 用 `Microsoft.Windows.Storage.Pickers`（构造传 `WindowId`）；缩放比取 `XamlRoot.RasterizationScale`。
- `StaticResource` 引用不存在资源启动即崩；业务画刷一律 `ThemeResource`；**元素级 `Resources` 禁止放 `{ThemeResource}`**（布局 pass 内 realize 会 fail-fast）→ 配色覆盖放**页面级 ThemeDictionaries**。
- **ContentDialog**：① Content 不可用仍挂视觉树的元素（双父级抛「already the child of another element」，App 层吞异常表现为「点击无效」）；② 复用前须 `Content = null`；③ 弹层主题不跟随 root → `RequestedTheme = ActualTheme`；④ XAML 里 Collapsed 的 Content 须手动置 Visible；⑤ 跨页共享样式放 App.xaml 级。
- `x:Bind` TwoWay 绑 `Selector.SelectedValue` + 值类型 VM 属性是雷（置 null → 回写拆箱 NRE）→ OneWay + SelectionChanged 手动回写。
- **无堆栈崩溃（0xc000027b）定位**：`Start-Process` + `Get-Process` 判存活（15s）+ 最后探针，每轮 ≤1 分钟消融实验；或 `git stash` 跑 HEAD 对照 + 逐块回退二分；构建后等 30s 再启动。已知诱因：模板元素挂 `PointerEntered/Exited`、模板内 x:Bind 到悬停派生属性、运行期改元素 `Visibility`。
- `SoftwareBitmapSource` 实测不可用（UI 亲和 → fail-fast）；`BitmapImage` 是唯一稳定显示管线。
- unpackaged：Win11 圆角只能靠 `MicaBackdrop`；**PRI 不索引 `<Content>` 项 → 资源按 `AppContext.BaseDirectory` 磁盘路径加载**。
- 包徽标：任务栏/开始菜单取 `Square44x44Logo`；`altform-unplated` 与 `altform-lightunplated` **两套必须同时存在**（缺一套系统画「图标板」——随强调色变化的方块）；限定符顺序「尺寸 → altform」；须提供 scale-100/125/150/200/400 变体，统一由 `scripts/New-AppIcon.ps1` 生成。
- `ThemeShadow` + `Translation`：z 是投影唯一输入；`Border.CornerRadius` 会裁掉子内容投影 → 圆角图片交给 `Border.Background` 的 `ImageBrush`。
- 延伸标题栏后系统按钮前景色不随主题更新 → 显式设 `AppWindow.TitleBar.Button{Foreground,Background,Inactive*}Color`，在「设置切换」与 `ActualThemeChanged` 两路径各刷一次。
- 元素外观「运行时覆盖 + 退出还原」：初值写 Style Setter，退出 `ClearValue` 回落。绝不在运行时把页面宿主搬进另一容器（触发 `Page.Unloaded`，播放器页会销毁 `MediaPlayer`）。
- **NavigationView**：①动态子项扁平进同一列表，只在父项 `IsExpanded` 值变化时重算 → 填充后须先 false 再 true；②在 `ItemInvoked`/`Expanding`/`Collapsed` 回调里同步改 `IsExpanded` 会 fail-fast → 一律 `TryEnqueue`；③区分点箭头/点行只能按 `PointerPressed` 落点（箭头在行右端约 44px）。

## 控件、布局与动画
- 无内置 Justified 布局与 GridLength 动画；自定义标题栏用 `InputNonClientPointerSource` Passthrough（矩形为物理像素须乘 `RasterizationScale`，布局/激活变化后重注册）。
- ItemsPanelTemplate 内不能 x:Bind 页面属性、不能 ElementName 跨 namescope；OneWay 与 `x:Load` 只支持 Page/UserControl。**ItemContainerStyle 模板内 x:Bind 禁配 StaticResource Converter**（运行时 NRE、编译期 0 警告）→ 条件显隐用 VisualState；页面属性须经 PageProxy。
- 改控件外观优先覆盖主题资源（键名 `Xxx`/`XxxPointerOver`/`XxxFocused`/`XxxDisabled`）；圆角两档：4（控件）/ 8（表面）。
- `MenuFlyout` 从 `Application.Current.Resources` 取是共享单例，重复 `ShowAt` 抛 `E_INVALIDARG` → 工厂方法每次 `new`。
- 切换 `SelectionMode` 会重置选择 → 先抓快照；间距由面板 `Spacing` 承担；嵌套 ScrollViewer 内的列表禁用自身垂直滚动。
- `Page.KeyboardAccelerators` 污染所有 ToolTip → 用代码后置 KeyDown；键盘事件自焦点元素向上冒泡，页面级快捷键须订阅在页面自身。覆盖层按钮必须就地拦截 `Tapped`。
- **符号码点**：空心文件夹 `\uED25` / 实心 `\uE8B7`（`EA39`=ErrorBadge 错误徽章、非文件夹；`E8F6`=UnsyncFolder）；线星 `\uE734`/实心 `\uE735`；空心爱心 `\uEB51`/实心 `\uEB52`。查字形表用 Segoe Fluent Icons 官方文档。
- `Expander` 嵌卡片须把三个 `Expander*BorderBrush` 与两个 BorderThickness 归零；`ToggleSwitch` 默认 `MinWidth=154px`；`Border` 只能一个 `Child`；XAML 注释内不得出现连续 `--`；`NumberBox` 清空时 `Value` 为 `NaN`。
- Storyboard 优先代码后置现场创建并以元素对象为目标；`FillBehavior` 默认 `HoldEnd` → 每轮前复位起始值。「控件自动隐藏」一律用 `DispatcherQueue.CreateTimer()`，绝不订阅 `CompositionTarget.Rendering`。

## 异步与线程
- `MediaPlayer` 的 `Source`/`Play`/`Pause` 必须在 UI 线程（跨线程 `Play()` 同步挂起）→ 操作 UI 亲和对象的 await 链不加 `ConfigureAwait(false)`。
- 「执行流无声消失」排查：步骤级埋点逐步逼近；fire-and-forget 里的异常是黑洞，被调方须自己 try/catch 留痕。
- 跨线程回 UI 唯一可靠手段：注入 DispatcherQueue + `TryEnqueue` + TCS（`RunContinuationsAsynchronously`）；单条 IO 必须有超时（`WaitAsync`）。「任务正常完成」≠「有效工作」（WhenAll 完成但产出 0 = 全员静默失败）。

## 缩略图管线
- 请求尺寸语义统一为「显示区最长边」；档位量化先乘 `RasterizationScale`；缩小 `Fant`/放大 `Cubic`（上限 2 倍）。**`BitmapImage` 绝不能同时设 `DecodePixelWidth` 与 `DecodePixelHeight`**。
- 异步管线按线程亲和性切开：CPU 段线程池限流，只在最后一跳回 UI 线程构造 `BitmapImage`。磁盘缓存命中会对源文件 stat（HDD 不可忽略）→ 用索引库 `file_size`/`modified_utc` 替代；`Invalidate` = 内容真失效（清内存 + 磁盘），`Release` = 仅释放内存位图。
- 尺寸探测：图片 `BitmapDecoder.OrientedPixel*`（含 EXIF）；视频 `GetVideoPropertiesAsync()` + 按旋转标记交换宽高。视频封面指定时间点须 `MediaClip.CreateFromFileAsync` + `MediaComposition.GetThumbnailAsync`（系统 `GetThumbnailAsync` 取帧位置不可控）。
- `ThumbnailPresenter` + `ThumbnailLoadState` 驱动骨架/图片/失败三态；**取消 ≠ 失败**，必须回落 Loading。`{TemplateBinding}` 一次性求值，运行期会变的属性须依赖属性回调写入。
- XAML 无 `EventTrigger`/`BeginStoryboard` → 状态驱动动画选「模板化控件 + VisualState」；`RepeatBehavior=Forever` 回收后不自停，`Unloaded` 里回静态态。可靠就绪信号是 Image 控件级 `ImageOpened`。

## 窗口视觉分层与视频
- 视觉分层自下而上：全窗口 `Image` 背景（`UniformToFill`、`RowSpan=2`）→ 透明标题栏与 NavigationView → 内容区半透明卡片。透出背景改 `{Default,Expanded,Top}PaneBackground` 与 `NavigationViewContentBackground` 为 Transparent（死键：`NavigationViewPaneBackground`）；容器级圆角须归零。
- 页面为 DI 单例；`MediaPlayer` 惰性创建（构造期创建会 `0xC000027B`），`Unloaded` 必须释放。播放态是窗口级独立分支，切换收口在 `ApplyViewerChrome`；装载必须先让容器可见再赋 Content，卸载必须置空。
- 顶栏在系统标题栏 48px 内：交互控件必须登记 Passthrough；可见性变化后延一帧重算矩形；CommandBar 右留 140px 避让系统按钮。控制条为覆盖层（不占布局行），自动隐藏 3s；**画面之上不得叠铺满的层**（打断硬件覆盖）。
- 「播放 CPU 高」判别：先看任务管理器 GPU 页 Video Decode 占用——0% 即软解。处置（代价递增）：装 HEVC 扩展 → `CreateFromUri` 替换 `CreateFromStorageFile` → FFmpegInteropX。
- **FFmpegInteropX**：`CreateFromStreamAsync` → `CreateMediaPlaybackItem()`，失败回退系统解码。硬约束：`FFmpegMediaSource` 必须字段强引用；项目必须有 RID 否则 native dll 不复制（静默回退）；需 `CsWinRTWindowsMetadata` 指向已装 SDK，`CsWinRT1028` 可豁免。许可：Apache-2.0 + FFmpeg LGPL-2.1-or-later（动态链接、须署名）。

## 音乐库 / 索引 / 取数
- **音乐库独立于图库**：曲目落 `music_tracks`，不进 `media_items`；不能用 `LibraryWatcherService` → 设置变更时重扫 + 启动时从库恢复（机械盘全量扫盘须 `Task.Run`）。
- 索引两阶段：扫描只写文件属性，宽高时长后台分批回填；失败必须落「已失败」否则反复捞取；写回只覆盖尺寸/时长列，用户数据（收藏/评分/分类）用 `COALESCE` 保护。
- 展示的大小/日期/时长全部来自索引库，不实时读文件系统；库里 `taken_utc` 是文件系统时间而非 EXIF 拍摄时间。
- 分页：非随机排序走**键集游标**（排序值 + id 双键；OFFSET 深翻慢 151×），随机排序走固定 `random_rank` 游标；删除收缩集合后游标取已加载末条，天然不回退。

## 产品 / 技术决策（已定）
- 排序为 `MediaSortKey` × `SortDirection` 两维；随机用固定序列保证分页稳定，不用 SQL `RANDOM()`。
- 删除走回收站（`IRecycleBinService` → `Microsoft.VisualBasic.FileIO.FileSystem`）并同步清索引；「从索引移除」仅删记录。搜索保留 `LIKE '%词%'` 子串语义（FTS5 因中文 2 字词受 trigram ≥3 限制而放弃，勿重复评估）。
- 局域网共享不内置 TLS（配反向代理）；可绑定指定网卡，非法地址显式抛异常而非静默回退。
- 已推翻旧定论：「滑动窗口触发条件苛刻」不成立；埋点改「异步化 + 默认关闭」；查看器 `PreviousImage` 已正确回收。
- 用户数据备份（方案已确认）：JSON 数据包以 `path` 为条目业务键、分类/分组以 `name` 互引；导入策略「合并，导入文件为准」；仅备份扫描源与音乐目录（不含界面偏好与 Web 密码哈希）；OneDrive 同步走「启动时检查 + 补齐错过周期」，根目录探测顺序 `OneDrive` → `OneDriveConsumer` → `OneDriveCommercial` 环境变量。

## 验证手段 / 入口排查
- 验证 UI 用截屏 + UIA 枚举 ListItem（Name + 坐标）；WinUI `TextBlock` 不把 Text 暴露为 UIA Name，按钮可用 `InvokePattern`。
- 「点了没反应」先查入口是否存在（跳转常是「按 Tag 查导航项 → 找不到静默 return」）；`git log -S '<Tag>'` 为空 = 功能从未接入。验证「设置即时生效」类功能必须走真实 UI 路径。
- XamlCompiler 缓存旧类型元数据：改 VM 属性类型报 CS1503 时 `dotnet clean` 即解。HEIC/AVIF 依赖 WIC 编解码器扩展。
- 批量核对**注释与代码是否一致**：写只读脚本扫 XAML 注释（多行注释续行文字列 vs 首行、`-->` 是否与 `<!--` 同列、注释块与下方元素缩进），比人工比对可靠。
