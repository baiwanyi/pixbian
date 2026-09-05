/**
 * DispatcherQueue 的异步扩展（M4）。
 * 职责：提供把代码块切回 UI 线程执行的异步等待能力。
 * 复用约定：所有在 await 之后需要修改 UI 绑定集合或创建 UI 对象的场景，都必须经本扩展切回 UI 线程。
 * 关键约束：仓储层为提升性能统一使用 ConfigureAwait(false)，await 之后会落到线程池线程；
 *          ObservableCollection 与 BitmapImage 等非线程安全类型只能在 UI 线程操作，
 *          在非 UI 线程修改会触发 XAML 层的 STATUS_STOWED_EXCEPTION (0xc000027b) 崩溃，
 *          且该异常不走 Application.UnhandledException，表现为随机崩溃，极难排查。
 */

using Microsoft.UI.Dispatching;

namespace Pixbian.Services;

/// <summary>DispatcherQueue 的异步辅助方法。</summary>
public static class DispatcherQueueExtensions
{
    /// <summary>把指定动作投递到 UI 线程执行，并异步等待其完成。</summary>
    /// <param name="queue">目标调度队列。</param>
    /// <param name="action">要在 UI 线程执行的动作。</param>
    /// <returns>动作完成后的任务；动作抛出的异常会通过该任务重新抛出。</returns>
    /// <exception cref="ArgumentNullException">参数为 null 时抛出。</exception>
    public static Task EnqueueAsync(this DispatcherQueue queue, Action action) =>
        queue.EnqueueAsync(action, DispatcherQueuePriority.Normal);

    /// <summary>按指定优先级把动作投递到 UI 线程执行，并异步等待其完成。</summary>
    /// <param name="queue">目标调度队列。</param>
    /// <param name="action">要在 UI 线程执行的动作。</param>
    /// <param name="priority">调度优先级；Low 用于把大块 UI 工作排到渲染与输入之后。</param>
    /// <returns>动作完成后的任务；动作抛出的异常会通过该任务重新抛出。</returns>
    /// <exception cref="ArgumentNullException">参数为 null 时抛出。</exception>
    public static Task EnqueueAsync(this DispatcherQueue queue, Action action, DispatcherQueuePriority priority)
    {
        ArgumentNullException.ThrowIfNull(queue);
        ArgumentNullException.ThrowIfNull(action);

        var completion = new TaskCompletionSource();

        // TryEnqueue 在队列已关闭（应用退出、窗口销毁）时返回 false。此时若置之不理，
        // 返回的任务永远不会完成，await 方将静默挂死——必须转为异常让调用方有机会收尾。
        if (!queue.TryEnqueue(priority, () =>
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
            completion.SetException(new InvalidOperationException("UI 调度队列不可用，无法投递任务。"));
        }

        return completion.Task;
    }
}
