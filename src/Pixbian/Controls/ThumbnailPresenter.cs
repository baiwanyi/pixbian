/**
 * 缩略图展示控件（图库条目）。
 * 职责：按加载状态在骨架屏、缩略图、错误占位之间切换，并在缩略图真正可绘制时切到图片终态。
 * 复用约定：默认模板与视觉状态定义在同目录的 ThumbnailPresenter.xaml，由 App.xaml 合并为全局
 *          隐式样式；状态由 MediaItemViewModel 写入，本控件只读、不自行发起任何加载。
 * 关键约束：
 *   1. 模板中 ImageLayer（Border+ImageBrush）与探针 Image 的 Source **不可用 {TemplateBinding}**
 *      ——它是一次性求值，模板应用时缩略图尚未到位（Source 为 null），此后 Source 变更不会同步。
 *      缩略图是异步到达的，必须由代码在 Source 变更回调里写入（ApplyImageSource）。
 *   2. **切到图片终值的时机必须以探针 Image 的 ImageOpened 为门闩**：ImageBrush 本身没有
 *      「内容可绘制」信号，BitmapImage.PixelWidth/ImageOpened 又只表示 CPU 解码完成
 *      （且服务层返回的位图早已解码、位图级事件已错过），纹理上传到 GPU 在其后异步进行——
 *      提前切终值会让图片在就绪瞬间突现 = 闪。探针与 ImageBrush 共享同一 BitmapImage
 *      （纹理只解码一次），Image 控件的 ImageOpened 是控件级事件，无论位图是否已缓存
 *      都会在可渲染时触发——信号源必须取控件级而非位图级。
 *      探针 ImageOpened 后还需再等一渲染帧：共享同一 BitmapImage 的 ImageBrush
 *      纹理上传可能比探针晚一帧。
 *   3. 虚拟化容器回收**不会重新应用模板**：Unloaded 停靠 Inactive 并复位挂起标记，
 *      滚回时由 Loaded 事件按 State 恢复终值；容器被复用到别的条目时，须在
 *      DataContextChanged 里清除跃迁记录与挂起标记，否则新条目会被误判为「骨架 → 图片」。
 *   4. 跃迁判据是「上一次状态为 Loading」：写在 VisualState.Storyboard 里无法区分跃迁与
 *      状态未变的重复应用（升级加载完成会重播），用 VisualStateGroup.Transitions 则会被
 *      先应用的 Setters 抢先一帧。终值一律由 Setters 保证。
 *   5. 占位层承载「失败」与「缺失」两种语义（后者为文件不存在）：二者共用 Failed 视觉状态，
 *      差异只在文本。FailedTitle / FailedDetail 同样**不可用 {TemplateBinding}**，
 *      理由与第 1 条相同；字号与最大行数按容器短边调整以适配 128 / 256 / 512 三档格子。
 */

using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace Pixbian.Controls;

/// <summary>按加载状态展示骨架屏、缩略图或错误占位的控件。</summary>
public sealed class ThumbnailPresenter : Control
{
    /// <summary>模板中承载缩略图的 Image 名称，须与 ThumbnailPresenter.xaml 中的 x:Name 一致。</summary>
    private const string ImageLayerName = "ImageLayer";

    /// <summary>模板中用于停靠容器回收的视觉状态名，须与 ThumbnailPresenter.xaml 中的 x:Name 一致。</summary>
    private const string InactiveStateName = "Inactive";

    /// <summary>模板中骨架层的元素名，须与 ThumbnailPresenter.xaml 中的 x:Name 一致。</summary>
    private const string SkeletonLayerName = "SkeletonLayer";

    /// <summary>模板中透明探针的元素名，须与 ThumbnailPresenter.xaml 中的 x:Name 一致。</summary>
    private const string ImageProbeName = "ImageProbe";

    /// <summary>模板中占位层图标、标题与详情的元素名，须与 ThumbnailPresenter.xaml 中的 x:Name 一致。</summary>
    private const string FailedIconName = "FailedIcon";
    private const string FailedTitleTextName = "FailedTitleText";
    private const string FailedDetailTextName = "FailedDetailText";

    /// <summary>宽高比的合法区间，与 MediaItemViewModel.FromDimensions 的钳制范围保持一致。</summary>
    private const double MinAspectRatio = 0.25;

    private const double MaxAspectRatio = 4.0;

    /// <summary>标识 Source 依赖属性：已加载的缩略图。</summary>
    public static readonly DependencyProperty SourceProperty = DependencyProperty.Register(
        nameof(Source),
        typeof(ImageSource),
        typeof(ThumbnailPresenter),
        new PropertyMetadata(null, OnSourceChanged));

    /// <summary>标识 State 依赖属性：当前加载状态，驱动视觉状态切换。</summary>
    public static readonly DependencyProperty StateProperty = DependencyProperty.Register(
        nameof(State),
        typeof(ThumbnailLoadState),
        typeof(ThumbnailPresenter),
        new PropertyMetadata(ThumbnailLoadState.Loading, OnStateChanged));

    /// <summary>标识 FailedGlyph 依赖属性：加载失败时显示的字形。</summary>
    public static readonly DependencyProperty FailedGlyphProperty = DependencyProperty.Register(
        nameof(FailedGlyph),
        typeof(string),
        typeof(ThumbnailPresenter),
        new PropertyMetadata("\uE91B"));

    /// <summary>标识 AspectRatio 依赖属性：图片宽高比，决定骨架层的形状。</summary>
    public static readonly DependencyProperty AspectRatioProperty = DependencyProperty.Register(
        nameof(AspectRatio),
        typeof(double),
        typeof(ThumbnailPresenter),
        new PropertyMetadata(1.0, OnSkeletonBoundsPropertyChanged));

    /// <summary>标识 Stretch 依赖属性：图片在容器内的缩放方式。</summary>
    public static readonly DependencyProperty StretchProperty = DependencyProperty.Register(
        nameof(Stretch),
        typeof(Stretch),
        typeof(ThumbnailPresenter),
        new PropertyMetadata(Stretch.Uniform, OnStretchChanged));

    /// <summary>标识 SkeletonFillsContainer 依赖属性：骨架层是否填满容器。</summary>
    public static readonly DependencyProperty SkeletonFillsContainerProperty = DependencyProperty.Register(
        nameof(SkeletonFillsContainer),
        typeof(bool),
        typeof(ThumbnailPresenter),
        new PropertyMetadata(false, OnSkeletonBoundsPropertyChanged));

    /// <summary>标识 FailedTitle 依赖属性：占位层标题（缺失态即文件名）。</summary>
    public static readonly DependencyProperty FailedTitleProperty = DependencyProperty.Register(
        nameof(FailedTitle),
        typeof(string),
        typeof(ThumbnailPresenter),
        new PropertyMetadata(string.Empty, OnFailedTextChanged));

    /// <summary>标识 FailedDetail 依赖属性：占位层详情（缺失态即完整路径）。</summary>
    public static readonly DependencyProperty FailedDetailProperty = DependencyProperty.Register(
        nameof(FailedDetail),
        typeof(string),
        typeof(ThumbnailPresenter),
        new PropertyMetadata(string.Empty, OnFailedTextChanged));

    private Border? _imageLayer;
    private Image? _imageProbe;
    private Border? _skeletonLayer;
    private FontIcon? _failedIcon;
    private TextBlock? _failedTitleText;
    private TextBlock? _failedDetailText;

    /// <summary>上次已应用的状态，用于区分「状态跃迁」与「状态未变的重复应用」。</summary>
    private ThumbnailLoadState? _appliedState;

    /// <summary>骨架 → 图片跃迁已发生、正在等待 ImageOpened 的挂起标记。</summary>
    private bool _pendingFadeIn;

    /// <summary>当前 Source 是否已触发 ImageOpened（内容可绘制）。每次换源后重置。</summary>
    private bool _imageOpened;

    /// <summary>初始化缩略图展示控件。</summary>
    public ThumbnailPresenter()
    {
        DefaultStyleKey = typeof(ThumbnailPresenter);
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
        SizeChanged += OnSizeChanged;
        DataContextChanged += OnDataContextChanged;
    }

    /// <summary>已加载的缩略图；为空时按 <see cref="State"/> 显示骨架屏或错误占位。</summary>
    public ImageSource? Source
    {
        get => (ImageSource?)GetValue(SourceProperty);
        set => SetValue(SourceProperty, value);
    }

    /// <summary>当前加载状态。</summary>
    public ThumbnailLoadState State
    {
        get => (ThumbnailLoadState)GetValue(StateProperty);
        set => SetValue(StateProperty, value);
    }

    /// <summary>加载失败时显示的字形；默认与图库空态一致的图片图标。</summary>
    public string FailedGlyph
    {
        get => (string)GetValue(FailedGlyphProperty);
        set => SetValue(FailedGlyphProperty, value);
    }

    /// <summary>图片宽高比（宽 / 高）；用于把骨架层裁成与图片相同的形状。</summary>
    public double AspectRatio
    {
        get => (double)GetValue(AspectRatioProperty);
        set => SetValue(AspectRatioProperty, value);
    }

    /// <summary>图片在容器内的缩放方式；默认 Uniform（等比缩放适配容器），
    /// 方形网格模式传 None——按图片当前尺寸原样显示、宽高双向居中，超出格子部分被裁剪。</summary>
    public Stretch Stretch
    {
        get => (Stretch)GetValue(StretchProperty);
        set => SetValue(StretchProperty, value);
    }

    /// <summary>骨架层是否填满容器；默认 false（按 AspectRatio 等比贴合）。
    /// 方形网格模式传 true——格子本身就是 1:1 布局单元，骨架应与格子同形，
    /// 若仍按图片宽高比裁剪，非正方形图片的骨架会缩成一条，与方形网格观感不符。</summary>
    public bool SkeletonFillsContainer
    {
        get => (bool)GetValue(SkeletonFillsContainerProperty);
        set => SetValue(SkeletonFillsContainerProperty, value);
    }

    /// <summary>占位层标题；缺失态传文件名，失败态可传提示语。</summary>
    public string FailedTitle
    {
        get => (string)GetValue(FailedTitleProperty);
        set => SetValue(FailedTitleProperty, value);
    }

    /// <summary>占位层详情；缺失态传完整路径。</summary>
    public string FailedDetail
    {
        get => (string)GetValue(FailedDetailProperty);
        set => SetValue(FailedDetailProperty, value);
    }

    /// <inheritdoc />
    protected override void OnApplyTemplate()
    {
        base.OnApplyTemplate();

        // 绑定在模板应用前就已写入 State 与 Source，此处补一次同步，否则首帧会停在 Loading 且不显示图片。
        _imageLayer = GetTemplateChild(ImageLayerName) as Border;
        _imageProbe = GetTemplateChild(ImageProbeName) as Image;
        _skeletonLayer = GetTemplateChild(SkeletonLayerName) as Border;
        _failedIcon = GetTemplateChild(FailedIconName) as FontIcon;
        _failedTitleText = GetTemplateChild(FailedTitleTextName) as TextBlock;
        _failedDetailText = GetTemplateChild(FailedDetailTextName) as TextBlock;

        if (_imageProbe is not null)
        {
            // 探针仅用于拿 ImageOpened 信号，其加载流程与 Opacity 无关。
            _imageProbe.ImageOpened += OnImageOpened;
            _imageProbe.ImageFailed += OnImageFailed;
        }

        ApplyStretch();
        ApplyImageSource();
        UpdateSkeletonBounds();
        ApplyFailedText();
        ApplyVisualState();
    }

    private static void OnSourceChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is ThumbnailPresenter presenter)
        {
            presenter.ApplyImageSource();
        }
    }

    /// <summary>把缩略图同步写入显示层（ImageBrush）与信号探针（Image）。</summary>
    /// <remarks>
    /// 模板不能用 TemplateBinding 绑定该属性，原因见文件头。
    /// 两个渲染路径共享同一 BitmapImage，纹理只解码一次；探针的 ImageOpened
    /// 是「内容可绘制」的可靠信号，见 OnImageOpened。
    /// </remarks>
    private void ApplyImageSource()
    {
        // 换源后旧图的 ImageOpened 状态作废，等待新图触发 ImageOpened。
        _imageOpened = false;

        if (_imageLayer?.Background is ImageBrush brush)
        {
            brush.ImageSource = Source;
        }

        if (_imageProbe is not null)
        {
            _imageProbe.Source = Source;
        }
    }

    private void OnImageOpened(object sender, RoutedEventArgs e)
    {
        // 探针 Image 的控件级「内容可绘制」信号：纹理已就绪，此时才可切换终值。
        // 无论位图是否已缓存，Image 控件每次设置 Source 后都会触发本事件。
        _imageOpened = true;

        // 若骨架→图片跃迁已挂起，此刻补齐切换。
        if (_pendingFadeIn)
        {
            _pendingFadeIn = false;
            ShowLoadedState();
        }
    }

    private void OnImageFailed(object sender, ExceptionRoutedEventArgs e)
    {
        // 解码失败由 VM 侧置 State=Failed（Source 会置 null）驱动错误占位。
        // 此处仅清掉可能残留的挂起标记，避免等不到 ImageOpened 而永不切换。
        _pendingFadeIn = false;
    }

    private static void OnSkeletonBoundsPropertyChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is ThumbnailPresenter presenter)
        {
            presenter.UpdateSkeletonBounds();
        }
    }

    private static void OnStretchChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is ThumbnailPresenter presenter)
        {
            presenter.ApplyStretch();
        }
    }

    private static void OnFailedTextChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is ThumbnailPresenter presenter)
        {
            presenter.ApplyFailedText();
        }
    }

    /// <summary>把标题与详情写入模板元素，并按新内容重算字号与行数。</summary>
    /// <remarks>
    /// 不能用 {TemplateBinding}：模板应用与条目绑定求值的先后不确定，早于绑定求值时会停在空串，
    /// 此后文本变化也不会同步（Source 有同样的约束，故同样走代码写入）。
    /// </remarks>
    private void ApplyFailedText()
    {
        if (_failedTitleText is not null)
        {
            _failedTitleText.Text = FailedTitle;
        }

        if (_failedDetailText is not null)
        {
            _failedDetailText.Text = FailedDetail;
        }

        UpdateFailedDetailLayout();
    }

    /// <summary>按容器短边调整占位层的字号与文本行数：格子越小越紧凑。</summary>
    /// <remarks>
    /// 三档与缩略图档位（128 / 256 / 512）对齐：128 档方形格子的高度只够一行文本，
    /// 512 档等高行则能完整显示多行路径。MaxLines 配合 TextTrimming 让溢出以省略号收尾。
    /// </remarks>
    private void UpdateFailedDetailLayout()
    {
        if (_failedIcon is null || _failedTitleText is null || _failedDetailText is null)
        {
            return;
        }

        var shortest = Math.Min(ActualWidth, ActualHeight);

        var (iconSize, titleSize, detailSize, maxLines) = shortest switch
        {
            < 160 => (20.0, 11.0, 10.0, 1),
            < 300 => (28.0, 12.0, 11.0, 2),
            _ => (40.0, 13.0, 12.0, 4)
        };

        _failedIcon.FontSize = iconSize;
        _failedTitleText.FontSize = titleSize;
        _failedDetailText.FontSize = detailSize;
        _failedTitleText.MaxLines = maxLines;
        _failedDetailText.MaxLines = maxLines;
    }

    /// <summary>把缩放方式同步到图片层画刷。</summary>
    /// <remarks>
    /// 对齐方式必须显式设为双向居中：TileBrush 的 AlignmentX/AlignmentY 默认是 Left/Top，
    /// Stretch=None 时若不覆盖，图片会贴在格子左上角而非居中。
    /// Stretch=Uniform 时对齐属性本就不生效，同值设置无副作用。
    /// </remarks>
    private void ApplyStretch()
    {
        if (_imageLayer?.Background is ImageBrush brush)
        {
            brush.Stretch = Stretch;
            brush.AlignmentX = AlignmentX.Center;
            brush.AlignmentY = AlignmentY.Center;
        }
    }

    private static void OnStateChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is ThumbnailPresenter presenter)
        {
            presenter.ApplyVisualState();
        }
    }

    /// <summary>计算骨架层尺寸：默认按图片渲染区域贴合，方形网格模式填满容器。</summary>
    /// <remarks>
    /// 默认算法与 <c>Image</c> 的 <c>Stretch="Uniform"</c> 一致：取容器内接的最大等比矩形并居中。
    /// 形状一旦不一致，正方形格子里的 3:2 图片旁会露出骨架灰边，观感即为抖动。
    /// SkeletonFillsContainer 时直接填满容器（方形格子的 1:1 布局单元）。
    /// </remarks>
    private void UpdateSkeletonBounds()
    {
        if (_skeletonLayer is null)
        {
            return;
        }

        var availableWidth = ActualWidth;
        var availableHeight = ActualHeight;

        // 布局尚未测量时保持 Fill（拉伸填满），待 SizeChanged 到达后再修正。
        if (availableWidth <= 0 || availableHeight <= 0)
        {
            return;
        }

        if (SkeletonFillsContainer)
        {
            _skeletonLayer.Width = availableWidth;
            _skeletonLayer.Height = availableHeight;
            return;
        }

        var ratio = Math.Clamp(AspectRatio, MinAspectRatio, MaxAspectRatio);

        // 容器相对更宽时高度撑满，反之宽度撑满——此即 Uniform 的取值规则。
        var width = availableWidth / availableHeight > ratio
            ? availableHeight * ratio
            : availableWidth;

        _skeletonLayer.Width = width;
        _skeletonLayer.Height = width / ratio;
    }

    private void ApplyVisualState()
    {
        var name = State switch
        {
            ThumbnailLoadState.Loaded => nameof(ThumbnailLoadState.Loaded),
            ThumbnailLoadState.Failed => nameof(ThumbnailLoadState.Failed),
            _ => nameof(ThumbnailLoadState.Loading)
        };

        // 退出 Loaded 时清掉挂起标记：条目被刷新置空后不得再切回图片终值。
        if (State != ThumbnailLoadState.Loaded)
        {
            _pendingFadeIn = false;
        }

        // 终值总由状态自身的 Setters 应用——它是唯一权威，
        // 容器回收复用时靠它直接恢复，不依赖任何动画。
        _ = VisualStateManager.GoToState(this, name, false);

        // 只有「骨架 → 图片」这一次跃迁才切终值。判据必须是「上一次是 Loading」而非
        // 「上一次不是 Loaded」：后者会把「滚入一个已加载的条目」也算作跃迁，
        // 滚动时每张图都要重切一次，正是「图片显示后还闪」的直接来源。
        // 升级加载（Loaded→Loaded）同样不属此列，新位图直接替换，图片本就可见。
        var isSkeletonToImage = State == ThumbnailLoadState.Loaded
            && _appliedState == ThumbnailLoadState.Loading;

        // State 先于 Source 到达（实测顺序）：不可立即 GoToState(Loaded)——
        // Loaded 的 Setters 会把 ImageLayer 置为全亮而 Source 仍为空，图片层+骨架层
        // 双透明 = 格子闪成空白一帧。故保持骨架视觉并挂起，由 ImageOpened 补齐。
        if (isSkeletonToImage && !_imageOpened)
        {
            _pendingFadeIn = true;
            _appliedState = State;
            return;
        }

        // 内容已可绘制（ImageOpened 已触发）：完成切换。
        if (isSkeletonToImage)
        {
            ShowLoadedState();
        }

        _appliedState = State;
    }

    /// <summary>切换到图片终态（同步置值）；调用方须确保条目仍在视口内且内容已可绘制。</summary>
    private void ShowLoadedState()
    {
        _ = VisualStateManager.GoToState(this, nameof(ThumbnailLoadState.Loaded), false);
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        // 虚拟化容器回收复用时不会重新应用模板，必须在此恢复终值，
        // 否则条目会停留在 Unloaded 时停靠的 Inactive 状态。
        // State 通常未变，ApplyVisualState 只会重设终值，可直接调用。
        ApplyVisualState();
    }

    private void OnSizeChanged(object sender, SizeChangedEventArgs e)
    {
        // 容器尺寸变化后须重算，否则骨架仍保持上一布局的形状。
        UpdateSkeletonBounds();
    }

    private void OnDataContextChanged(FrameworkElement sender, DataContextChangedEventArgs args)
    {
        // 虚拟化容器被回收复用到另一个条目：_appliedState 记录的是**上一个**条目的状态，
        // 若不清除，当上一个条目处于 Loading、新条目已 Loaded 时，会被误判为
        // 「骨架 → 图片」而重切终值，滚动时每张图都闪一次。
        // 置空表示「跃迁历史未知」，此后 State 的首次变化只设终值。
        _appliedState = null;
        _pendingFadeIn = false;
        _imageOpened = false;
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        // 停靠到静态的 Inactive 状态并复位挂起标记：容器已滚出视口，挂起的切换不应再发生。
        // 不可停靠到 Loaded：那会让「已加载」与「已回收」两种语义混淆，滚回时无法恢复骨架屏。
        _pendingFadeIn = false;
        _imageOpened = false;
        _ = VisualStateManager.GoToState(this, InactiveStateName, false);

        // 刻意不重置 _appliedState：滚回同一条目时 State 未变，不应重切终值，
        // 否则每次滚回都再切一次，表现为滚动时图片反复闪烁。
        // 容器被复用到**别的**条目时，由 DataContextChanged 负责清除。
    }
}
