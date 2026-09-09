/**
 * 图库页视图模型——删除链路（partial）。
 * 职责：把条目对应的磁盘文件逐个移入回收站，经通知条展示进度与结果，并同步收缩集合与索引；
 *      回收站操作经注入的 IRecycleBinService 执行，删除链路因此可离线单元测试。
 * 复用约定：进度/结果通知条复用删除专用的 ObservableProperty 与 IUiDispatcherTimer；
 *          索引清理复用 IMediaItemRepository.DeleteByPathsAsync。
 * 关键约束：置位与复位 IsDeleteInProgress 之间任何一环抛异常都必须复位（否则守卫令此后
 *          所有删除静默失效）；索引清理不接受取消令牌——文件已进回收站，中断会留幽灵条目；
 *          删除期间取消属正常路径，已删部分保留。
 */

using CommunityToolkit.Mvvm.ComponentModel;
using Pixbian.Services;

namespace Pixbian.ViewModels;

/// <summary>图库页视图模型的删除链路。</summary>
public sealed partial class GalleryViewModel
{
    /// <summary>删除结果通知条自动消失的延时。</summary>
    private static readonly TimeSpan DeleteResultAutoCloseDelay = TimeSpan.FromSeconds(5);

    private CancellationTokenSource? _deleteCts;
    private IUiDispatcherTimer? _deleteResultTimer;

    [ObservableProperty]
    private bool _isDeleteInProgress;

    [ObservableProperty]
    private bool _isDeleteResultVisible;

    [ObservableProperty]
    private string _deleteProgressText = string.Empty;

    [ObservableProperty]
    private double _deleteProgressValue;

    [ObservableProperty]
    private double _deleteProgressMaximum = 1;

    [ObservableProperty]
    private string _deleteResultText = string.Empty;

    /// <summary>删除通知条整体可见性：删除进行中或有待查看的结果时显示。</summary>
    public bool IsDeleteNotificationVisible => IsDeleteInProgress || IsDeleteResultVisible;

    partial void OnIsDeleteInProgressChanged(bool value) =>
        OnPropertyChanged(nameof(IsDeleteNotificationVisible));

    partial void OnIsDeleteResultVisibleChanged(bool value) =>
        OnPropertyChanged(nameof(IsDeleteNotificationVisible));

    /// <summary>把指定条目对应的磁盘文件逐个移入回收站，原地从列表移除，并经通知条展示进度与结果。</summary>
    /// <param name="items">待删除条目。</param>
    /// <returns>(成功删除数, 失败数)。</returns>
    /// <remarks>
    /// 回收站删除（Shell API + FOF_ALLOWUNDO）同步且耗时，放线程池执行避免卡 UI；
    /// 每删一项即回 UI 线程从集合移除并推进进度，后续条目自然前移补位，不整页重载；
    /// 结束后按成功路径批量清索引并刷新页头统计；可经 CancelDelete 中止，已删部分保留。
    /// </remarks>
    public async Task<(int Deleted, int Failed)> DeleteFilesAsync(IReadOnlyList<MediaItemViewModel> items)
    {
        if (items.Count == 0 || IsDeleteInProgress)
        {
            return (0, 0);
        }

        CloseDeleteResult();

        _deleteCts = new CancellationTokenSource();
        var token = _deleteCts.Token;

        var targets = items.ToList();

        IsDeleteInProgress = true;
        DeleteProgressMaximum = targets.Count;
        DeleteProgressValue = 0;
        DeleteProgressText = BuildProgressText(0, targets.Count);

        // 全程 try/finally：置位与复位之间任何一环抛异常（回收站 Win32 失败、
        // 数据库写入失败）都必须复位 IsDeleteInProgress——否则该标志永久为真，
        // 而本方法开头的守卫会让此后**所有**删除静默失效（实测即此症状）。
        (int Deleted, int Failed, bool Cancelled, string? FirstError) result = (0, 0, false, null);
        string? interruptError = null;

        try
        {
            result = await RunDeleteLoopAsync(targets, token);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // 非取消异常收口到通知条：既不吞掉信息，也不让界面毫无反馈。
            interruptError = $"{ex.GetType().Name}：{ex.Message}";
        }
        finally
        {
            IsDeleteInProgress = false;
            _deleteCts?.Dispose();
            _deleteCts = null;
        }

        var text = interruptError is null
            ? BuildDeleteResultText(result.Cancelled, result.Deleted, result.Failed, result.FirstError)
            : $"删除中断：{interruptError}";

        await _dispatcherQueue.EnqueueAsync(() => ShowDeleteResult(text));

        return (result.Deleted, result.Failed);
    }

    /// <summary>逐个把条目移入回收站并同步集合与索引。</summary>
    private async Task<(int Deleted, int Failed, bool Cancelled, string? FirstError)> RunDeleteLoopAsync(
        List<MediaItemViewModel> targets,
        CancellationToken token)
    {
        var deletedPaths = new List<string>(targets.Count);
        var deleted = 0;
        var failed = 0;
        var cancelled = false;
        string? firstError = null;

        foreach (var item in targets)
        {
            if (token.IsCancellationRequested)
            {
                cancelled = true;
                break;
            }

            var ok = false;

            try
            {
                // 同步的回收站 Shell 操作放线程池，避免批量删除期间冻结界面。
                ok = await Task.Run(() => _recycleBin.SendToRecycleBin(item.Item.Path), token);
            }
            catch (OperationCanceledException)
            {
                cancelled = true;
                break;
            }

            if (ok)
            {
                deleted++;
                deletedPaths.Add(item.Item.Path);

                // 先取消在途解码再移除：条目一旦离开集合，解码任务无从取消，会白占信号量槽位。
                // 绑定属性的赋值统一入 UI 队列：EnqueueAsync 的延续落在线程池线程（见 ExecuteLoadAsync）。
                await _dispatcherQueue.EnqueueAsync(() =>
                {
                    item.CancelPendingLoad();
                    Items.Remove(item);
                    _scheduler.Remove(item);
                    JustifiedSelection.Remove(item);
                    OnPropertyChanged(nameof(ItemCount));
                    DeleteProgressValue = deleted;
                    DeleteProgressText = BuildProgressText(deleted, targets.Count);
                });
            }
            else
            {
                failed++;
                firstError ??= $"无法将文件移入回收站：{item.Item.Path}";
            }
        }

        // 只要有一个成功，索引就按实际路径清理，避免残留幽灵条目。
        if (deletedPaths.Count > 0)
        {
            // 索引清理不接受取消令牌：文件已移入回收站，此刻中断会留下指向已删文件的
            // 幽灵条目，后续浏览与统计都会错——宁可多花一次写库也必须完成。
            await _mediaItems.DeleteByPathsAsync(deletedPaths, CancellationToken.None);

            if (SelectedItem is not null && deletedPaths.Contains(SelectedItem.Item.Path))
            {
                SelectedItem = null;
            }

            // 页头统计反映的是筛选结果全量规模，删除后须同步收缩，但不重载列表本身。
            await RefreshStatisticsAsync(_loadSequence);
        }

        return (deleted, failed, cancelled, firstError);
    }

    /// <summary>请求中止正在进行的删除；已移入回收站的部分保留。</summary>
    public void CancelDelete() => _deleteCts?.Cancel();

    /// <summary>关闭删除结果通知条（手动关闭与自动消失定时器共用）。</summary>
    public void CloseDeleteResult()
    {
        _deleteResultTimer?.Stop();
        IsDeleteResultVisible = false;
    }

    /// <summary>删除进度通知文本。</summary>
    private string BuildProgressText(int done, int total) =>
        $"正在从「{PageTitle}」中删除 {done}/{total} 项。";

    /// <summary>删除结果通知文本：取消 / 全部成功 / 部分失败 / 全部失败四种形态。</summary>
    private string BuildDeleteResultText(bool cancelled, int deleted, int failed, string? firstError)
    {
        if (cancelled)
        {
            return deleted == 0 ? "已取消删除。" : $"已删除 {deleted} 项，已取消。";
        }

        if (failed == 0)
        {
            return $"一切就绪！已成功从「{PageTitle}」中删除 {deleted} 项。";
        }

        if (deleted == 0)
        {
            return $"删除失败：{firstError}";
        }

        return $"已删除 {deleted} 项，{failed} 项无法删除。";
    }

    /// <summary>显示删除结果通知条，5 秒后自动消失。</summary>
    private void ShowDeleteResult(string text)
    {
        DeleteResultText = text;
        IsDeleteResultVisible = true;

        // 定时器须在 UI 线程创建，懒初始化后复用；每次显示前重置，避免上次的 Tick 提前关闭本次结果。
        _deleteResultTimer ??= _dispatcherQueue.CreateTimer();
        _deleteResultTimer.Stop();
        _deleteResultTimer.Interval = DeleteResultAutoCloseDelay;
        _deleteResultTimer.Tick -= OnDeleteResultTimerTick;
        _deleteResultTimer.Tick += OnDeleteResultTimerTick;
        _deleteResultTimer.Start();
    }

    private void OnDeleteResultTimerTick(object? sender, EventArgs e)
    {
        _deleteResultTimer?.Stop();
        IsDeleteResultVisible = false;
    }
}
