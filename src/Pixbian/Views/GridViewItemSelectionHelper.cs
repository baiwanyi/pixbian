/**
 * GridViewItem 选择模式附加属性。
 * 职责：把页面级的「是否处于勾选选择模式」下沉到每个条目容器，供 ItemContainerStyle 的
 *       ControlTemplate 内 x:Bind 访问（WinUI 3 的 ItemContainerStyle 模板根固定为容器类型，
 *       无法经 x:Bind 直接触达页面属性，RelativeSource AncestorType 亦不被 XamlCompiler 支持）。
 * 复用约定：页面在 IsSelectionMode 变更时遍历已生成容器设值，并在 ContainerContentChanging
 *           对新生成容器补设；命中即设，无需监听属性回调。
 * 关键约束：附加属性读写须走 GetValue/SetValue，禁止缓存局部字段绕过依赖属性系统。
 */

using Microsoft.UI.Xaml;

namespace Pixbian.Views;

/// <summary>GridViewItem 选择模式附加属性承载器。</summary>
internal static class GridViewItemSelectionHelper
{
    public static readonly DependencyProperty IsInSelectionModeProperty =
        DependencyProperty.RegisterAttached(
            "IsInSelectionMode",
            typeof(bool),
            typeof(GridViewItemSelectionHelper),
            new PropertyMetadata(false));

    public static bool GetIsInSelectionMode(DependencyObject obj) =>
        (bool)obj.GetValue(IsInSelectionModeProperty);

    public static void SetIsInSelectionMode(DependencyObject obj, bool value) =>
        obj.SetValue(IsInSelectionModeProperty, value);
}
