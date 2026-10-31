using Haytham.Runtime.Tasks;

namespace Haytham.Runtime.Events;

/// <summary>
/// 一个模块在 <see cref="RegisterEvent"/> 中提交的登记清单：它定义的 Event、它的订阅和它提供的 Task。
/// </summary>
/// <remarks>
/// 登记内容先暂存在这里，等所有模块提交后由 Runtime 统一检查冲突并合并。
/// 所以在这里登记成功不代表最终有效，合并失败时整份注册结果都不会生效。
/// </remarks>
public sealed class Registry {

    private readonly EventBus bus;
    private readonly List<Type> definedEvents = [];
    private readonly List<Subscription> subscriptions = [];
    private readonly List<TaskRoute> tasks = [];
    private bool submitted;

    internal Registry(string moduleId, EventBus bus) {
        ModuleId = moduleId;
        this.bus = bus;
    }

    public string ModuleId { get; }

    internal IReadOnlyList<Type> DefinedEvents => definedEvents;

    internal IReadOnlyList<Subscription> Subscriptions => subscriptions;

    internal IReadOnlyList<TaskRoute> Tasks => tasks;

    /// <summary>
    /// 定义本模块拥有的事件，返回只有本模块持有的派发入口。注册结果锁定后才能用它派发。
    /// </summary>
    public EventSource<TEvent> DefineEvent<TEvent>() where TEvent : RuntimeEvent {
        RequireOpen();
        definedEvents.Add(typeof(TEvent));
        return new EventSource<TEvent>(bus);
    }

    /// <summary>
    /// 订阅生命周期事件或任一模块定义的事件。
    /// </summary>
    public void Subscribe<TEvent>(RuntimeEventHandler<TEvent> handler) where TEvent : RuntimeEvent {
        RequireOpen();
        subscriptions.Add(Subscription.Create(ModuleId, handler));
    }

    /// <summary>
    /// 登记一个 Task：调用方提交 <typeparamref name="TInput"/> 时，Runtime 为这次执行新建一个 <see cref="TaskEvent{TInput, TResult}"/>，
    /// 由 <paramref name="handler"/> 执行业务并设置结果。同一种输入类型在整个 Runtime 里只能有一个 Task。
    /// </summary>
    public void AddTask<TInput, TResult>(RuntimeEventHandler<TaskEvent<TInput, TResult>> handler) {
        RequireOpen();
        tasks.Add(new TaskRoute(typeof(TInput), ModuleId, Subscription.Create(ModuleId, handler)));
    }

    internal void Submit() => submitted = true;

    private void RequireOpen() {
        if (submitted) {
            throw new InvalidOperationException($"模块 {ModuleId} 的 Registry 已经提交，不能再登记。");
        }
    }

}
