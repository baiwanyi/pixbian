/**
 * UI 调度队列抽象：隔离 WinRT DispatcherQueue 的线程亲和，使视图模型可离线测试。
 * 职责：提供「投递回 UI 线程」与「UI 线程节拍计时器」的最小能力面，生产实现包装
 *      DispatcherQueue，测试实现以同步内联方式执行投递。
 * 复用约定：视图模型一律经本接口切回 UI 线程修改 ObservableCollection / BitmapImage
 *          等非线程安全对象；不直接引用 DispatcherQueue 类型。
 * 关键约束：队列已关闭（TryEnqueue 返回 false）时 EnqueueAsync 必须以异常完成而非静默挂死；
 *          节拍器仅暴露 Start/Stop/Interval/Tick，不得泄露 DispatcherQueueTimer 的 WinRT 依赖。
 */

using Microsoft.UI.Dispatching;

namespace Pixbian.Services;

/// <summary>UI 线程计时器抽象。</summary>
public interface IUiDispatcherTimer : IDisposable
{
    /// <summary>节拍间隔；Start 前设置。</summary>
    TimeSpan Interval { get; set; }

    /// <summary>启动节拍。</summary>
    void Start();

    /// <summary>停止节拍（幂等）。</summary>
    void Stop();

    /// <summary>每个节拍触发一次。</summary>
    event EventHandler? Tick;
}

/// <summary>UI 调度队列抽象。</summary>
public interface IUiDispatcher : IDisposable
{
    /// <summary>尝试投递动作到 UI 线程；队列关闭时返回 false。</summary>
    bool TryEnqueue(Action action);

    /// <summary>投递动作到 UI 线程并异步等待其完成；动作异常经任务重新抛出。</summary>
    Task EnqueueAsync(Action action);

    /// <summary>创建绑定 UI 线程的节拍计时器。</summary>
    IUiDispatcherTimer CreateTimer();
}

/// <summary>生产实现：包装 WinRT DispatcherQueue。</summary>
public sealed class UiDispatcherAdapter : IUiDispatcher
{
    private readonly DispatcherQueue _queue;

    /// <summary>包装既有队列实例。</summary>
    public UiDispatcherAdapter(DispatcherQueue queue)
    {
        ArgumentNullException.ThrowIfNull(queue);
        _queue = queue;
    }

    /// <inheritdoc />
    public bool TryEnqueue(Action action) => _queue.TryEnqueue(() => action());

    /// <inheritdoc />
    public Task EnqueueAsync(Action action)
    {
        ArgumentNullException.ThrowIfNull(action);

        var completion = new TaskCompletionSource();

        if (!_queue.TryEnqueue(() =>
            {
                try
                {
                    action();
                    completion.SetResult();
                }
                catch (Exception ex)
                {
                    completion.SetException(ex);
                }
            }))
        {
            // 队列已关闭（应用退出、窗口销毁）时必须转为异常，否则 await 方静默挂死。
            completion.SetException(new InvalidOperationException("UI 调度队列不可用，无法投递任务。"));
        }

        return completion.Task;
    }

    /// <inheritdoc />
    public IUiDispatcherTimer CreateTimer() => new UiDispatcherTimerAdapter(_queue.CreateTimer());

    /// <inheritdoc />
    public void Dispose()
    {
        // DispatcherQueue 无可释放资源，容器单例走空实现。
    }
}

/// <summary>生产节拍计时器：包装 WinRT DispatcherQueueTimer。</summary>
public sealed class UiDispatcherTimerAdapter : IUiDispatcherTimer
{
    private readonly DispatcherQueueTimer _timer;

    /// <summary>包装既有计时器实例。</summary>
    public UiDispatcherTimerAdapter(DispatcherQueueTimer timer)
    {
        ArgumentNullException.ThrowIfNull(timer);
        _timer = timer;
        _timer.Tick += (_, _) => Tick?.Invoke(this, EventArgs.Empty);
    }

    /// <inheritdoc />
    public TimeSpan Interval { get => _timer.Interval; set => _timer.Interval = value; }

    /// <inheritdoc />
    public void Start() => _timer.Start();

    /// <inheritdoc />
    public void Stop() => _timer.Stop();

    /// <inheritdoc />
    public event EventHandler? Tick;

    /// <inheritdoc />
    public void Dispose() => _timer.Stop();
}
