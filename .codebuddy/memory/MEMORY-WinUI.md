# 长期记忆 · 界面与渲染分册

> `MEMORY.md` 的主题分册，收录 WinUI 3 / WASDK / XAML / 控件布局 / 缩略图 / 播放窗口 / UI 线程的已验证事实。
> 涉及界面、控件、布局、渲染、缩略图、视频播放、UI 线程的任务，动手前先读本文件。
> 只收判别式与硬约束；代码定义、具体数值、一次性排障流水进当日日志。

## 虚拟化：ItemsRepeater（已实测）
- **复用残留根治只有一条：让数据走 CollectionChanged（集合实例不变、原地 `Clear()` + 逐条 `Add()`）**。已验证无效：`ItemsSource=null → UpdateLayout → 赋新值`、`ForceCreate`、手动清 `DataContext`、延一拍赋新集合。
- 自定义 VirtualizingLayout 必须自己收拢陈旧元素（Arrange 前把不在本 pass 的元素 `Arrange(default)`）；不得改用 `Visibility`（运行期改必 fail-fast）。
- 等高视图取命中条目用 `JustifiedRepeater.GetElementIndex(element)`，不能用 DataContext（复用期停留旧条目）。
- 选择服务的「交集陷阱」：`SelectedItems = 选中集 ∩ 当前集合` 混入游离条目则恒空；除页面交互外的任何选中集合变更都必须触发 `SelectionChanged`。
- 页面级 UI 状态（选择模式/工具栏展开/侧栏开合）在集合替换前必须归零；GridView 不能外包 ScrollViewer，ItemsRepeater 必须外包。
- `ElementClearing` 不能清 DataContext；`ElementPrepared` 在布局 pass 内同步触发，改绑定属性会 fail-fast → 实际工作须 `TryEnqueue`。
- 缩略图调度器队列纪律：窗口外条目不得滞留待解队列；收编是窗口解码的唯一权威入口。

## WinUI 3 / WASDK 关键事实
- XAML 编译器路径须用 `PkgMicrosoft_WindowsAppSDK_WinUI`（旧键静默失败 → 全项目 CS0103）；net10 下 XAML 编译器 CWD 断裂须经包装脚本 `cd` 到项目目录；勿动 `BaseIntermediateOutputPath`、勿删 `obj\`。
- **C# 错误会连锁引发 XAML "Unknown type" 假错误，先修 CS**；XAML 报 `WMC0001`/`WMC9999` 先 grep C# 的 CS 错误；VS Code `.g.i.cs` 误报 CS0103 属固有限制，以 `dotnet build` 为准。
- 删除类文件前确认其内是否夹带被别处实现的契约；删「某层唯一的包使用者」后要检查下游是否靠传递引用获得该包。
- XAML 编译期不校验颜色字面量 → 「构建成功 + 启动崩溃」先 `git status` 全量排查；颜色须 8 位 `#AARRGGBB`。无 WPF 专有成员；`x:Bind` 无通知链写 OneWay 报 WMC1506 → 恒定值一律 `OneTime`。
- **`TransformToVisual(目标)` 的结果不含目标自身的 RenderTransform**（= `M目标⁻¹ · M调用者`）→ 变换挂在元素自身时，以它为参照求出的包围盒/指针坐标描述不了该变换；指针坐标与平移钳制范围须以**无变换的祖先**为参照或改用解析式计算；`GetCurrentPoint(该元素)` 同理。
- `ScrollViewer` 默认 `IsTabStop=False`，`Focus(Programmatic)` 静默失败；`FocusManager.GetFocusedElement()` 在键处理栈内返回 null → 须订阅 `FocusManager.GotFocus` 记录最近获焦元素。
- 命名空间：颜色常量在 `Microsoft.UI.Colors`；无 `Microsoft.UI.Core`（虚拟键用 `Windows.UI.Core.CoreVirtualKeyStates` + `Microsoft.UI.Input.InputKeyboardSource`）；Picker 用 `Microsoft.Windows.Storage.Pickers`（构造传 `WindowId`）；缩放比取 `XamlRoot.RasterizationScale`。
- `StaticResource` 引用不存在资源启动即崩；业务画刷一律 `ThemeResource`；**元素级 `Resources` 禁止放 `{ThemeResource}`**（布局 pass 内 realize 会 fail-fast）→ 配色覆盖放**页面级 ThemeDictionaries**。
- **ContentDialog**：① Content 不可用仍挂视觉树的元素（双父级抛「already the child of another element」，App 层吞异常表现为「点击无效」）；② 复用前须 `Content = null`；③ 弹层主题不跟随 root → `RequestedTheme = ActualTheme`；④ XAML 里 Collapsed 的 Content 须手动置 Visible；⑤ 跨页共享样式放 App.xaml 级。
- `x:Bind` TwoWay 绑 `Selector.SelectedValue` + 值类型 VM 属性是雷（置 null → 回写拆箱 NRE）→ OneWay + SelectionChanged 手动回写。
- **无堆栈崩溃（0xc000027b）定位**：`Start-Process` + `Get-Process` 判存活（15s）+ 最后探针，每轮 ≤1 分钟消融实验；或 `git stash` 跑 HEAD 对照 + 逐块回退二分；构建后等 30s 再启动。已知诱因：模板元素挂 `PointerEntered/Exited`、模板内 x:Bind 到悬停派生属性、运行期改元素 `Visibility`。
- `SoftwareBitmapSource` 实测不可用（UI 亲和 → fail-fast）；`BitmapImage` 是唯一稳定显示管线。
- unpackaged：Win11 圆角只能靠 `MicaBackdrop`；WinUI 资源按 `AppContext.BaseDirectory` 磁盘路径加载（实测 `Pixbian.pri` 同时也索引了 `Assets\*.png`，形如 `ms-resource://Pixbian/Files/Assets/...`）。
- 包徽标：任务栏/开始菜单取 `Square44x44Logo`；**App List 图标（任务栏等「无磁贴内边距」场景）走 targetsize 变体**，官方要求**三套主题并存**（默认无后缀 / `_altform-unplated` 深色 / `_altform-lightunplated` 浅色，即使图像相同也须各有独立文件）且覆盖 **14 档（16,20,24,30,32,36,40,48,60,64,72,80,96,256）**——缺任一套或任一档，系统就画「图标板」（一块随系统强调色变化的方块）并缩小图标；`scale-100/125/150/200/400` 另供磁贴等场景。限定符顺序「尺寸 → altform」；全部由 `scripts/New-AppIcon.ps1` 生成。
- 稀疏包（external location）图标解析（**已真机验证**）：包体只有清单、资源从 ExternalLocation 解析；**Shell 只按 `resources.pri` 这一名字查找资源索引**（应用自身索引为 `<AssemblyName>.pri`，不被 Shell 识别）→ 必须在 ExternalLocation 提供一份（`Register-Pixbian.ps1` ①′ 步从 `Pixbian.pri` 复制）。缺它时表现为任务栏图标带强调色「图标板」+ 图标缩小、磁贴模糊。图标资源更新后 Windows 仍按缓存渲染旧图 → 须 `ie4uinit.exe -show` 重建缓存（脚本末尾已内置）。
- **图标缩放的抗锯齿：不能用 WPF `RenderTargetBitmap` 大幅缩小**（1000→32 属严重欠采样，`BitmapScalingMode.HighQuality` 不生效，边缘半透明仅 ~0.8% → 任务栏小图标锯齿）。须用 **GDI+ `HighQualityBicubic` + 逐级减半**（每级 ≤2 倍，等效面积平均；实测 16px 半透明 25%、24px 21%、32px 13%，为正常水平）。要点：`CompositingMode.SourceCopy` 才能保持 alpha；`Bitmap(Stream)` 要求流在 Bitmap 生命周期内保持打开。判别式：边缘半透明占比 <1% = 无抗锯齿；正常小图标应 >10%。
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

## 异步与 UI 线程
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
