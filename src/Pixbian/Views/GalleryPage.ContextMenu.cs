/**
 * 图库页代码后置——右键菜单与文件操作（partial）。
 * 职责：条目右键菜单的构建/弹出/信息填充/事件挂解，打开、复制、重命名、删除、
 *      资源管理器定位等菜单动作与配套对话框（重命名、错误提示）。
 * 复用约定：操作目标统一经 _contextItem（右键命中项）或 GetContextTarget 传递，不依赖
 *          可能过期的 SelectedItem；删除与重命名全部委托 ViewModel。
 * 关键约束：MenuFlyout 每次弹出新建实例——共享单例再次 ShowAt 会因旧 XamlRoot 冲突抛
 *          E_INVALIDARG；Closed 里统一解绑 Click 防止重复订阅；explorer /select 的路径
 *          先去除结尾分隔符再引号包裹（反斜杠会转义收尾引号），argv 形式杜绝命令注入。
 */

using System.Diagnostics;
using System.Globalization;
using System.IO;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Pixbian.ViewModels;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;

namespace Pixbian.Views;

/// <summary>图库页的右键菜单与文件操作。</summary>
public sealed partial class GalleryPage
{
    // 右键菜单命中的目标项；菜单内各操作据此执行，避免依赖可能过期的 SelectedItem。
    private MediaItemViewModel? _contextItem;

    /// <summary>在图片上右键：定位命中项，弹出上下文菜单并填充只读信息项。</summary>
    /// <remarks>
    /// 右键命中项作为本次菜单的操作目标，统一经 _contextItem 传递，避免依赖可能过期的 SelectedItem；
    /// 菜单由 CreateItemContextMenu 在代码中构建（资源字典不带 x:Class，无法用 x:Name 绑定事件），
    /// Click 在构建时逐个挂接，故此处只需填充信息项。
    /// </remarks>
    private void OnItemRightTapped(object sender, RightTappedRoutedEventArgs e)
    {
        var (item, _) = ResolveHit(e.OriginalSource as DependencyObject);

        if (item is null)
        {
            return;
        }

        // 右键命中项即本次操作目标；同步为当前项，使工具栏状态与菜单一致。
        ViewModel.SelectedItem = item;
        _contextItem = item;

        // 每次弹出都构建全新的 MenuFlyout 实例：Application.Current.Resources 取出的是共享单例，
        // 首次 ShowAt 后其 FlyoutPresenter 残留在旧视觉树/XamlRoot 上，再次 ShowAt 会因新旧
        // XamlRoot 冲突而抛 E_INVALIDARG("参数错误")。每次新建实例可彻底规避该异常。
        var flyout = CreateItemContextMenu(ActualTheme);

        flyout.XamlRoot ??= this.XamlRoot;

        if (flyout.Items.FirstOrDefault(i => i is MenuFlyoutItem { Name: "MenuOpen" }) is MenuFlyoutItem open)
        {
            open.Click += OnOpenClick;
        }

        if (flyout.Items.FirstOrDefault(i => i is MenuFlyoutItem { Name: "MenuCopy" }) is MenuFlyoutItem copy)
        {
            copy.Click += OnCopyClick;
        }

        if (flyout.Items.FirstOrDefault(i => i is MenuFlyoutItem { Name: "MenuCopyPath" }) is MenuFlyoutItem copyPath)
        {
            copyPath.Click += OnCopyPathClick;
        }

        if (flyout.Items.FirstOrDefault(i => i is MenuFlyoutItem { Name: "MenuRename" }) is MenuFlyoutItem rename)
        {
            rename.Click += OnRenameClick;
        }

        if (flyout.Items.FirstOrDefault(i => i is MenuFlyoutItem { Name: "MenuReveal" }) is MenuFlyoutItem reveal)
        {
            reveal.Click += OnRevealClick;
        }

        if (flyout.Items.FirstOrDefault(i => i is MenuFlyoutItem { Name: "MenuDelete" }) is MenuFlyoutItem delete)
        {
            delete.Click += OnDeleteClick;
        }

        FillMenuInfo(flyout, item);

        // 订阅 Opened / Closed：在 Closed 里统一解绑 Click 与这两个事件，
        // 否则下次弹出会重复订阅，一次点击触发多次。
        flyout.Opened += OnContextMenuOpened;
        flyout.Closed += OnContextMenuClosed;

        // 跟随鼠标位置弹出：以页面根为锚点，偏移取右键指针相对页面根的坐标，
        // 菜单即出现在光标处，而非钉在图片条目容器边缘（固定位置）。
        var pointerPos = e.GetPosition(this);
        flyout.ShowAt(this, pointerPos);
    }

    /// <summary>构建图片右键上下文菜单的全新实例。</summary>
    /// <remarks>
    /// 每次调用返回独立 MenuFlyout，菜单项按 Name 暴露以便按 x:Name 绑定 Click 事件；
    /// 结构集中于此，新增/调整菜单项只需改此方法（对应原 ItemContextMenu.xaml）。
    /// 只读信息项（大小/尺寸/日期）用 IsEnabled="False" 呈现，前景色走主题键、不加图标，
    /// 并套用应用级资源 ReadOnlyMenuItemStyle（仅约束 MaxWidth）。
    /// 删除项前景色固定 IndianRed，并通过项级主题键覆盖 PointerOver/Pressed 视觉状态保持红色。
    /// 可点击项带 SymbolIcon 图标；重命名绑定 F2、在资源管理器中打开绑定 F3（亦见 OnGalleryPageKeyDown）。
    /// </remarks>
    private static MenuFlyout CreateItemContextMenu(ElementTheme theme)
    {
        // 只读信息项前景色统一引用主题键，明暗主题自动切换（对应原 XAML 的 ContextMenuInfoForeground）。
        var infoBrush = Application.Current.Resources["ContextMenuInfoForeground"] as Brush
            ?? new SolidColorBrush(Microsoft.UI.Colors.Gray);

        // 只读项样式经由 XAML 编译期应用级资源提供（见 App.xaml 的 ReadOnlyMenuItemStyle），
        // 不在此用 XamlReader.Load 动态解析，规避运行时应用该类模板造成的旋转忙碌光标。
        var readOnlyStyle = Application.Current.Resources["ReadOnlyMenuItemStyle"] as Style;

        var flyout = new MenuFlyout
        {
            Items =
            {
                new MenuFlyoutItem { Name = "MenuOpen", Text = "打开", Icon = new SymbolIcon(Symbol.View) },
                new MenuFlyoutItem { Name = "MenuCopy", Text = "复制", Icon = new SymbolIcon(Symbol.Copy), KeyboardAcceleratorTextOverride = "Ctrl+C" },
                new MenuFlyoutItem { Name = "MenuCopyPath", Text = "复制为路径", Icon = new SymbolIcon(Symbol.Link) },
                new MenuFlyoutItem { Name = "MenuRename", Text = "重命名", Icon = new SymbolIcon(Symbol.Rename), KeyboardAcceleratorTextOverride = "F2" },
                new MenuFlyoutItem { Name = "MenuReveal", Text = "在文件资源管理器中打开", Icon = new SymbolIcon(Symbol.OpenLocal), KeyboardAcceleratorTextOverride = "F3" },
                new MenuFlyoutSeparator(),
                new MenuFlyoutItem { Name = "MenuSize", Text = "大小：", IsEnabled = false, Foreground = infoBrush, Style = readOnlyStyle },
                new MenuFlyoutItem { Name = "MenuDimensions", Text = "尺寸：", IsEnabled = false, Foreground = infoBrush, Style = readOnlyStyle },
                new MenuFlyoutItem { Name = "MenuDate", Text = "日期：", IsEnabled = false, Foreground = infoBrush, Style = readOnlyStyle },
                new MenuFlyoutSeparator(),
            },
        };

        // 删除项：前景取统一的删除色（浅色 #C42B1C / 深色 #FF99A4，随主题从 ThemeDictionaries 取键），
        // 且 hover/pressed 视觉状态（默认模板会把 TextBlock.Foreground 改回主题键
        // MenuFlyoutItemForegroundPointerOver/Pressed）通过项级主题键覆盖保持红色，
        // 不重写 ControlTemplate（避免触发旋转忙碌光标）。
        var deleteBrush = (SolidColorBrush)((ResourceDictionary)Application.Current.Resources.ThemeDictionaries[
            theme == ElementTheme.Dark ? "Dark" : "Default"])["PixbianDeleteForeground"];
        var deleteItem = new MenuFlyoutItem
        {
            Name = "MenuDelete",
            Text = "删除",
            Icon = new SymbolIcon(Symbol.Delete),
            KeyboardAcceleratorTextOverride = "Delete",
            Foreground = deleteBrush,
        };
        deleteItem.Resources["MenuFlyoutItemForegroundPointerOver"] = deleteBrush;
        deleteItem.Resources["MenuFlyoutItemForegroundPressed"] = deleteBrush;
        flyout.Items.Add(deleteItem);

        return flyout;
    }

    /// <summary>弹出前填充只读信息项（大小 / 尺寸 / 日期）。</summary>
    private static void FillMenuInfo(MenuFlyout flyout, MediaItemViewModel item)
    {
        if (flyout.Items.FirstOrDefault(i => i is MenuFlyoutItem { Name: "MenuSize" }) is MenuFlyoutItem size)
        {
            size.Text = $"大小：{item.FileSizeText}";
        }

        if (flyout.Items.FirstOrDefault(i => i is MenuFlyoutItem { Name: "MenuDimensions" }) is MenuFlyoutItem dims)
        {
            dims.Text = $"尺寸：{item.DimensionText}";
        }

        if (flyout.Items.FirstOrDefault(i => i is MenuFlyoutItem { Name: "MenuDate" }) is MenuFlyoutItem date)
        {
            var taken = item.Item.TakenUtc ?? item.Item.ModifiedUtc;
            var text = item.Item.TakenUtc is null
                ? "未知"
                : taken.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.CurrentCulture);
            date.Text = $"日期：{text}";
        }
    }

    private void OnContextMenuOpened(object? sender, object e)
    {
        // 占位：菜单已通过 FillMenuInfo 预填，无需重复处理。
    }

    private void OnContextMenuClosed(object? sender, object e)
    {
        if (sender is not MenuFlyout flyout)
        {
            return;
        }

        flyout.Opened -= OnContextMenuOpened;
        flyout.Closed -= OnContextMenuClosed;

        foreach (var child in flyout.Items)
        {
            if (child is MenuFlyoutItem item)
            {
                item.Click -= OnOpenClick;
                item.Click -= OnCopyClick;
                item.Click -= OnCopyPathClick;
                item.Click -= OnRenameClick;
                item.Click -= OnRevealClick;
                item.Click -= OnDeleteClick;
            }
        }
    }

    /// <summary>菜单「打开」：复用查看器打开命中项。</summary>
    private async void OnOpenClick(object sender, RoutedEventArgs e)
    {
        if (_contextItem is { } item)
        {
            await Owner.OpenViewerAsync(item);
        }
    }

    /// <summary>菜单「复制」：把文件作为 StorageItem 写入剪贴板。</summary>
    private void OnCopyClick(object sender, RoutedEventArgs e)
    {
        _ = CopyItemToClipboardAsync(GetContextTarget());
    }

    /// <summary>菜单「复制为路径」：把文件完整路径写入剪贴板文本。</summary>
    private void OnCopyPathClick(object sender, RoutedEventArgs e)
    {
        if (_contextItem is { } item)
        {
            var package = new DataPackage { RequestedOperation = DataPackageOperation.Copy };
            package.SetText(item.Item.Path);
            Clipboard.SetContent(package);
        }
    }

    /// <summary>菜单「重命名」：弹出输入框，确认后委托 ViewModel 改文件并同步索引。</summary>
    private async void OnRenameClick(object sender, RoutedEventArgs e)
    {
        if (_contextItem is { } item)
        {
            await ShowRenameDialogAsync(item);
        }
    }

    /// <summary>菜单「删除」：把命中项移入回收站。</summary>
    private void OnDeleteClick(object sender, RoutedEventArgs e)
    {
        _ = DeleteContextItemsAsync(GetContextTarget());
    }

    /// <summary>菜单「在文件资源管理器中打开」：选中命中文件并定位到其所在文件夹。</summary>
    private void OnRevealClick(object sender, RoutedEventArgs e)
    {
        if (_contextItem is { } item)
        {
            OpenInFileExplorer(item.Item.Path);
        }
    }

    /// <summary>用资源管理器打开并选中指定文件（explorer.exe /select 形式）。</summary>
    /// <remarks>路径来自受信任的索引数据，经 argv 形式传入，杜绝命令注入；外层用引号包裹路径，
    /// 应对含空格的路径。unpackaged 下 explorer.exe 由系统 PATH 解析，无需硬编码绝对路径。</remarks>
    private static void OpenInFileExplorer(string filePath)
    {
        // 结尾分隔符必须去掉：`/select,"D:\Lib\"` 中 `\` 会转义收尾引号，explorer 解析失败。
        var target = filePath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{target}\"")
        {
            UseShellExecute = true,
        });
    }

    /// <summary>把目标集合首个文件复制到剪贴板（StorageItem 方式，支持跨应用粘贴）。</summary>
    private async Task CopyItemToClipboardAsync(IReadOnlyList<MediaItemViewModel> targets)
    {
        var first = targets.Count > 0 ? targets[0] : null;

        if (first is null)
        {
            return;
        }

        try
        {
            var file = await StorageFile.GetFileFromPathAsync(first.Item.Path);
            var package = new DataPackage { RequestedOperation = DataPackageOperation.Copy };
            package.SetStorageItems(new[] { file });
            Clipboard.SetContent(package);
        }
        catch (Exception ex)
        {
            // 目标文件可能已被外部删除 / 移动（页面 Selection 短暂过期等）：fire-and-forget
            // 的异常是黑洞，静默吞掉用户只会看到「按了没反应」，必须反馈。
            await ShowErrorAsync("复制失败", ex.Message);
        }
    }

    /// <summary>弹出重命名对话框，确认后调用 ViewModel 重命名并回写路径。</summary>
    private async Task ShowRenameDialogAsync(MediaItemViewModel item)
    {
        var input = new TextBox
        {
            Text = item.FileName,
        };

        // WinUI 3 的 TextBox 无 SelectAllOnFocus 属性，改为获得焦点时全选，便于用户直接覆盖输入。
        input.Loaded += (_, _) => input.SelectAll();

        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = "重命名",
            PrimaryButtonText = "确定",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Primary,
            Content = input,
        };

        ContentDialogResult result;
        try
        {
            result = await dialog.ShowAsync();
        }
        catch (OperationCanceledException)
        {
            // 对话框被外部关闭（如应用退出）时 ShowAsync 会取消，属预期行为，不应视为崩溃。
            return;
        }

        if (result is not ContentDialogResult.Primary)
        {
            return;
        }

        var newName = input.Text.Trim();

        if (string.IsNullOrWhiteSpace(newName) || newName == item.FileName)
        {
            return;
        }

        var error = await ViewModel.RenameAsync(item, newName);

        if (!string.IsNullOrEmpty(error))
        {
            await ShowErrorAsync("重命名失败", error);
        }
    }

    /// <summary>把目标集合移入回收站，并同步从索引与视图集合中移除；进度与结果经通知条呈现。</summary>
    /// <remarks>失败不再弹错误对话框：删除通知条统一呈现结果（成功 / 取消 / 部分失败）。</remarks>
    private async Task DeleteContextItemsAsync(IReadOnlyList<MediaItemViewModel> targets)
    {
        if (targets.Count == 0)
        {
            return;
        }

        await ViewModel.DeleteFilesAsync(targets);
    }

    /// <summary>弹出错误提示对话框。</summary>
    private async Task ShowErrorAsync(string title, string message)
    {
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = title,
            CloseButtonText = "确定",
            DefaultButton = ContentDialogButton.Close,
            Content = message,
        };

        try
        {
            await dialog.ShowAsync();
        }
        catch (OperationCanceledException)
        {
            // 对话框被外部关闭（如应用退出）时 ShowAsync 会取消，属预期行为，不应视为崩溃。
        }
    }
}
