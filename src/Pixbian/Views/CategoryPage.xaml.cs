/**
 * 分类规则管理页代码后置（M5）。
 * 职责：把界面操作转交 ViewModel，并在正则输入变化时即时触发校验提示。
 * 复用约定：视图模型由依赖注入提供；目标下拉框用索引与枚举互转，避免 XAML 直接绑定枚举。
 * 关键约束：正则校验必须在用户输入时就地反馈，不得等到点保存才报错——
 *          用户往往不知道自己的正则有误，即时提示能显著降低无效保存；
 *          规则的启用开关使用 ToggleSwitch 的 Toggled 事件驱动，
 *          若改用双向绑定会在加载列表时触发批量写入，造成误改数据。
 */

using System.ComponentModel;
using System.Runtime.CompilerServices;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Pixbian.Core.Models;
using Pixbian.ViewModels;

namespace Pixbian.Views;

/// <summary>分类规则管理页。</summary>
public sealed partial class CategoryPage : Page, INotifyPropertyChanged
{
    private int _newRuleTargetIndex;

    /// <summary>初始化分类页。</summary>
    /// <param name="viewModel">分类视图模型，由依赖注入提供。</param>
    public CategoryPage(CategoryViewModel viewModel)
    {
        ArgumentNullException.ThrowIfNull(viewModel);

        ViewModel = viewModel;
        InitializeComponent();

        Loaded += OnLoaded;
    }

    /// <inheritdoc />
    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>分类视图模型。</summary>
    public CategoryViewModel ViewModel { get; }

    /// <summary>匹配目标下拉框的当前索引。</summary>
    public int NewRuleTargetIndex
    {
        get => _newRuleTargetIndex;
        set
        {
            if (_newRuleTargetIndex == value)
            {
                return;
            }

            _newRuleTargetIndex = value;
            OnPropertyChanged();
        }
    }

    private async void OnLoaded(object sender, RoutedEventArgs e) =>
        await ViewModel.LoadAsync();

    private async void OnAddCategoryClick(object sender, RoutedEventArgs e)
    {
        await ViewModel.AddCategoryAsync(CategoryNameBox.Text);
        CategoryNameBox.Text = string.Empty;
    }

    /// <summary>正则输入变化时即时校验，让用户立刻看到提示。</summary>
    private void OnPatternChanged(object sender, TextChangedEventArgs e) =>
        ViewModel.NotifyValidationChanged();

    private void OnCategoryChanged(object sender, SelectionChangedEventArgs e) =>
        ViewModel.NotifyValidationChanged();

    private void OnTargetChanged(object sender, SelectionChangedEventArgs e) =>
        ViewModel.NewRuleTarget = (RuleMatchTarget)NewRuleTargetIndex;

    private async void OnAddRuleClick(object sender, RoutedEventArgs e) =>
        await ViewModel.AddRuleAsync();

    private async void OnApplyRulesClick(object sender, RoutedEventArgs e) =>
        await ViewModel.ApplyRulesAsync();

    /// <summary>控件加载时按规则当前状态初始化开关。</summary>
    private void OnRuleToggleLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is ToggleSwitch { DataContext: CategoryRule rule } toggle)
        {
            toggle.IsOn = rule.IsEnabled;
        }
    }

    /// <summary>切换规则启用状态；用 DataContext 取回规则，避免依赖选中项。</summary>
    private async void OnRuleToggled(object sender, RoutedEventArgs e)
    {
        if (sender is not ToggleSwitch { DataContext: CategoryRule rule } toggle)
        {
            return;
        }

        // 状态一致说明是列表重建引起的事件回声，此时不应回写，避免误改数据。
        if (toggle.IsOn == rule.IsEnabled)
        {
            return;
        }

        await ViewModel.ToggleRuleAsync(rule);
    }

    private async void OnDeleteRuleClick(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: CategoryRule rule })
        {
            return;
        }

        var dialog = new ContentDialog
        {
            Title = "删除规则",
            Content = $"确定删除规则「{rule.Name}」？\n\n删除后需要重新匹配才会更新已归类的文件。",
            PrimaryButtonText = "删除",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = XamlRoot
        };

        if (await dialog.ShowAsync() != ContentDialogResult.Primary)
        {
            return;
        }

        await ViewModel.DeleteRuleAsync(rule);
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
