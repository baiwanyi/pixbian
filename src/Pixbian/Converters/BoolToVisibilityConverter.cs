/**
 * 布尔 / 对象存在性到可见性的 XAML 转换器（M2）。
 * 职责：把绑定值转换为 Visibility，并提供取反版本，避免 ViewModel 引用 UI 类型。
 * 复用约定：在页面资源中以静态资源注册，x:Bind 与 Binding 均可引用；
 *          布尔值按其自身判定，其余类型按「非空」判定，供 Thumbnail、Metadata 等对象直接驱动可见性。
 * 关键约束：ViewModel 不得直接返回 Visibility 等 UI 类型，否则领域与界面层会耦合，
 *          且无法在单元测试中验证；对象判空依赖属性变更通知，
 *          故异步赋值（如缩略图加载完成）的属性必须在变更时发出 PropertyChanged。
 */

using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Data;

namespace Pixbian.Converters;

/// <summary>布尔值或对象存在性到可见性的转换器；可反转结果。</summary>
public sealed class BoolToVisibilityConverter : IValueConverter
{
    /// <summary>为 true 时是否反转结果（true 对应 Collapsed）。</summary>
    public bool IsInverted { get; set; }

    /// <inheritdoc />
    public object Convert(object value, Type targetType, object parameter, string language)
    {
        // 非布尔值按「非空」判定：界面常把对象本身（缩略图、元数据）绑到 Visibility 上表达
        // 「存在即显示」，若只认 bool，这类绑定会恒为 Collapsed 导致内容永不显示。
        var flag = value is bool boolValue ? boolValue : value is not null;

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
