/**
 * 设置页代码后置——数据与备份（partial）。
 * 职责：用户数据的导出与导入交互——文件选择、导入前的说明与二次确认（含路径前缀重映射）、
 *      结果反馈；「同步到 OneDrive」行的开关、周期与立即同步（成功只更新该行副标题，
 *      失败才弹窗）；整库快照的备份与还原（还原为破坏性操作，须二次确认并在完成后提示重启应用）。
 * 复用约定：文件选择统一经 Microsoft.Windows.Storage.Pickers 的 FileSavePicker / FileOpenPicker
 *          （构造传 Owner.AppWindow.Id 完成归属）；导出、导入与同步一律委托 BackupViewModel，
 *          页面只负责选择路径与呈现结果；导出与快照的默认文件名取自 BackupFileNaming，
 *          与 OneDrive 同步写出的文件名共用同一套规则，避免两处前缀各写一份而漂移。
 * 关键约束：导入是「合并、导入文件为准」，会改写用户数据，必须在选定文件后弹确认对话框；
 *          对话框的 Content 每次新建——复用仍挂在视觉树上的元素会因双父级抛异常；
 *          周期下拉的索引与 BackupSyncFrequency 枚举数值一一对应，改枚举顺序即错位。
 */

using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.Windows.Storage.Pickers;
using Pixbian.Core.Abstractions;
using Pixbian.Core.Models;
using Pixbian.Core.Services;
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

            // 默认名与 OneDrive 同步、整库快照共用同一套规则，用户在两处看到的备份名一致。
            SuggestedFileName = BackupFileNaming.BuildUserDataBaseName(
                BackupFileNaming.FormatStamp(DateTimeOffset.Now))
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

    /// <summary>「备份完整数据库」：选择保存位置后导出整库快照（含索引，仅用于本机还原）。</summary>
    private async void OnBackupSnapshotClick(object sender, RoutedEventArgs e)
    {
        var picker = new FileSavePicker(Owner.AppWindow.Id)
        {
            SuggestedStartLocation = PickerLocationId.DocumentsLibrary,
            SuggestedFileName = BackupFileNaming.BuildIndexBaseName(
                BackupFileNaming.FormatStamp(DateTimeOffset.Now))
        };

        picker.FileTypeChoices.Add("Pixbian 索引库快照", [".db"]);

        var file = await picker.PickSaveFileAsync();

        if (file is null)
        {
            return;
        }

        if (await Backup.CreateSnapshotAsync(file.Path))
        {
            await ShowBackupMessageAsync("备份完成", Backup.SnapshotStatusText);
        }
        else
        {
            await ShowBackupMessageAsync("备份失败", Backup.SnapshotStatusText);
        }
    }

    /// <summary>「从快照还原」：选择快照文件，二次确认后用其替换当前索引库。</summary>
    private async void OnRestoreSnapshotClick(object sender, RoutedEventArgs e)
    {
        var picker = new FileOpenPicker(Owner.AppWindow.Id)
        {
            SuggestedStartLocation = PickerLocationId.DocumentsLibrary
        };

        picker.FileTypeFilter.Add(".db");

        var file = await picker.PickSingleFileAsync();

        if (file is null)
        {
            return;
        }

        if (!await ConfirmRestoreAsync(file.Path))
        {
            return;
        }

        var backupPath = await Backup.RestoreSnapshotAsync(file.Path);

        if (backupPath is null)
        {
            await ShowBackupMessageAsync("还原失败", Backup.SnapshotStatusText);
            return;
        }

        // 还原只换了磁盘上的库文件：进程内已加载的图库、分类与分组仍是旧库的内存副本，
        // 必须重启才能全部收敛，此处必须明说，否则用户会以为还原没生效。
        await ShowBackupMessageAsync(
            "还原完成",
            $"索引库已被替换为所选快照。\n\n旧库已备份为：\n{backupPath}\n\n"
                + "请关闭并重新打开应用，使界面加载还原后的数据。");
    }

    /// <summary>还原前的二次确认：这是破坏性操作，必须明确告知当前数据会被替换。</summary>
    private async Task<bool> ConfirmRestoreAsync(string path)
    {
        var content = new StackPanel { Spacing = 12 };
        content.Children.Add(new TextBlock
        {
            Text = path,
            TextWrapping = TextWrapping.Wrap
        });
        content.Children.Add(new TextBlock
        {
            Text = "将用该快照替换当前的索引库（含收藏、分组、分类与索引记录）。\n"
                + "当前库会另存为带时间戳的 .bak 副本，不会丢失；还原完成后需要重启应用。",
            TextWrapping = TextWrapping.Wrap
        });

        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = "从快照还原",
            PrimaryButtonText = "还原",
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
            return false;
        }

        return result is ContentDialogResult.Primary;
    }

    /// <summary>把同步开关、周期与状态文案同步为当前设置；进入设置页时调用。</summary>
    private void SyncBackupControls()
    {
        BackupSyncToggle.IsOn = Backup.IsSyncEnabled;
        BackupSyncFrequencySelector.SelectedIndex = Backup.SyncFrequencyIndex;
        Backup.RefreshSyncStatus();
    }

    /// <summary>同步开关：落盘后刷新状态文案（目标目录未探测到时会给出提示）。</summary>
    private async void OnBackupSyncToggled(object sender, RoutedEventArgs e)
    {
        await Backup.SetSyncEnabledAsync(BackupSyncToggle.IsOn);
    }

    /// <summary>同步周期下拉：索引与枚举数值一一对应，变更即落盘。</summary>
    private async void OnBackupSyncFrequencyChanged(object sender, SelectionChangedEventArgs e)
    {
        var index = BackupSyncFrequencySelector.SelectedIndex;

        if (index < 0)
        {
            return;
        }

        await Backup.SetSyncFrequencyAsync((BackupSyncFrequency)index);
    }

    /// <summary>「立即同步」：执行一次同步并给出结果（成功与否都更新状态文案）。</summary>
    private async void OnBackupSyncNowClick(object sender, RoutedEventArgs e)
    {
        var result = await Backup.SyncNowAsync();

        // 成功不弹对话框：结果（目标目录 + 本次同步时间）直接落在该行的副标题上。
        // 手动同步是低频、低风险操作，再弹一个「同步完成」只会多一次点击；
        // 失败必须弹窗——原因（未登录 OneDrive、目录被占用等）只出现在副标题里容易被忽略。
        if (result is null || result.Succeeded)
        {
            return;
        }

        await ShowBackupMessageAsync("同步失败", result.ErrorMessage ?? "未知错误");
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
