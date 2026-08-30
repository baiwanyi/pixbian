/**
 * 尚未实现的导航页占位（M2）。
 * 职责：为视频、分类、发现等尚未交付的导航项提供统一的占位界面，避免导航到空白区域。
 * 复用约定：各里程碑完成后以真实页面替换对应导航项，本页随之移除；
 *          界面一律使用 ThemeResource，深浅主题切换时自动生效。
 * 关键约束：Title 与 Description 会在导航时就地改写，故必须实现属性通知，
 *          否则界面不会刷新（XAML 编译器会给出 WMC1506 警告）；
 *          占位页不得承载任何业务逻辑，必须明确标注计划交付的里程碑，避免用户误解。
 */

using System.ComponentModel;
using System.Runtime.CompilerServices;
using Microsoft.UI.Xaml.Controls;

namespace Pixbian.Views;

/// <summary>尚未实现的导航页占位。</summary>
public sealed partial class PlaceholderPage : Page, INotifyPropertyChanged
{
    private string _title = "即将推出";
    private string _description = "该功能正在开发中。";

    /// <summary>初始化占位页。</summary>
    public PlaceholderPage()
    {
        InitializeComponent();
        DataContext = this;
    }

    /// <inheritdoc />
    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>展示的标题。</summary>
    public string Title
    {
        get => _title;
        set
        {
            if (_title == value)
            {
                return;
            }

            _title = value;
            OnPropertyChanged();
        }
    }

    /// <summary>展示的说明。</summary>
    public string Description
    {
        get => _description;
        set
        {
            if (_description == value)
            {
                return;
            }

            _description = value;
            OnPropertyChanged();
        }
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
