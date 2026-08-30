/**
 * XAML 绑定代理（供容器/条目模板跨 namescope 访问页面属性）。
 * 职责：把页面实例作为数据源注入页面资源，使 ControlTemplate、DataTemplate 内的传统 {Binding}
 *       能读取页面级属性——这些模板的绑定根固定为模板化控件或条目，x:Bind 无法触达页面。
 * 复用约定：在页面资源中以 x:Key 声明，Data 用无路径 x:Bind 绑定到页面自身；
 *           替代 WinUI 3 不支持的 (local:Type.Property) 附加属性路径写法。
 * 关键约束：Data 必须是依赖属性，承载类型须为 public 以便 XAML 类型解析；
 *           页面须实现 INotifyPropertyChanged，否则属性变化不会刷新到模板内的绑定。
 */

using Microsoft.UI.Xaml;

namespace Pixbian.Views;

/// <summary>把页面实例暴露为绑定源的代理对象。</summary>
public sealed class BindingProxy : DependencyObject
{
    /// <summary>标识 Data 依赖属性：被代理的数据源（通常为页面自身）。</summary>
    public static readonly DependencyProperty DataProperty = DependencyProperty.Register(
        nameof(Data),
        typeof(object),
        typeof(BindingProxy),
        new PropertyMetadata(null));

    /// <summary>被代理的数据源。</summary>
    public object? Data
    {
        get => GetValue(DataProperty);
        set => SetValue(DataProperty, value);
    }
}
