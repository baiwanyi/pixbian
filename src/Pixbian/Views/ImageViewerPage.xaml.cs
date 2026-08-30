/**
 * 图片查看器页代码后置（M3）。
 * 职责：设置数据上下文、处理幻灯片开关与键盘快捷键，并在离开页面时停止播放。
 * 复用约定：视图模型由依赖注入提供；页面本身不持有图片数据，全部通过绑定获取。
 * 关键约束：幻灯片切换按钮是 AppBarToggleButton，其 Checked/Unchecked 事件与 ViewModel 命令
 *          只能选其一驱动状态，否则会出现状态回环；此处统一由事件调用命令，控件只做单向展示。
 */

using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Pixbian.ViewModels;
using Windows.System;

namespace Pixbian.Views;

/// <summary>图片查看器页。</summary>
public sealed partial class ImageViewerPage : Page
{
    /// <summary>初始化图片查看器页。</summary>
    /// <param name="viewModel">查看器视图模型，由依赖注入提供。</param>
    public ImageViewerPage(ImageViewerViewModel viewModel)
    {
        ArgumentNullException.ThrowIfNull(viewModel);

        ViewModel = viewModel;
        DataContext = viewModel;

        InitializeComponent();

        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
        KeyDown += OnKeyDown;
    }

    /// <summary>查看器视图模型。</summary>
    public ImageViewerViewModel ViewModel { get; }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        // 获得焦点后键盘快捷键（左右翻页、空格播放）才会命中本页面。
        Focus(FocusState.Programmatic);
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        // 离开页面必须停止定时器，否则后台会持续触发切换。
        ViewModel.StopSlideShow();
    }

    private void OnSlideShowChecked(object sender, RoutedEventArgs e) => ViewModel.StartSlideShow();

    private void OnSlideShowUnchecked(object sender, RoutedEventArgs e) => ViewModel.StopSlideShow();

    private void OnRotateRightClick(object sender, RoutedEventArgs e) => ViewModel.RotateRight();

    private void OnCloseClick(object sender, RoutedEventArgs e) => App.Services
        .GetRequiredService<MainWindow>()
        .CloseViewer();

    private async void OnKeyDown(object sender, KeyRoutedEventArgs e)
    {
        switch (e.Key)
        {
            case VirtualKey.Left:
                await ViewModel.GoPreviousCommand.ExecuteAsync(null);
                e.Handled = true;
                break;

            case VirtualKey.Right:
            case VirtualKey.Space:
                await ViewModel.GoNextCommand.ExecuteAsync(null);
                e.Handled = true;
                break;

            case VirtualKey.Escape:
                ViewModel.StopSlideShow();
                e.Handled = true;
                break;
        }
    }
}
