/**
 * 布尔到不透明度转换器（图库条目复选框等 hover/模式显隐用）。
 * 职责：把布尔绑定值转换为 1 / 0 的不透明度，供 x:Bind 一次性编译的绑定使用。
 * 复用约定：签名与 BoolToVisibilityConverter 同构（object 入参），满足 x:Bind 对转换器的
 *          强类型解析要求——BoolToValueConverter 的属性化形态在 x:Bind 下会让 XamlCompiler
 *          内部错误（WMC9999），Binding 下则正常，两者不可混用场景须选用本类。
 * 关键约束：非布尔值一律按未选中（0）处理，不抛异常；ConvertBack 不支持（单向绑定专用）。
 */

using Microsoft.UI.Xaml.Data;

namespace Pixbian.Converters;

/// <summary>布尔到不透明度转换器。</summary>
public sealed class BoolToOpacityConverter : IValueConverter
{
    /// <summary>开关为 true 时不透明度。</summary>
    public double TrueOpacity { get; set; } = 1d;

    /// <summary>开关为 false 时不透明度。</summary>
    public double FalseOpacity { get; set; }

    /// <inheritdoc />
    public object Convert(object value, Type targetType, object parameter, string language) =>
        value is true ? TrueOpacity : FalseOpacity;

    /// <inheritdoc />
    public object ConvertBack(object value, Type targetType, object parameter, string language) =>
        value is double opacity && opacity > FalseOpacity;
}
