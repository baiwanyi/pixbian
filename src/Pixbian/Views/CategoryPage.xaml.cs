/**
 * 分类规则管理页代码后置。
 * 职责：把界面操作转交视图模型；构建新建/编辑共用的 ContentDialog；
 *      维护「分类管理 / 规则管理」两个区块的互斥展开（手风琴）。
 * 复用约定：视图模型由依赖注入提供；表单面板常驻页面（折叠），弹出时移交对话框，
 *          元素同属页面 namescope，x:Bind 在对话框内保持有效。
 * 关键约束：正则与分类名称必须在用户输入时就地反馈校验结果，不得等保存才报错；
 *          ToggleSwitch 程序化赋 IsOn 会触发 Toggled，一律以「状态一致即回声」忽略，
 *          避免列表加载时批量误写；对话框保存按钮随校验状态动态启停。
 */

using System.ComponentModel;
using System.Runtime.CompilerServices;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Pixbian.Core.Models;
using Pixbian.ViewModels;

namespace Pixbian.Views;

/// <summary>分类规则管理页。</summary>
public sealed partial class CategoryPage : Page, INotifyPropertyChanged
{
    private int _newRuleTargetIndex;

    /// <summary>两个区块正在互斥联动时为 true，抑制程序化收起引发的事件回声。</summary>
    private bool _isSyncingSectionExpansion;

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

    /// <summary>规则表单里匹配目标下拉框的当前索引。</summary>
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

    /// <summary>Expander 融入外层卡片：归零模板 Header 内边距，行内留白由 SettingsRowStyle 承担。</summary>
    private void OnSectionExpanderLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is not Expander expander)
        {
            return;
        }

        if (FindDescendantByName<ToggleButton>(expander, "ExpanderHeader") is { } header)
        {
            header.Padding = new Thickness(0);
        }
    }

    /// <summary>在视觉树中按名称深度优先查找指定类型的后代元素（模板内元素对 FindName 不可见）。</summary>
    private static T? FindDescendantByName<T>(DependencyObject root, string name)
        where T : DependencyObject
    {
        var count = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChildrenCount(root);

        for (var i = 0; i < count; i++)
        {
            var child = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChild(root, i);

            if (child is T typed && typed is FrameworkElement { Name: var elementName } && elementName == name)
            {
                return typed;
            }

            var descendant = FindDescendantByName<T>(child, name);

            if (descendant is not null)
            {
                return descendant;
            }
        }

        return null;
    }

    /// <summary>展开分类区块时收起规则区块，维持互斥手风琴。</summary>
    private void OnCategorySectionExpanding(Expander sender, ExpanderExpandingEventArgs args) =>
        CollapseSiblingSection(RulesSection);

    /// <summary>展开规则区块时收起分类区块，维持互斥手风琴。</summary>
    private void OnRuleSectionExpanding(Expander sender, ExpanderExpandingEventArgs args) =>
        CollapseSiblingSection(CategoriesSection);

    /// <summary>程序化收起同组的另一区块；标志抑制其事件回调再次连锁。</summary>
    private void CollapseSiblingSection(Expander sibling)
    {
        if (_isSyncingSectionExpansion || !sibling.IsExpanded)
        {
            return;
        }

        _isSyncingSectionExpansion = true;
        sibling.IsExpanded = false;
        _isSyncingSectionExpansion = false;
    }

    // —— 分类 ——

    private async void OnNewCategoryClick(object sender, RoutedEventArgs e)
    {
        ViewModel.BeginNewCategory();
        await ShowCategoryDialogAsync();
    }

    private async void OnEditCategoryClick(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: Category category })
        {
            return;
        }

        ViewModel.BeginEditCategory(category);
        await ShowCategoryDialogAsync();
    }

    /// <summary>弹出分类表单对话框；保存按钮随名称校验结果启停。</summary>
    private async Task ShowCategoryDialogAsync()
    {
        // 表单面板常驻页面视觉树，元素不能同时有两个父级：
        // 弹出前先从页面摘除并置为可见（XAML 里为 Collapsed，ContentDialog
        // 不会自动展开 Content），关闭后归还并复回折叠，否则内容区空白。
        RootGrid.Children.Remove(CategoryFormPanel);
        CategoryFormPanel.Visibility = Visibility.Visible;

        var dialog = new ContentDialog
        {
            Title = ViewModel.EditingCategoryId is null ? "新建分类" : "编辑分类",
            Content = CategoryFormPanel,
            PrimaryButtonText = "保存",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Primary,
            // 弹层主题不随页面 RequestedTheme 自动跟随，显式对齐，
            // 否则深浅混合下文字前景与底色错配（白底白字不可见）。
            RequestedTheme = ActualTheme,
            XamlRoot = XamlRoot
        };

        void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs args) =>
            dialog.IsPrimaryButtonEnabled = ViewModel.IsNewCategoryNameValid;

        ViewModel.PropertyChanged += OnViewModelPropertyChanged;
        dialog.IsPrimaryButtonEnabled = ViewModel.IsNewCategoryNameValid;

        ContentDialogResult result;

        try
        {
            result = await dialog.ShowAsync();
        }
        finally
        {
            ViewModel.PropertyChanged -= OnViewModelPropertyChanged;

            // 对话框关闭不会清空对 Content 的引用，面板可能仍挂在其 ContentPresenter 上
            //（直到 dialog 被 GC），不清空则下次 set_Content 概率性抛「已属于另一元素」。
            dialog.Content = null;

            RootGrid.Children.Add(CategoryFormPanel);
            CategoryFormPanel.Visibility = Visibility.Collapsed;
        }

        if (result == ContentDialogResult.Primary)
        {
            await ViewModel.SaveCategoryAsync();
        }
    }

    /// <summary>控件加载时按分类当前状态初始化开关。</summary>
    private void OnCategoryToggleLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is ToggleSwitch { DataContext: Category category } toggle)
        {
            toggle.IsOn = category.IsEnabled;
        }
    }

    /// <summary>切换分类启停；状态一致说明是列表重建引起的事件回声，不回写。</summary>
    private async void OnCategoryToggled(object sender, RoutedEventArgs e)
    {
        if (sender is not ToggleSwitch { DataContext: Category category } toggle)
        {
            return;
        }

        if (toggle.IsOn == category.IsEnabled)
        {
            return;
        }

        await ViewModel.ToggleCategoryAsync(category);
    }

    private async void OnDeleteCategoryClick(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: Category category })
        {
            return;
        }

        var dialog = new ContentDialog
        {
            Title = "删除分类",
            Content = $"确定删除分类「{category.Name}」？\n\n其下全部规则会被一并删除；"
                + "已归入该分类的文件将不再显示在任何分类中。",
            PrimaryButtonText = "删除",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = XamlRoot
        };

        if (await dialog.ShowAsync() != ContentDialogResult.Primary)
        {
            return;
        }

        await ViewModel.DeleteCategoryAsync(category);
    }

    // —— 规则 ——

    private async void OnNewRuleClick(object sender, RoutedEventArgs e)
    {
        ViewModel.BeginNewRule();
        await ShowRuleDialogAsync();
    }

    private async void OnEditRuleClick(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: CategoryRule rule })
        {
            return;
        }

        ViewModel.BeginEditRule(rule);
        await ShowRuleDialogAsync();
    }

    /// <summary>弹出规则表单对话框；保存按钮随正则校验与分类选择启停。</summary>
    private async Task ShowRuleDialogAsync()
    {
        // 同分类表单：弹出前摘除并置为可见，关闭后归还并复回折叠。
        RootGrid.Children.Remove(RuleFormPanel);
        RuleFormPanel.Visibility = Visibility.Visible;

        var dialog = new ContentDialog
        {
            Title = ViewModel.EditingRuleId is null ? "新建规则" : "编辑规则",
            Content = RuleFormPanel,
            PrimaryButtonText = "保存",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Primary,
            // 同分类对话框：显式对齐弹层主题。
            RequestedTheme = ActualTheme,
            XamlRoot = XamlRoot
        };

        void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs args) =>
            dialog.IsPrimaryButtonEnabled = ViewModel.IsNewRuleValid;

        ViewModel.PropertyChanged += OnViewModelPropertyChanged;
        dialog.IsPrimaryButtonEnabled = ViewModel.IsNewRuleValid;

        ContentDialogResult result;

        try
        {
            result = await dialog.ShowAsync();
        }
        finally
        {
            ViewModel.PropertyChanged -= OnViewModelPropertyChanged;

            // 同分类对话框：关闭后断开 Content 引用，避免面板被旧对话框长期持有。
            dialog.Content = null;

            RootGrid.Children.Add(RuleFormPanel);
            RuleFormPanel.Visibility = Visibility.Collapsed;
        }

        if (result == ContentDialogResult.Primary)
        {
            await ViewModel.SaveRuleAsync();
        }
    }

    /// <summary>控件加载时按规则当前状态初始化开关。</summary>
    private void OnRuleToggleLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is ToggleSwitch { DataContext: RuleDisplay display } toggle)
        {
            toggle.IsOn = display.Rule.IsEnabled;
        }
    }

    /// <summary>切换规则启停；状态一致说明是列表重建引起的事件回声，不回写。</summary>
    private async void OnRuleToggled(object sender, RoutedEventArgs e)
    {
        if (sender is not ToggleSwitch { DataContext: RuleDisplay display } toggle)
        {
            return;
        }

        if (toggle.IsOn == display.Rule.IsEnabled)
        {
            return;
        }

        await ViewModel.ToggleRuleAsync(display.Rule);
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

    /// <summary>执行批量重新匹配。</summary>
    private async void OnApplyRulesClick(object sender, RoutedEventArgs e) =>
        await ViewModel.ApplyRulesAsync();

    // —— 表单联动 ——

    /// <summary>正则输入变化时即时校验，让用户立刻看到提示。</summary>
    private void OnFormPatternChanged(object sender, TextChangedEventArgs e) =>
        ViewModel.NotifyValidationChanged();

    /// <summary>分类名称输入变化时即时校验（重名拦截）。</summary>
    private void OnFormCategoryNameChanged(object sender, TextChangedEventArgs e) =>
        ViewModel.NotifyValidationChanged();

    /// <summary>归入分类选择变化时手动回写；SelectedValue 可能因列表清空为 null，须按缺失归零。</summary>
    private void OnFormCategoryChanged(object sender, SelectionChangedEventArgs e)
    {
        if (sender is ComboBox { SelectedValue: long categoryId })
        {
            ViewModel.NewRuleCategoryId = categoryId;
        }

        ViewModel.NotifyValidationChanged();
    }

    private void OnFormTargetChanged(object sender, SelectionChangedEventArgs e) =>
        ViewModel.NewRuleTarget = (RuleMatchTarget)NewRuleTargetIndex;

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
