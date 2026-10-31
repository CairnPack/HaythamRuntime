using Haytham.Runtime.Events;

namespace Haytham.Runtime.Tasks;

/// <summary>
/// 一个已登记的 Task：调用方提交 <see cref="InputType"/> 时，由 <see cref="Subscription"/> 执行。
/// </summary>
internal sealed record TaskRoute(Type InputType, string Owner, Subscription Subscription);

/// <summary>
/// 跟踪已接纳、尚未结束的 Task，并控制是否接纳新的 Task。
/// </summary>
/// <remarks>
/// 停止时先关闭准入，再等待已接纳的 Task 自然结束。关闭准入只拦截新的外部提交，已接纳 Task 的内部工作照常进行。
/// 任务池不为每个 Task 分配线程，Task 在调用方的异步流程和线程池上执行。
/// </remarks>
internal sealed class TaskPool {

    private readonly object gate = new();
    private bool accepting;
    private int running;
    private TaskCompletionSource? idle;

    public bool TryEnter() {
        lock (gate) {
            if (!accepting) {
                return false;
            }

            running++;
            return true;
        }
    }

    public void Exit() {
        lock (gate) {
            running--;
            if (running == 0) {
                idle?.TrySetResult();
                idle = null;
            }
        }
    }

    public void Open() {
        lock (gate) {
            accepting = true;
        }
    }

    public void Close() {
        lock (gate) {
            accepting = false;
        }
    }

    /// <summary>
    /// 等待所有已接纳的 Task 结束。应先调用 <see cref="Close"/>，否则新的 Task 可能一直让它无法结束。
    /// </summary>
    public Task WaitIdleAsync() {
        lock (gate) {
            if (running == 0) {
                return Task.CompletedTask;
            }

            idle ??= new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            return idle.Task;
        }
    }

}
