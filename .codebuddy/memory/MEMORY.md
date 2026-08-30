# 长期记忆

## Windows 脚本规范
- 含中文的 `.ps1` 文件必须保存为 UTF-8 with BOM：Windows PowerShell 5.1 会把无 BOM 的 UTF-8 按系统 ANSI（GBK）解析，中文注释/字符串会破坏语法（报"字符串缺少终止符"）。用 `write_to_file` 写出的文件默认无 BOM，需补写。

## Pixbian 项目环境事实
- 项目品牌已于 2026-08-30 由 PhotoApps 整体更名为 Pixbian（解决方案、9 个子项目目录/程序集、命名空间、文档、脚本全部同步）。用户数据目录亦更名为 `%LOCALAPPDATA%\Pixbian`，并内置旧目录一次性自动迁移（`AppPaths.MigrateLegacyDirectory`）。
- 本机 `dotnet` 不在用户 PATH，命令行需用绝对路径 `C:\Program Files\dotnet\dotnet.exe`（或 Developer PowerShell）。仓库根目录已提供 `run.ps1` / `run.cmd` 一键构建并启动应用。
- 项目为 OneDrive 同步目录：新构建产物落盘后立即启动 exe 可能被同步/杀软瞬时锁定导致秒退，属环境竞态。
- 工作区源文件用 `dotnet run` 偶发无输出挂起（残留 build server 节点 + OneDrive 锁），一键脚本采用「显式 build + Start-Process」两段式绕开。
- 本机命令行删除文件受「Safe-Delete」安全钩子拦截（文件不存在也会中断整个复合命令），PowerShell 复合命令中避免 `Remove-Item` 临时诊断文件。
- 大规模文件/目录批量改名的实用做法：写纯英文 `.ps1`（规避 BOM 问题）批量替换文本后再改名，替换时按原文件 BOM 状态回写；改名目录前必须先 `dotnet build-server shutdown`，否则 MSBuild 节点锁 bin/obj 导致失败。
- **顶层工作区文件夹无法在 AI 会话内重命名**：IDE 进程自身持有工作区根目录句柄（叠加 OneDrive `FileCoAuth`），`Rename-Item` 恒定报「文件正在使用中」，切换进程 CWD、独立进程、多次重试均无效。需用户关闭 IDE 后用资源管理器手动改名。
- **判断某路径是否被 git 忽略，只能用 `git check-ignore -v <path>`，不可用文本搜索 `.gitignore` 下结论**：片段正则（如 `bin|obj`）会因大小写变体（`[Bb]in/`）与字符类语法产生假阴性，曾据此误报「.gitignore 未忽略 bin/obj」而实际规则完善。凡涉及忽略判定的结论，必须以 `git check-ignore` 输出为准。
- 仓库的 `.gitignore` 已包含完整的 .NET 忽略规则（bin/obj/artifacts/.vs/覆盖率/本地密钥/NuGet 等），无需再补充基础项。
- 项目已在 `Directory.Build.props` 启用 `ImplicitUsings`，**不要手动添加 `System`、`System.Linq`、`System.IO`、`System.Collections.Generic` 等隐式 using**（会与既有代码风格不一致，且属冗余）。仅第三方与非隐式命名空间（如 `Microsoft.UI.Xaml.*`、`Windows.Foundation`）才需要显式 using。
- CI 以 `-warnaserror` 提升警告为错误（本地 `TreatWarningsAsErrors=false`），故改动应尽量做到 0 警告。

## 自动化验证 WinUI 交互的可靠手段（本机实测）
- **模拟鼠标完全不可用**：`SetCursorPos`、`mouse_event(MOUSEEVENTF_ABSOLUTE)`、`[System.Windows.Forms.Cursor]::Position`（配窗口置顶/激活）均无法触发 WinUI 的指针相关事件——既触发不了 `PointerEntered`（PointerOver 视觉状态），也触发不了 `ItemClick`。
- **UIA `SelectionItemPattern.Select()` 也不触发 `ItemClick`**：它只改变选择状态。凡业务状态写在 `ItemClick`/`Tapped` 里（而非 `SelectionChanged`），Select() 无法驱动。
- **可靠替代**：① 验证视觉状态 → `VisualStateManager.GoToState(控件, "PointerOver", true)` 直接激活，走的是同一状态机路径；② 验证选中联动 → 直接在代码后置设置 ViewModel 对应属性（如 `_gallery.SelectedItem = item`），并在属性通知处打日志观察结果。两者都比模拟输入可靠。
- WinUI 应用的 `$p.MainWindowHandle` 常为 0，取窗口句柄须用 UIA 的 `$win.Current.NativeWindowHandle`。
- UIA 按 `NameProperty` 搜 WinUI 的 `TextBlock`/`Image` 通常搜不到（不暴露自动化节点），不能据此断言 UI 未显示；判断应改用日志 dump 真实属性值。

## WinUI 3 平台约束（Pixbian）
- 分组 ListViewBase（GridView/ListView）配自定义 ItemsPanel 时，面板排列的是 `GroupItem` 组容器而非条目容器，组内条目回落到内建竖排 StackPanel——自定义行式布局（如 JustifiedPanel）在分组场景必须用「每组一个非分组控件实例」的组合结构（ItemsControl 按组迭代 + 组内非分组 GridView），不能指望面板自动感知分组。
- ItemsPanelTemplate 内元素无法 x:Bind 页面属性、无法 ElementName 跨 namescope 引用、DataTemplate 内 DataContext 是条目而非页面——模板内的动态值（如 RowHeight）须经代码后置（Loaded 事件 + 视觉树查找）设置，传统 Binding 仅在「页面 DataContext=自身且模板不在 DataTemplate 内」时可用。
- 嵌套在外层 ScrollViewer 中的内层 GridView 须设 `ScrollViewer.VerticalScrollMode/VerticalScrollBarVisibility="Disabled"` 防止滚动劫持；GridView 多实例时选择聚合与 SelectionMode 联动须经实例列表（Loaded/Unloaded 登记）管理。
- 自定义 ItemsPanel 解析子项数据须优先读 `FrameworkElement.DataContext`，而非 `ContentControl.Content`——GridViewItem 的 Content 是 DataTemplate 根（如 Border），ViewModel 只存在于 DataContext。
- 依赖宽高比/尺寸的布局要确保数据源真实：若布局依赖缩略图位图尺寸，**系统缩略图 API 的 ThumbnailMode.PicturesView / VideosView 返回的是居中裁剪的方形缩略图**（PixelWidth == PixelHeight），必须改用 `ThumbnailMode.SingleItem` 才能拿到原图纵横比。
- 计算属性（如 `AspectRatio`）依赖其他可观察属性时，须在其依赖属性变更回调中手动触发 `OnPropertyChanged`，否则布局面板收不到变更通知不会重测。
- **x:Bind 默认 Mode=OneTime**：凡绑定到「异步/后续会变化的属性」（如缩略图、元数据、加载状态）的 `Visibility`/`Text` 等，必须显式写 `Mode=OneWay`，否则只会取首帧值且永不刷新——这是「数据明明加载成功、界面却不变」类问题的首要排查点。
- **把对象本身绑给 BoolToVisibility 表达「存在即显示」时，转换器必须支持非空判定**：只认 `is bool` 会让对象值恒为 false（内容永隐、占位永显）。本项目 `BoolToVisibilityConverter` 已改为「布尔按值、其余按非空」。
- **WinUI 3 的 `Image` 控件不暴露 UIA 自动化节点**（ControlView 与 RawView 均查不到），不能用 Image 元素数量判断图片是否显示；界面验证须用截屏（CopyFromScreen + 存 PNG）判读，截图前用 `SetWindowPos(HWND_TOPMOST)` + `WindowPattern.SetWindowVisualState(Maximized)` 保证窗口可见且够大。
- **改 WinUI 3 内置控件的视觉状态，优先覆盖主题资源，而不是重写控件模板**：控件模板里 `VisualState/Storyboard` 的 `DiscreteObjectKeyFrame` 值几乎都是 `{ThemeResource Xxx}`，而 ThemeResource 沿视觉树向上查找、元素级优先。因此在控件（或其容器）的 `Resources` 里定义同名资源即可改变状态行为。例：让 `AutoSuggestBox`/`TextBox` 聚焦时底部不加粗不变色，只需两份资源：
  `<Thickness x:Key="TextControlBorderThemeThicknessFocused">1</Thickness>`（默认 `1,1,1,2`）
  `<StaticResource x:Key="TextControlBorderBrushFocused" ResourceKey="TextControlBorderBrush" />`（默认含 `SystemAccentColorLight2` 的强调色渐变）
  代码后置直接改 `BorderElement` 的 Brush 无效——视觉状态动画会在聚焦时覆盖回去。
- **查 WinUI 内置控件模板/资源默认值，直接读 NuGet 包里的 generic.xaml**，不要凭记忆猜：`~/.nuget/packages/microsoft.windowsappsdk/<版本>/lib/net6.0-windows10.0.18362.0/Microsoft.WinUI/Themes/generic.xaml`。键名、默认值、TemplateBinding 关系都能查到。
- **`TextControlBorderBrush` 不是纯色，而是 `TextControlElevationBorderBrush` 竖向渐变**（generic.xaml Light 7737 / Dark 2183）：上淡下深，底部是 `ControlStrongStrokeColorDefault`（浅色 #72000000，45% 黑），所以输入框看起来「底部有一条黑边」，这是 WinUI 的刻意设计。改为纯色的方式同样是覆盖主题资源：
  `<StaticResource x:Key="TextControlBorderBrush" ResourceKey="ControlStrokeColorSecondaryBrush" />`（浅色 #29000000 / 深色 #18FFFFFF）。
  备选：`ControlStrokeColorDefaultBrush`（#0F000000，极淡）、`ControlStrongStrokeColorDefaultBrush`（#72000000，偏重）；要固定色值则写 `<SolidColorBrush x:Key="TextControlBorderBrush" Color="#E5E5E5" />`。
- **元素级 `Resources` 里用 `StaticResource` 引用主题字典中的资源（如 `ControlStrokeColorSecondaryBrush`）实测可行**，不必担心加载期解析顺序问题。
- **改 WinUI 视觉状态的资源键名规律**：`<XxxBrush>`（Normal）、`<XxxBrush>PointerOver`（悬停）、`<XxxBrush>Focused`（聚焦）、`<XxxBrush>Disabled`。以 TextBox/AutoSuggestBox 边框为例（generic.xaml 行号）：
  `TextControlBorderBrush`（Normal，16106 行）、`TextControlBorderBrushPointerOver`（PointerOver，16108/16112 行）、`TextControlBorderBrushFocused`（Focused）。
  覆盖这些键即可改各状态外观，无需重写控件模板。
- **验证视觉状态不要用模拟鼠标**：`SetCursorPos`、`mouse_event(MOUSEEVENTF_ABSOLUTE)`、外部脚本 `Cursor.Position` 在无交互会话中都无法触发 `PointerEntered`/`PointerOver`（合成输入被过滤）。
  可靠做法是 `VisualStateManager.GoToState(控件, "PointerOver", true)` 直接激活——它走与真实悬停相同的状态机路径，再 dump 目标元素属性。
  另注：WinUI 应用的 `$p.MainWindowHandle` 常为 0，须用 UIA 的 `$win.Current.NativeWindowHandle` 取句柄。
- **`AutoSuggestBox` 上的 `Background/BorderBrush/BorderThickness/CornerRadius` 会通过 TemplateBinding 传给内部 `TextBox`**（generic.xaml 的 `DefaultAutoSuggestBoxStyle`），设在 AutoSuggestBox 上即为内部 TextBox 的初始值，是有效的。
- **验证控件视觉状态最可靠的手段是运行时 dump，而不是截图**：临时在代码后置遍历视觉树找到目标元素（如 `BorderElement`），把 `BorderThickness` 与 `BorderBrush`（SolidColorBrush 输出 Color、LinearGradientBrush 输出各 GradientStop）写进日志，并在状态切换（如 `Focus()`）前后各记录一次对比。这比肉眼看截图精确，且不受「模型读不了图」的限制；验证完移除诊断代码。
- **标题栏搜索框宽度做「最大化 720px、窗口变小随可用空间收缩」时**，用外层 `Grid` 设 `MaxWidth="720"` + 内部 `AutoSuggestBox` `HorizontalAlignment="Stretch"` 最简洁；同时用 `Margin` 控制左右留白。
- **避免启动时搜索框自动聚焦**：在根 `Grid` 的 `Loaded` 中调用 `NavigationViewControl.Focus(FocusState.Programmatic)`（`Window` 本身没有 `Loaded` 事件）。
