/**
 * 布尔开关到任意属性值的通用转换器：按开关在 TrueValue / FalseValue 间取值。
 * 职责：支撑「元素常驻视觉树、以渲染属性切换显隐」的绑定写法（Opacity 0/1、IsHitTestVisible 等），
 *      替代 Collapsed/Visible 切换，避免大规模条目在模式切换时全量重测量排列造成卡顿。
 * 复用约定：页面资源注册实例并配置 TrueValue/FalseValue，经传统 Binding 单向引用；
 *          目标类型支持 double 与 bool，其余类型原样返回配置值。
 * 关键约束：XAML attribute 配置的值恒为字符串，Convert 内按绑定目标类型显式转换，
 *          防止绑定引擎静默转换失败导致属性不生效。
 */

using System.Globalization;
using Microsoft.UI.Xaml.Data;

namespace Pixbian.Converters;

/// <summary>布尔开关到任意属性值的转换器；TrueValue / FalseValue 由 XAML 侧配置。</summary>
public sealed class BoolToValueConverter : IValueConverter
{
    /// <summary>开关为 true 时返回的值。</summary>
    public string TrueValue { get; set; } = "True";

    /// <summary>开关为 false 时返回的值。</summary>
    public string FalseValue { get; set; } = "False";

    /// <inheritdoc />
    public object Convert(object value, Type targetType, object parameter, string language)
    {
        // 非布尔值按「非空」判定，与 BoolToVisibilityConverter 的口径保持一致。
        var flag = value is bool boolValue ? boolValue : value is not null;
        var raw = flag ? TrueValue : FalseValue;

        return targetType == typeof(double)
            ? double.Parse(raw, CultureInfo.InvariantCulture)
            : targetType == typeof(bool) ? raw == "True" : raw;
    }

    /// <inheritdoc />
    public object ConvertBack(object value, Type targetType, object parameter, string language) => value is true;
}
