# App.xaml 资源与样式说明

对应文件：`src/Pixbian/App.xaml`。应用级 XAML 资源的唯一集中处，按五个分区组织：

| 分区 | 内容 | 是否随主题 |
|------|------|-----------|
| ① 合并字典 | WinUI 控件资源（必需）+ 自定义控件默认样式 | — |
| ② 主题字典 | `Pixbian*` 自定义语义键，浅色 `Default` / 深色 `Dark` 各一套；含取值随主题的系统键覆盖 | 是 |
| ③ 共享样式 | 跨页面复用的自定义样式 | — |
| ④ 常量语义键 | 不随主题反转的固定配色 | 否 |
| ⑤ 系统键覆盖区 | 全应用唯一允许覆盖「常量型系统键」的位置 | 否 |

## 关键约束（改动前必读）

1. **顶层字典内严禁 `StaticResource` 重定向系统状态键**。资源别名只在加载（启动主题）解析一次，切换主题后仍钉死旧主题值——曾两次因此出现「深色下悬停背景不可见」。取值随主题的系统键必须放进 `ThemeDictionaries`。
2. **与默认同值的键一律不覆盖**。覆盖前先查包内 `generic.xaml`：
   `microsoft.windowsappsdk.winui/<版本>/lib/net6.0-windows10.0.17763.0/Microsoft.WinUI/Themes/generic.xaml`。
3. **颜色必须写 8 位 `#AARRGGBB`**，只写 6 位没有 alpha 通道（等价完全不透明）。
4. **只在某页使用的键不下沉到 App**：页面内挂主题字典的写法是元素级 `X.Resources` 里显式包一层 `<ResourceDictionary>` 再放 `<ResourceDictionary.ThemeDictionaries>`（直接写属性元素报 WMC9997）。图库页的复选框配色即按此放在页面根 Grid。

## ① 合并字典

| 来源 | 说明 |
|------|------|
| `controls:XamlControlsResources` | WinUI 3 控件样式与主题资源，必需项。**不可设 `ControlsResourcesVersion`**（1.7 才有，在 1.6 上触发 WMC0011） |
| `Controls/ThumbnailPresenter.xaml` | 自定义控件的默认样式（隐式 Style，全应用生效） |

## ② 主题字典

业务引用一律用 `ThemeResource`。

| 键 | 类型 | 浅色（Default） | 深色（Dark） | 用途 |
|----|------|-----------------|--------------|------|
| `PixbianItemHover` | SolidColorBrush | `#000000` | `#FFFFFF` | 列表项悬停蒙版 |
| `PixbianItemPressed` | SolidColorBrush | `#000000` | `#FFFFFF` | 列表项按下蒙版 |
| `PixbianSkeletonBrush` | SolidColorBrush | `#F0F0F0` | `#333333` | 缩略图骨架屏占位底色 |
| `PixbianItemOverlayBrush` | SolidColorBrush | `#80000000` | 同浅色 | 时长角标底色（压在图片上，不随主题反转） |
| `PixbianItemOverlayForegroundBrush` | SolidColorBrush | `#FFFFFF` | 同浅色 | 时长角标文字（角标底恒为半透明黑，故字恒白） |
| `ContextMenuInfoForeground` | SolidColorBrush | `#6B6B6B` | `#9A9A9A` | 右键菜单只读信息项前景 |
| `PixbianContentCardBackground` | SolidColorBrush | `#99FFFFFF` | `#66282828` | 内容区卡片底色 |
| `PixbianContentCardBorderBrush` | SolidColorBrush | `#59CCCCCC` | `#59191919` | 内容区卡片描边（两主题同 alpha 保视觉权重对称） |
| `PixbianWallpaperGradient` | LinearGradientBrush | `#EDF5F9` →(0.45)`#EEF4F9` →`#F0F3F9` | `#1B2225` →(0.45)`#1D2124` →`#1E2023` | 窗口背景线性光（实测参考图取色） |
| `PixbianWallpaperGlow` | SolidColorBrush | `#00FFFFFF` | `#00FFFFFF` | 背景光晕层，恒全透明（键必须保留，模板引用它，缺键会崩） |
| `ButtonBackgroundPointerOver` | SolidColorBrush（系统键） | `#14000000`（8% 黑） | `#1AFFFFFF`（10% 白） | 按钮悬停底色 |
| `ButtonBackgroundPressed` | SolidColorBrush（系统键） | `#1F000000`（12% 黑） | `#26FFFFFF`（15% 白） | 按钮按下底色 |

按钮两键说明：默认取 Control 系填充，铺在内容卡片上几乎不可见，故加深一档；**按下比悬停更深，与系统默认（按下略淡）有意相反**——本应用按钮无其它按下反馈，靠加深区分两态。全应用默认 `Button` 一并生效。

## ③ 共享样式

| 样式 | 目标类型 | 关键取值 | 应用位置 |
|------|----------|----------|----------|
| `ReadOnlyMenuItemStyle` | `MenuFlyoutItem` | `MaxWidth=320` | 图库页右键菜单只读项（代码后置按 key 取用，见 `GalleryPage.xaml.cs`） |
| `PageHeaderIconStyle` | `FontIcon` | `FontSize=28`、`SymbolThemeFontFamily`、垂直居中 | 图库页、设置页的页头 |
| `BodyMediumTextBlockStyle` | `TextBlock` | `FontSize=16` | 图库页底部删除通知条（Fluent 在 Body 14 与 BodyLarge 18 之间缺档） |
| `PixbianIconButtonBaseStyle` | `ButtonBase` | `Background=Transparent`、`BorderThickness=0`、`CornerRadius=4` | 只作基类被继承，不直接使用 |
| `ViewerToolButtonStyle` | `ButtonBase` | `40×36`、`Padding=0`、`Foreground=#FFFFFF`、内容居中 | 图片查看器页、幻灯片放映页工具栏（Button 与 ToggleButton 共用） |

`PixbianIconButtonBaseStyle` 的继承者：`ViewerToolButtonStyle`、图库页 `ToolBarButtonStyle`、图库页 `ItemFavoriteButtonStyle`、短片页 `ShortOverlayButtonStyle`。

约束：**常态背景必须写在 Style Setter，写成元素本地值会压掉 hover/pressed 反馈**（本地值优先级高于 VisualState）。

## ④ 常量语义键

| 键 | 值 | 说明 |
|----|----|------|
| `PixbianPlayerCardBackground` | `#FF000000` | 播放态卡片底色：纯黑不透明，与播放器页舞台一致，不随主题反转。由代码后置覆盖到内容卡片，退出播放时 `ClearValue` 回落 |
| `PixbianDeleteForeground` | `#FFFF99A4` | **删除类文本统一前景（#FF99A4 柔和红）**：所有「删除」菜单项文本与工具栏删除按钮图标都取此键，不随主题反转。使用方：图库页图片菜单、图库页选择工具栏删除按钮、主窗口文件夹菜单、短片页更多菜单 |

## ⑤ 系统键覆盖区

| 键 | 覆盖值 | 包内默认值 | 偏离理由 |
|----|--------|-----------|----------|
| `NavigationViewDefaultPaneBackground` | `Transparent` | `AcrylicInAppFillColorDefaultBrush` | 让背景图透出（并排形态下真正生效的是 Expanded 键，见下） |
| `NavigationViewContentBackground` | `Transparent` | `LayerFillColorDefaultBrush`（2455 行） | 内容区底色会挡住背景图 |
| `NavigationViewContentGridCornerRadius` | `0` | `8,0,0,0`（32472 行） | 圆角会对内容网格裁切，卡片阴影扩散到边界外会被切角 |
| `NavigationViewBorderThickness` | `0` | `1`（32447 行） | 左栏与内容区之间不要分割线 |
| `NavigationViewContentGridBorderThickness` | `0` | `1,1,0,0`（32443 行） | 其中「左 1」正是 Pane 与内容区之间那条竖线 |

已回归默认、不再覆盖的同值键：`NavigationViewExpandedPaneBackground`、`NavigationViewTopPaneBackground`（默认即透明）、`TextControlBorderBrush*`。

登记时避开：`NavigationViewPaneBackground` / `NavigationViewBackground` 是死键；不要把 `NavigationViewItemSeparatorForeground` 改成透明画刷（该笔刷还被 Top 模式的 SeparatorLine 复用，会误伤）。
