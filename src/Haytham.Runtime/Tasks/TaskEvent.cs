using Haytham.Runtime.Events;

namespace Haytham.Runtime.Tasks;

/// <summary>
/// 一次 Task 执行的信道。Runtime 每执行一次 Task 就新建一个，本次的输入、结果、取消和内部工作都属于它。
/// </summary>
/// <remarks>
/// 它是 Runtime 内置的事件，和模块事件走同一套执行机制。
/// 不同信道的数据互不相通，但这不会自动解决它们同时写同一个文件这类竞争，共享资源仍由业务自己协调。
/// </remarks>
public abstract class TaskEvent : RuntimeEvent {

    private readonly object gate = new();
    private readonly List<Task> work = [];
    private bool completed;

    private protected TaskEvent(CancellationToken cancellationToken) => CancellationToken = cancellationToken;

    /// <summary>
    /// 调用方的取消请求。取消只是请求，Task 要等内部工作真正结束才算结束。
    /// </summary>
    public CancellationToken CancellationToken { get; }

    /// <summary>
    /// 在本次信道内开始一项短程工作。处理器可以不等待它直接返回，但 Runtime 会等它结束后才把结果交给调用方，
    /// 它的失败也会报告给调用方。
    /// </summary>
    public void Run(Func<CancellationToken, Task> action) {
        ArgumentNullException.ThrowIfNull(action);
        lock (gate) {
            if (completed) {
                throw new InvalidOperationException("这次 Task 已经结束，不能再在它的信道里开始工作。");
            }

            work.Add(Task.Run(() => action(CancellationToken)));
        }
    }

    /// <summary>
    /// 等待本次信道内的全部工作结束，并返回失败的工作。等待期间新开始的工作也会被等待。
    /// </summary>
    internal async Task<IReadOnlyList<Exception>> DrainAsync() {
        var failures = new List<Exception>();
        var waited = 0;
        while (true) {
            Task[] pending;
            lock (gate) {
                if (waited == work.Count) {
                    completed = true;
                    return failures;
                }

                pending = work.Skip(waited).ToArray();
                waited = work.Count;
            }

            foreach (var item in pending) {
                try {
                    await item;
                }
                catch (Exception error) {
                    failures.Add(error);
                }
            }
        }
    }

}

/// <summary>
/// 输入为 <typeparamref name="TInput"/>、结果为 <typeparamref name="TResult"/> 的一次 Task 执行。
/// </summary>
public sealed class TaskEvent<TInput, TResult> : TaskEvent {

    private TResult? result;
    private bool hasResult;

    internal TaskEvent(TInput input, CancellationToken cancellationToken) : base(cancellationToken) => Input = input;

    public TInput Input { get; }

    public void SetResult(TResult value) {
        if (hasResult) {
            throw new InvalidOperationException("这次 Task 的结果已经设置过。");
        }

        result = value;
        hasResult = true;
    }

    internal TResult GetResult() =>
        hasResult ? result! : throw new InvalidOperationException($"处理 {typeof(TInput).Name} 的 Task 结束时没有设置结果。");

}
