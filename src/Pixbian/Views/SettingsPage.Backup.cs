/**
 * 设置页代码后置——数据与备份（partial）。
 * 职责：用户数据的导出与导入交互——文件选择、导入前的说明与二次确认、结果反馈。
 * 复用约定：文件选择统一经 Microsoft.Windows.Storage.Pickers 的 FileSavePicker / FileOpenPicker
 *          （构造传 Owner.AppWindow.Id 完成归属）；导出与导入一律委托 BackupViewModel，
 *          页面只负责选择路径与呈现结果。
 * 关键约束：导入是「合并、导入文件为准」，会改写用户数据，必须在选定文件后弹确认对话框；
 *          对话框的 Content 每次新建——复用仍挂在视觉树上的元素会因双父级抛异常。
 */

using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.Windows.Storage.Pickers;
using Pixbian.Core.Abstractions;
using Pixbian.Core.Models;
using Pixbian.ViewModels;

namespace Pixbian.Views;

/// <summary>设置页的数据与备份分区。</summary>
public sealed partial class SettingsPage
{
    /// <summary>「导出用户数据」：选择保存位置后写出 JSON 数据包。</summary>
    private async void OnExportUserDataClick(object sender, RoutedEventArgs e)
    {
        var picker = new FileSavePicker(Owner.AppWindow.Id)
        {
            SuggestedStartLocation = PickerLocationId.DocumentsLibrary,
            SuggestedFileName = $"Pixbian-userdata-{DateTime.Now:yyyyMMdd-HHmmss}"
        };

        picker.FileTypeChoices.Add("Pixbian 用户数据", [".json"]);

        var file = await picker.PickSaveFileAsync();

        if (file is null)
        {
            return;
        }

        await Backup.ExportAsync(file.Path);
    }

    /// <summary>「导入用户数据」：选择备份文件，确认后合并导入，并弹出结果摘要。</summary>
    private async void OnImportUserDataClick(object sender, RoutedEventArgs e)
    {
        var picker = new FileOpenPicker(Owner.AppWindow.Id)
        {
            SuggestedStartLocation = PickerLocationId.DocumentsLibrary
        };

        picker.FileTypeFilter.Add(".json");

        var file = await picker.PickSingleFileAsync();

        if (file is null)
        {
            return;
        }

        var (confirmed, includeItemCategories, pathMappings) = await ConfirmImportAsync(file.Path);

        if (!confirmed)
        {
            return;
        }

        var result = await Backup.ImportAsync(file.Path, includeItemCategories, pathMappings);

        // 失败原因（格式不符、版本过高、文件过大等）由视图模型写入状态文本，此处原样呈现。
        await ShowBackupMessageAsync(
            result is null ? "导入失败" : "导入完成",
            Backup.ImportStatusText);
    }

    /// <summary>导入前的说明与二次确认；返回是否继续、是否应用分类归属与路径重映射。</summary>
    private async Task<(bool Confirmed, bool IncludeItemCategories, IReadOnlyList<PathPrefixMapping> PathMappings)>
        ConfirmImportAsync(string path)
    {
        var includeItemCategories = new CheckBox
        {
            Content = "同时应用条目的分类归属（之后执行「重新匹配」会覆盖它）",
            IsChecked = true
        };

        // 路径重映射：换盘或换根目录后，备份里的路径在本机不存在，靠前缀改写重新命中。
        // 两个输入框都留空表示不改写（默认路径不变的情形）。
        var fromBox = new TextBox
        {
            Header = "旧路径前缀（备份中的）",
            PlaceholderText = @"例如 D:\Downloads\Photos"
        };

        var toBox = new TextBox
        {
            Header = "新路径前缀（本机的）",
            PlaceholderText = @"例如 E:\Media\Photos"
        };

        var content = new StackPanel { Spacing = 12 };
        content.Children.Add(new TextBlock
        {
            Text = path,
            TextWrapping = TextWrapping.Wrap
        });
        content.Children.Add(new TextBlock
        {
            Text = "导入采用合并方式：收藏取并集、评分只升不降，分类与分组按名称合并；"
                + "不会删除本机已有的数据。备份中的文件若已被移动或删除，其收藏与分组归属无法写入。",
            TextWrapping = TextWrapping.Wrap
        });
        content.Children.Add(includeItemCategories);
        content.Children.Add(fromBox);
        content.Children.Add(toBox);

        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = "导入用户数据",
            PrimaryButtonText = "导入",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Close,
            Content = content
        };

        ContentDialogResult result;

        try
        {
            result = await dialog.ShowAsync();
        }
        catch (OperationCanceledException)
        {
            // 对话框被外部关闭（如应用退出）时 ShowAsync 会取消，属预期行为。
            return (false, false, []);
        }

        var mappings = BuildPathMappings(fromBox.Text, toBox.Text);

        return (result is ContentDialogResult.Primary, includeItemCategories.IsChecked == true, mappings);
    }

    /// <summary>由两个输入框构造路径映射；任一为空时不产生映射（表示不改写路径）。</summary>
    private static IReadOnlyList<PathPrefixMapping> BuildPathMappings(string from, string to)
    {
        var fromText = from.Trim();
        var toText = to.Trim();

        if (fromText.Length == 0 || toText.Length == 0)
        {
            return [];
        }

        return [new PathPrefixMapping(fromText, toText)];
    }

    /// <summary>弹出备份操作的结果提示。</summary>
    private async Task ShowBackupMessageAsync(string title, string message)
    {
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = title,
            CloseButtonText = "确定",
            DefaultButton = ContentDialogButton.Close,
            Content = new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap }
        };

        try
        {
            await dialog.ShowAsync();
        }
        catch (OperationCanceledException)
        {
            // 同上：对话框被外部关闭属预期行为。
        }
    }
}
