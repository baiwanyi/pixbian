/**
 * 自适应视图分组模板选择器。
 * 职责：为「组」返回头部 + 内嵌非分组 GridView 的模板，为其余类型降级返回叶子条目模板。
 * 复用约定：作为 XAML 资源被 GalleryPage 引用，须为独立文件以避免 XAML 编译期不可用
 *          （代码后置内的类型在 MarkupCompile 阶段尚未编译）；组内布局用非分组 GridView 承载自定义面板。
 * 关键约束：SelectTemplateCore 仅按 item 实际类型分流，不依赖容器或视觉树；模板实例由 XAML 注入。
 */

using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Pixbian.ViewModels;

namespace Pixbian.Views;

/// <summary>
/// 自适应视图分组模板选择器：为「组」返回头部 + 内嵌非分组 GridView 的模板，
/// 为其余类型降级返回叶子条目模板。组内布局须用非分组 GridView 承载自定义面板。
/// </summary>
internal sealed class MediaGroupTemplateSelector : DataTemplateSelector
{
    public DataTemplate? GroupTemplate { get; set; }

    public DataTemplate? ItemTemplate { get; set; }

    protected override DataTemplate? SelectTemplateCore(object item, DependencyObject container)
    {
        if (item is MediaGroupViewModel)
        {
            return GroupTemplate;
        }

        return ItemTemplate;
    }
}
