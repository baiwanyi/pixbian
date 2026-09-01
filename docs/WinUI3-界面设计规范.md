# Pixbian WinUI 3 界面设计规范

> 本规范依据微软官方 Fluent Design / WinUI 3 设计指南（Fluent 2 体系）编写，并结合 Pixbian 项目既有实践沉淀，作为全部 UI 开发与代码评审的**强制基线**。
> 凡标注「实测」的数值，均取自本机 Windows App SDK 内置 WinUI 主题字典 `generic.xaml`（检索方法见附录 A）；其余数值来自微软官方设计文档。
> 适配基线：Windows App SDK 2.4.0 / .NET 8 / 最低 OS 17763（Win10 1809），x64 + ARM64。

---

## 1. 总则

### 1.1 设计原则

| 原则 | 含义 | 落地要求 |
|------|------|----------|
| 内容优先 | 界面服务于内容（照片、相册），Chrome 最小化 | 画廊页不堆装饰；控件密度服从浏览效率 |
| 一致性 | 同一操作、同一含义，全应用表现一致 | 同类控件用同一资源键；图标、术语不重复造 |
| 主题自适配 | 浅色 / 深色 / 高对比度三主题一等公民 | 颜色一律经 `ThemeResource`，禁止硬编码 |
| 包容性 | 键盘、触控、屏幕阅读器、文本缩放均可用 | 见 §10 无障碍；验收清单逐项检查 |
| 性能即体验 | 流畅滚动、即时反馈 | 大列表虚拟化；动效只走合成属性 |

### 1.2 分级约定

与仓库编码规范一致：**【必须】**（违反即评审打回）、**【推荐】**（例外需说明理由）、可选。

---

## 2. 布局与间距

### 2.1 4px 网格

- 【必须】所有边距、间距、控件尺寸取 4 的倍数；精细微调允许 2px。
- 常用档位：

| 档位 | 用途 |
|------|------|
| 4 | 图标与文本之间、紧凑内边距 |
| 8 | 同组相关元素之间 |
| 12 | 相邻控件之间（如按钮组） |
| 16 | 内容区边距、卡片内边距（Flyout 内容实测 `16,15,16,17`） |
| 24 | 区块之间 |
| 32 / 40 / 48 | 页面级大区块分隔 |

### 2.2 页面结构（桌面宽屏）

```
┌────────────────────────────────────────────┐
│ 自定义标题栏（拖拽区，含窗口控制按钮占位）     │
├──────────┬─────────────────────────────────┤
│          │  页面标题（TitleLarge/Title）     │
│  导航     │  内容区（外边距 ≥ 16px，         │
│ Navigation│  画廊宽屏可 24~32px）            │
│  View    │                                 │
└──────────┴─────────────────────────────────┘
```

- 【必须】窗口背景启用 Mica：`SystemBackdrop = new MicaBackdrop()`；内容区为「层」，不再另铺不透明底色。
- 【推荐】内容区左右边距 16px 起，照片画廊等沉浸场景可加大至 24~32px。

### 2.3 响应式断点

| 窗口宽度 | 档位 | 建议行为 |
|----------|------|----------|
| < 640 px | 紧凑 | NavigationView 切 `LeftCompact` 或 `LeftMinimal`；画廊单列/双列 |
| 640–1007 px | 中等 | 适度多列；侧面板可折叠 |
| ≥ 1008 px | 宽松 | 完整布局；Justified 多行画廊 |

- 【必须】断点切换用 `AdaptiveTrigger`（VisualState）或 NavigationView 内建自适应，禁止 C# 里监听 `SizeChanged` 改布局参数。

---

## 3. 排版（Typography）

### 3.1 字体族

- 【必须】UI 文本使用默认 `XamlAutoFontFamily`（Win11 自动映射 Segoe UI Variable，Win10 回退 Segoe UI），禁止硬编码字体名。
- 【必须】图标字体使用主题资源 `SymbolThemeFontFamily`（实测值：`Segoe Fluent Icons,Segoe MDL2 Assets`，自带回退链）。

### 3.2 Type ramp（官方标准 + 实测资源值）

| XAML 样式 | 字号（实测资源） | 行高（官方） | 字重 | 用途 |
|-----------|------------------|--------------|------|------|
| `DisplayTextBlockStyle` | 68 | 92 | SemiBold | 首屏焦点大字（极少用） |
| `TitleLargeTextBlockStyle` | 40 | 52 | SemiBold | 页面主标题 |
| `TitleTextBlockStyle` | 28 | 36 | SemiBold | 大区块标题 |
| `SubtitleTextBlockStyle` | 20 | 28 | SemiBold | 卡片 / 分区标题 |
| `BodyStrongTextBlockStyle` | 14 | 20 | SemiBold | 强调正文、列表主行 |
| `BodyTextBlockStyle` | 14 | 20 | Regular | 正文默认 |
| `CaptionTextBlockStyle` | 12 | 16 | Regular | 辅助说明、时间戳、角标 |

规则：

- 【必须】一律通过上述 `TextBlockStyle` 控制文字，禁止逐个设置 `FontSize` / `FontWeight`。
- 【必须】中文文本字号不低于 12px（即 Caption 为下限）。
- 【推荐】单行截断用 Base 样式自带的 `CharacterEllipsis`；需换行的显式设 `TextWrapping="Wrap"` + `MaxLines`，避免高度抖动。
- 大标题样式已内置 `OpticalMarginAlignment="TrimSideBearings"`（消除字形侧边距），不要覆盖。

---

## 4. 颜色与主题

### 4.1 硬性规则

- 【必须】XAML 中禁止出现 `#FFxxxxxx` 形式的前景/背景硬编码；一切颜色经 `{ThemeResource ...}` 取用。
- 【必须】每处界面必须在 Light / Dark / High Contrast 三主题下人工验证。

### 4.2 强调色（Accent）

- 使用系统强调色及其派生：`SystemAccentColor`、`SystemAccentColorLight1~3`、`SystemAccentColorDark1~3`，覆盖选中态、焦点、链接、进度、滑块。
- 【必须】不新造强调色变体、不随主题改强调色语义。

### 4.3 文本层级

| 画刷 | 不透明度（实测值域） | 用途 |
|------|----------------------|------|
| `TextFillColorPrimaryBrush` | 100% | 主文本 |
| `TextFillColorSecondaryBrush` | ≈61% | 副标题、说明 |
| `TextFillColorTertiaryBrush` | ≈48% | 占位符、弱提示 |
| `TextFillColorDisabledBrush` | ≈36% | 禁用文本 |

### 4.4 表面分层（Fluent 2 Layering）

| 层 | 画刷 | 说明 |
|----|------|------|
| 窗口底 | Mica 材质 | 见 §2.2 |
| 内容层 | `LayerFillColorDefaultBrush` | 覆盖在 Mica 上的内容容器 |
| 卡片 | `CardBackgroundFillColorDefaultBrush` + `CardStrokeColorDefaultBrush` | 相册卡片、信息卡 |
| 分隔线 | `DividerStrokeColorDefaultBrush` | 列表分隔 |

### 4.5 控件与语义色

- 控件底色按状态取 `ControlFillColorDefault/Secondary/Subtle/TransparentBrush`，描边取 `ControlStrokeColorDefault/SecondaryBrush`。
- 选中态用 `AccentFillColorDefaultBrush` / `AccentTextFillColorPrimaryBrush`。
- 语义状态：`SystemFillColorSuccessBrush` / `CautionBrush` / `CriticalBrush`（配合 InfoBar 等专用画刷），不得挪作装饰。

---

## 5. 圆角与描边

实测主题资源（官方圆角体系只有两档）：

| 资源键 | 实测值 | 适用 |
|--------|--------|------|
| `ControlCornerRadius` | `4,4,4,4` | 按钮、输入框、列表项、卡片内控件 |
| `OverlayCornerRadius` | `8,8,8,8` | ContentDialog、Flyout、TeachingTip、MenuFlyout 等浮层 |
| `GridViewItemCornerRadius` / `ListViewItemCornerRadius` | 4 | 集合项 |
| `ListViewItemSelectionIndicatorCornerRadius` | 1.5 | 选中指示条 |

- 【必须】自绘卡片容器圆角用 8（表面层级），其内嵌控件用 4，不得发明第三档。
- 【必须】修改圆角只能在元素 `Resources` 覆盖同名主题键（或控件自带的 `CornerRadius` 属性），不得重写模板硬塞。

---

## 6. 图标（Iconography）

- 字体：Segoe Fluent Icons（经 `SymbolThemeFontFamily`）。
- 完整图标速查与视觉预览见 [`docs/Segoe-Fluent-Icons-图标速查与预览.html`](./Segoe-Fluent-Icons-图标速查与预览.html)：内嵌微软官方全量 1533 个字形，支持按名称/码点搜索、字号切换、点击复制 XAML 码点；取用图标时以该文档为准。
- 用法优先级：`SymbolIcon`（Symbol 枚举够用时）> `FontIcon` + `Glyph`（需精确控制字号/回退）> `PathIcon`（自定义矢量，最后手段）。
- 尺寸约定：控件内嵌图标 16px（默认）；命令区/导航 16~20px；空态插图 40~48px；官方推荐字号仅 16 / 20 / 24 / 32 / 40 / 48 / 64，偏离会发虚。
- 【必须】同一操作全应用同一图标；可点击图标必须包在按钮内，不得裸 `TextBlock` 充当按钮。
- 【必须】前缀 `E0xx–E5xx`（如 E001、E5B1）已标记旧版并弃用，新代码禁止使用。
- 常用 Glyph 摘录：Add `E710`、Back `E72B`、Settings `E713`、Search `E721`、Delete `E74D`、Save `E74E`、Play `E768`、Pause `E769`、Heart `EB51`、HeartFill `EB52`。

---

## 7. 控件规范

默认尺寸中标注「实测」者来自本机 `generic.xaml`：

| 控件 | 默认尺寸 | 使用场景 | 禁忌 |
|------|----------|----------|------|
| Button | 行高 32 | 命令触发；每屏一个主按钮（Style=Accent） | 禁止用默认按钮承载危险操作 |
| HyperlinkButton | — | 页内导航 / 外链 | 不用于执行命令 |
| ToggleSwitch | — | 即时生效的二态开关 | 需「提交」才生效的用 CheckBox |
| CheckBox / RadioButtons | — | 多选 / 3~6 项单选 | 超过 6 项改 ComboBox |
| ComboBox | MinWidth 64（实测），弹层 ≥ 80 | > 6 项选择 | 不可多选 |
| TextBox / AutoSuggestBox | MinHeight 32、MinWidth 64（实测） | 文本输入 / 建议 | 不做按钮用 |
| GridView | 项 MinHeight 44（实测） | 网格集合、多选；照片网格见 §12 | — |
| ListView | — | 单列数据流 | 大数据+自定义布局用 ItemsRepeater |
| ItemsRepeater | — | 海量数据 + 自定义布局（无内建选择模型） | 需要选择模型时选 GridView |
| NavigationView | Compact 面板 48（实测）、展开默认 320 | 应用级导航 | 不做页内 Tab 切换 |
| CommandBar / AppBar | MinHeight 56（实测） | 页面高频命令 | 低频命令收进「更多」 |
| ContentDialog | 圆角 8 | 强模态确认 | 【必须】同屏仅允许一个实例 |
| Flyout / MenuFlyout | Min 96×40（实测） | 轻量浮层、右键菜单 | 不承载复杂表单 |
| ToolTip | 字号 12（实测） | 纯补充说明 | 必要信息不得只放 ToolTip |
| InfoBar / TeachingTip / Expander | — | 状态通知 / 新功能引导 / 渐进披露 | 不互相替代 |
| ProgressBar | MinHeight 4（实测） | 确定进度 | 不确定等待用 ProgressRing |

- 【必须】控件五态 `Normal / PointerOver / Pressed / Focused / Disabled` 视觉完备（默认模板已含；自绘控件必须用 VisualState 补齐）。
- 【必须】右键操作必须同时提供等价菜单项 / 按钮（触屏与键盘可达）。

---

## 8. 动效（Motion）

### 8.1 实测时长资源（以主题资源引用，勿散写魔法数）

| 资源键 | 实测值 | 用途 |
|--------|--------|------|
| `ControlFasterAnimationDuration` | 83ms | 颜色 / 透明度微反馈 |
| `ControlFastAnimationDuration` | 167ms | 悬停、按下、小型浮层 |
| `ControlNormalAnimationDuration` | 250ms | 标准状态切换 |
| `SplitViewPaneAnimationOpenDuration` | 200ms | 侧面板展开（关闭 100ms） |

### 8.2 原则

- 动效必须有明确目的：状态反馈、空间连续性、引导注意；装饰性动画禁止。
- 自定义时长区间 100~350ms；超过 500ms 禁止。
- 缓动：优先 XAML 内置过渡（`EntranceThemeTransition` 进场、`RepositionThemeTransition` 重排、`AddDeleteThemeTransition` 增删）；Composition 自定义动画统一用 `CubicBezier(0,0,0,1)`。
- 【必须】动画属性优先合成层（`Opacity` / `Offset` / `Scale`），避免触发布局重算；长列表滚动期间不做重动效。
- 【推荐】缩略图 → 详情页用 `ConnectedAnimationService` 保持空间连续性。
- 【必须】尊重系统「动画效果」开关（设置 > 辅助功能 > 视觉效果），关闭动画时直接呈现终态。

---

## 9. 窗口与标题栏

- 主窗口启用 Mica（§2.2）；窗口圆角与阴影由系统呈现，不自绘。
- 自定义标题栏两条路线：
  1. 简单场景：`Window.ExtendsContentIntoTitleBar = true` + `SetTitleBar(element)`；
  2. 复杂交互区（**项目现状**）：`AppWindow.TitleBar` + `InputNonClientPointerSource` Passthrough 声明交互区——官方推荐方案，保持原样，勿改动架构。
- 窗口图标：`AppWindow.SetIcon(path)`；建议设置最小窗口尺寸（`OverlappedPresenter.PreferredMinimumWidth/Height`）防破版。
- 【必须】DPI / 缩放一律取 `XamlRoot.RasterizationScale`；禁止 `DisplayInformation.GetForCurrentView()`（已弃用且易抛异常）。

---

## 10. 无障碍与输入

- 【必须】对比度：正文文本 ≥ 4.5:1；大文本（≥18pt 或 ≥14pt SemiBold）与 UI 组件 / 图形 ≥ 3:1。
- 【必须】可交互元素有效命中区 ≥ 44×44 effective px（触控基准；纯鼠标场景建议 ≥ 32×32）。小视觉尺寸用透明内边距补足命中区。
- 【必须】键盘可达：Tab 序与视觉序一致；列表 / 网格支持方向键移动；所有命令有可触发的键盘路径。
- 【必须】保持系统焦点框（`UseSystemFocusVisuals` 默认值），不得移除或改为不可见。
- 【必须】纯图标按钮必须设置 `AutomationProperties.Name`；为自动化测试保留稳定 `AutomationId`。
- 【必须】文本缩放（`IsTextScaleFactorEnabled` 默认开启）在 225% 下布局不破版。
- 高对比度主题靠主题资源自动适配；禁止在 High Contrast 主题上叠加任何硬编码色。

---

## 11. 主题定制手法（项目实测沉淀）

- 改外观的优先级：**覆盖主题资源 > 元素级 `Resources` 同名键 > 重写 ControlTemplate（最后手段）**。
- 主题资源键名规律：`Xxx`（Normal）/ `XxxPointerOver` / `XxxPressed` / `XxxFocused` / `XxxDisabled`。ThemeResource 沿视觉树向上查找、元素级优先。
- 【必须】**`StaticResource` 无法解析 `ThemeDictionaries` 内的资源**：`StaticResource` 在加载时一次性查找、不能穿透主题字典。业务侧要以 `StaticResource` 引用的画刷，必须定义在 `App.xaml` 顶层、**主题字典之外**（见 `App.xaml` 中卡片底色与描边的定义方式）。反之，需要随主题联动的值一律用 `ThemeResource`。（初版文档此处记为「可行」，实测为解析失败，已更正。）

示例（TextBox 聚焦边框由默认 `1,1,1,2` 改为均匀 1px，消除底部加粗感）：

```xml
<Grid.Resources>
    <Thickness x:Key="TextControlBorderThemeThicknessFocused">1</Thickness>
</Grid.Resources>
```

- 【必须】禁止代码后置改视觉树内部 `BorderElement` 之类的字段——会被视觉状态动画覆盖回去（实测结论）。
- 【推荐】`AutoSuggestBox` 外层设置的 `Background` / `BorderBrush` / `CornerRadius` 会经 TemplateBinding 传入内部 TextBox，是合法的初始值设定方式。

---

## 12. Pixbian 项目专属约定

以下条目为项目实战结论，与官方规范同等效力：

1. **Justified 画廊结构**：**单个非分组 GridView + 自实现 `JustifiedPanel`**，条目纯平铺、不按日期分组；方形视图同样是非分组 GridView，换用内建 `ItemsWrapGrid`。WinUI 至今无内置 Justified 布局，自实现面板不可替换；自定义面板解析子项数据读 `FrameworkElement.DataContext`（不是 `ContentControl.Content`）。（初版文档记为「`ItemsControl` 按组迭代 + 组内 GridView」，实际未采用分组——分组 GridView 配自定义 ItemsPanel 时面板只能拿到 `GroupItem` 组容器、条目根本不渲染；两视图均已改为整库非分组流。）
2. **间距规则**：缩进 / 间距统一由面板 `Spacing` 承担，子项模板保持零 `Margin`【必须】——否则内容区比分配尺寸少 `Spacing`，宽高比与位图失配，照片会变形或缩略图边缘露出背景色。
3. **x:Bind 默认 OneTime**：绑定会变的属性（缩略图、加载态）必须显式 `Mode=OneWay`【必须】；`ItemsPanelTemplate` 内不能 `x:Bind` 页面属性、不能 `ElementName` 跨 namescope，动态值用代码后置（`Loaded` + 视觉树查找）设置。
4. **嵌套滚动**：嵌套在 ScrollViewer 内的 GridView 必须 `VerticalScrollMode="Disabled"`、`VerticalScrollBarVisibility="Disabled"`。
5. **多实例选择聚合**：多个 GridView 的选中聚合须经实例列表（`Loaded` / `Unloaded` 登记），不得用静态事件。
6. **计算属性**：如 `AspectRatio` 这类派生值，须在依赖属性变更回调里手动 `OnPropertyChanged`。
7. **缩略图管线**：请求尺寸先乘 `RasterizationScale` 换算物理像素、再量化到固定档位（`ThumbnailSizes.DecodeBuckets`）；`BitmapImage` 严禁同时设置 `DecodePixelWidth` 与 `DecodePixelHeight`（语义是拉伸变形，须先判方向只设一维）；高质量降采样用 `BitmapDecoder` + `BitmapTransform`（缩小插值 `Fant`、放大 `Cubic` 且上限 2 倍、`RespectExifOrientation` + `OrientedPixel*`、`ColorManageToSRgb`）；「先模糊后清晰」二次加载只升不降（配 `_inflightSize` 防重入 + 容差）。
8. **弃用 API 禁用清单**：`Window.Current`、`DependencyObject.Dispatcher`、`FocusManager.GetFocusedElement`、`SystemBackdropHost`、`WrapPanel.HorizontalSpacing`、`Windows.Storage.Pickers`（统一用 `Microsoft.Windows.Storage.Pickers`，构造传 `WindowId`）。
9. **代码风格**：4 空格缩进、文件头 3~8 行中文 JSDoc 模块注释、`ImplicitUsings` 已开启（勿手加 `System` 等隐式 using）、CI `-warnaserror` 0 警告——详见仓库根编码规范。

### 12.1 布局与渲染（踩坑集）

以下为调试成本极高的实测结论，改布局或动画前必读。

**LayoutCycle（布局死循环）**

- **判别式：CPU 单核 ~100% + 业务日志 0 增长 = 布局/渲染死循环**；CPU 高但日志持续增长 = 业务慢。
  死循环时托管堆栈常为空、`crash.log` 不留痕迹，症状是「进程活着、界面完全点不动」。
- 【必须】**绝对不要让「缩略图 / 降采样位图的尺寸」参与任何驱动布局的属性**。位图按档位量化解码，
  宽高比相对原图有微小偏差；一旦它覆盖已有准确值，就形成
  **解码 → 宽高比抖动 → 重排 → 回写显示尺寸 → 再解码** 的环。位图尺寸只能作兜底
  （预取与索引均无尺寸时），**优先级必须排最后**。「宽高比是相对值所以用位图无害」是错误判断——
  相对值同样被量化误差污染，且它驱动布局，抖动会被面板放大成循环。
- 【必须】**覆盖层与内容网格同处一个布局容器时，同帧内既替换整页条目又折叠覆盖层会让两者测量互相失效**。
  撤 loading 覆盖层前须 `await` 一个渲染帧。后台优化让前置步骤耗时归零时，这两步会撞进同一帧，
  把偶发缺陷变成必发。
- `ProgressRing` 的模板动画**参与布局测量**，与同格大重排同帧会概率性 `LayoutCycleException`
  （启动即崩、进程仍活）。换指示器不能根治——根治靠**把 loading 覆盖层放到窗口层（PageHost 的兄弟位）**。
  取证靠二分 + 连启观察（至少 8 秒 × 多次，6 秒窗口不够）。
- **概率性缺陷被性能优化引爆是常态**：不要回滚优化，去找被掩盖的根因。

**模板、绑定与状态**

- 【必须】**`ItemContainerStyle` 模板内 `x:Bind` 的根是模板化控件**，页面属性须经 PageProxy
  （`Data="{x:Bind}"` 放在 `Page.Resources`）用传统 Binding 访问。
- 【必须】**模板内 `x:Bind` 禁止配合 `StaticResource` Converter**（`LookupConverter` 运行时 NRE，
  编译期 0 警告、延迟数秒~数十秒才崩）→ 条件显隐一律改用 **VisualState 状态机**。
- **VisualState 的 Setter 优先级高于本地绑定值**：基础值走绑定、hover 用 Setter 覆盖，
  可纯 XAML 表达条件显示。
- **`{TemplateBinding}` 是一次性求值**，运行期会变的属性须在依赖属性回调里写入；
  把 DataTemplate 重构为 ControlTemplate 时绑定机制会静默改变，须逐个复核。
- **虚拟化容器回收复用不会重新应用模板**：`Unloaded` 里改过的状态要在 `Loaded` 对称恢复；
  「上一次 X」这类字段须在 `DataContextChanged` 清除。
- WinUI 3 XAML 无 `EventTrigger` / `BeginStoryboard` → 状态驱动动画选「模板化控件 + VisualState」；
  `RepeatBehavior="Forever"` 的动画在容器回收后不自停，须在 `Unloaded` 回静态态。

**菜单与工具栏**

- 【必须】**`MenuFlyout` 从 `Application.Current.Resources` 取出的是共享单例**，重复 `ShowAt` 抛
  `E_INVALIDARG` → 可重复弹出的菜单必须工厂方法每次 `new`。改「数据定义 + 工厂方法 + 每次 Opening 重建」
  可消掉一整类勾选同步代码。
- **`CommandBar` 动态溢出有未修 bug（issue #6450，官方 not planned）**：触发溢出后 Flyout 永久异常
  → 带 Flyout 的工具栏溢出只能手动实现（`AdaptiveTrigger` + VisualState，「更多」菜单每次 Opening 重建）。
- **`Page.KeyboardAccelerators` 会污染页面内所有 ToolTip**（官方 by design，`PlacementMode="Hidden"`
  实测无效）→ 快捷键只能用代码后置 `KeyDown`。菜单项的 `KeyboardAcceleratorTextOverride` 是豁免用法。
- **切换 ListViewBase 的 `SelectionMode` 会重置选择**：保留选择的切换须先抓快照、归零后再恢复。

**外观与资源**

- 【必须】**改控件外观优先覆盖主题资源，而非重写模板**。给 `MenuFlyoutItem` 自定义模板会触发旋转忙碌光标；
  主题键覆盖**不要放进 `Style.Resources`**。圆角两档：4（控件）/ 8（表面）。
- **`Border.CornerRadius` 会裁剪子内容（含投影）** → 圆角图片交给 `Border.Background` 的 `ImageBrush`
  （无 `Image.CornerRadius`，`RectangleGeometry` 也无 `RadiusX/Y`）。
- **unpackaged 应用的 PRI 不索引 `<Content>` 项**，`ms-appx://` 解析不到 → 资源一律按
  `AppContext.BaseDirectory` 磁盘路径加载；默认 Content glob 不含 `.jpg`，须显式声明。
- **`ThemeShadow` + `Translation`**：z 是投影唯一输入，z=0 几乎不可见；`Translation` 在合成层、不参与布局。
- **unpackaged 应用要 Win11 圆角只能靠 `MicaBackdrop`**（WASDK 2.3.6 无 `TransparentBackdrop`）；
  材质可被不透明背景覆盖而不影响圆角；Mica 仅 Win11 生效。

**窗口视觉分层（背景图 + 玻璃卡片）**

自下而上：全窗口 `Image` 背景（`Grid.RowSpan` 覆盖标题栏行 + 内容行，`UniformToFill`）
→ 透明标题栏与 NavigationView → 内容区半透明卡片（`Border` + `ThemeShadow` + `Translation` 抬高 z）。

- 透出背景：`NavigationView{Default,Expanded,Top}PaneBackground` 与 `NavigationViewContentBackground`
  全改 `Transparent`，NavigationView 自身 `Background` 也要透明。**Pane 展开态走
  `NavigationViewExpandedPaneBackground`**，改 `Default` 无效；死键：
  `NavigationViewPaneBackground` / `ContentBackground` / `Background`。
- 容器级圆角必须归零（`NavigationViewContentGridCornerRadius`）否则裁掉卡片投影；卡片圆角自己声明。
  卡片贴边 = `Margin="24,24,0,0"` + `CornerRadius="12,0,0,0"` + `BorderThickness="1,1,0,0"`。
- **Pane 与内容区之间的竖线来自 `ContentGrid` 的 `BorderThickness`（默认 `1,1,0,0`）的 Left=1**，
  不是 Pane 自身的 Border；覆盖归零即可，勿动 `NavigationViewItemSeparatorForeground`。
  左栏圆角来自模板的 `RightCornerRadiusFilterConverter`，元素级覆盖 `OverlayCornerRadius` 无效。
- **内容区紧贴左栏时，「内容区左上圆角」与「左栏右上深色圆弧」是同一几何事实**，无法同时消除，
  要兼得只能留缝。
- NavigationView 纵向结构：Row0 `ContentTopPadding` → Row1 `HeaderContent`（MinHeight=36，
  可 `AlwaysShowHeader="False"` 消掉）→ Row2 `ContentPresenter`。

**骨架屏与淡入**

- 「**内容可绘制**」与「**数据已就绪**」是两个时刻：可靠信号是 **Image 控件级的 `ImageOpened`**；
  探针就绪后纹理可能晚一帧，淡入再等 `CompositionTarget.Rendering` 一帧。
- 取消 **≠** 失败：加载被取消必须回落 Loading 态，不能显示为失败。
- 动画异常排查顺序：① 就绪信号级别 → ② 状态被重置 → ③ 跃迁判据（正向枚举）→ ④ 二次换源 →
  ⑤ 帧间隔 → ⑥ 形状/位置 → ⑦ 播放时机 → ⑧ 时长/缓动。**先问「什么时候播」，再问「怎么播」**。
- 小元素动画 250ms 偏短（本项目定 500ms）。

**异步管线**

- 按线程亲和性切开：中间产物用 `byte[]`，CPU 段限流放线程池，只在最后一跳回 UI 线程构造 `BitmapImage`；
  **批量写回 UI 线程**。
- 【必须】**绝不能 `EnqueueAsync(async () => await Xxx())` 而 `Xxx` 内部又 `EnqueueAsync`**（自我死锁）；
  批量加载循环必须 `await Task.WhenAll`。UI 状态赋值统一放进 `EnqueueAsync` 块。
- 【必须】**信号量只控并发数，不控「该不该做」**；可见性判定用 `ContainerFromItem(item) is null`；
  滚走取消 + 滚回重触发。删除「整页提交」兜底是高危操作。

**符号字体码点（离屏渲染实证）**

空心文件夹 `\uED25`；`\uE8B7` 在 Fluent 是实心 FolderFill、MDL2 是文件+书签；线性星 `\uE734` /
实心星 `\uE735`；空心爱心 `\uEB51` / 实心 `\uEB52`（`Symbol.Favorite` 是爱心非星形）。
查码点用 PowerShell + WPF `RenderTargetBitmap` 离屏渲染 PNG 目检（白底）。
**Microsoft Learn 的 WinRT 页与中文图标表都可能写错**，须以包内二进制逐项 diff。

---

## 13. UI 验收清单（PR 自查）

- [ ] 颜色 / 字号 / 字体 100% 经主题资源，XAML 无硬编码色值
- [ ] Light / Dark / High Contrast 三主题渲染正确
- [ ] 100% / 150% / 200% DPI 与 225% 文本缩放下不破版、缩略图清晰
- [ ] 控件五态齐全（含 Disabled），焦点可见
- [ ] 键盘完整可达；图标按钮有 `AutomationProperties.Name`
- [ ] 可交互元素命中区 ≥ 44×44 epx
- [ ] 动效有目的且时长 ≤ 350ms（骨架屏 / 淡入等小元素放宽至 500ms）
- [ ] 无弃用 API（§12.8 清单）；构建 0 警告

**布局与渲染（§12.1）**

- [ ] 位图尺寸**未**参与任何驱动布局的属性，仅作兜底且优先级最后
- [ ] 覆盖层未与内容网格同处一个布局容器；撤销覆盖层前已 `await` 一个渲染帧
- [ ] 模板内 `x:Bind` 未配合 `StaticResource` Converter；条件显隐走 VisualState
- [ ] 容器回收路径对称：`Unloaded` 改过的状态在 `Loaded` 恢复，`RepeatBehavior="Forever"` 动画已停
- [ ] 可重复弹出的 `MenuFlyout` 每次 `new`，未从 `Application.Current.Resources` 取单例
- [ ] 页面快捷键走代码后置 `KeyDown`，未用 `Page.KeyboardAccelerators`
- [ ] 请求缩略图尺寸是先乘 `RasterizationScale` 再量化，且只设最长边一维
- [ ] 异步管线无「`EnqueueAsync` 包裹 async 且内部再 `EnqueueAsync`」的自我死锁

---

## 附录 A：主题资源实测速查表

以下值实测自本机 NuGet 包内 `Microsoft.WinUI/Themes/generic.xaml`（与 WASDK 2.4.0 基线的 Fluent 2 主题值一致）：

| 资源键 | 值 |
|--------|-----|
| `ControlCornerRadius` | 4 |
| `OverlayCornerRadius` | 8 |
| `GridViewItemCornerRadius` / `ListViewItemCornerRadius` | 4 |
| `ListViewItemCheckBoxCornerRadius` / `GridViewItemCheckBoxCornerRadius` | 3 |
| `ListViewItemSelectionIndicatorCornerRadius` | 1.5 |
| `CaptionTextBlockFontSize` | 12 |
| `BodyTextBlockFontSize` / `BodyStrongTextBlockFontSize` | 14 |
| `SubtitleTextBlockFontSize` | 20 |
| `TitleTextBlockFontSize` | 28 |
| `TitleLargeTextBlockFontSize` | 40 |
| `DisplayTextBlockFontSize` | 68 |
| `ControlContentThemeFontSize` | 14 |
| `ToolTipContentThemeFontSize` | 12 |
| `TextControlThemeMinHeight` / `MinWidth` | 32 / 64 |
| `ComboBoxThemeMinWidth` / `ComboBoxPopupThemeMinWidth` | 64 / 80 |
| `FlyoutThemeMinWidth` / `FlyoutThemeMinHeight` | 96 / 40 |
| `FlyoutContentPadding` | 16,15,16,17 |
| `AppBarThemeMinHeight` | 56 |
| `GridViewItemMinHeight` | 44 |
| `NavigationViewCompactPaneLength` | 48 |
| `ProgressBarThemeMinHeight` | 4 |
| `ControlFasterAnimationDuration` | 83ms |
| `ControlFastAnimationDuration` | 167ms |
| `ControlNormalAnimationDuration` | 250ms |
| `SplitViewPaneAnimationOpenDuration` / `CloseDuration` | 200ms / 100ms |
| `ContentControlThemeFontFamily` | XamlAutoFontFamily |
| `SymbolThemeFontFamily` | Segoe Fluent Icons,Segoe MDL2 Assets |

复测方法（版本升级后核对）：

```powershell
$p = "$env:USERPROFILE\.nuget\packages\microsoft.windowsappsdk\<版本>\lib\net6.0-windows10.0.18362.0\Microsoft.WinUI\Themes\generic.xaml"
Select-String -Path $p -Pattern 'x:Key="(Control|Overlay)CornerRadius"|TextBlockFontSize|AnimationDuration' | ForEach-Object { $_.Line.Trim() }
```

## 附录 B：官方文档索引

| 主题 | 链接（learn.microsoft.com/zh-cn/windows/apps） |
|------|------------------------------------------------|
| 设计总览（Fluent Design） | `/design/` |
| 排版 | `/design/style/typography` |
| 颜色 | `/design/style/color` |
| 圆角 | `/design/style/corner-radius` |
| 间距 | `/design/style/spacing` |
| 图标 / Segoe Fluent Icons | `/design/style/iconography/overview`、`/design/style/segoe-fluent-icons-font` |
| 动效 | `/design/motion/` |
| 控件总览 | `/design/controls/` |
| XAML 主题资源 | `/design/style/xaml-theme-resources` |
| 无障碍 | `/design/accessibility/` |
| NavigationView | `/design/controls/navigationview` |
| 命令栏 | `/design/controls/command-bar` |
