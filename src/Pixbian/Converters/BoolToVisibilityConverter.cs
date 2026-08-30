/**
 * 布尔到可见性的 XAML 转换器（M2）。
 * 职责：把 ViewModel 的布尔状态转换为 Visibility，并提供取反版本，避免 ViewModel 引用 UI 类型。
 * 复用约定：在 App.xaml 中以静态资源注册，全应用共用；x:Bind 与 Binding 均可引用。
 * 关键约束：ViewModel 不得直接返回 Visibility 等 UI 类型，否则领域与界面层会耦合，
 *          且无法在单元测试中验证；一切 UI 相关的形态转换都应收敛到转换器。
 */

using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Data;

namespace Pixbian.Converters;

/// <summary>布尔到可见性的转换器；可反转结果。</summary>
public sealed class BoolToVisibilityConverter : IValueConverter
{
    /// <summary>为 true 时是否反转结果（true 对应 Collapsed）。</summary>
    public bool IsInverted { get; set; }

    /// <inheritdoc />
    public object Convert(object value, Type targetType, object parameter, string language)
    {
        var flag = value is bool boolValue && boolValue;

        if (IsInverted)
        {
            flag = !flag;
        }

        return flag ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <inheritdoc />
    public object ConvertBack(object value, Type targetType, object parameter, string language) =>
        value is Visibility visibility
            ? visibility == Visibility.Visible ^ IsInverted
            : false;
}
