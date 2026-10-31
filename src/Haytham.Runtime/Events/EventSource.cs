namespace Haytham.Runtime.Events;

/// <summary>
/// 模块派发自己定义的事件的入口，由 <see cref="Registry.DefineEvent{TEvent}"/> 返回，只有定义事件的模块持有。
/// </summary>
public sealed class EventSource<TEvent> where TEvent : RuntimeEvent {

    private readonly EventBus bus;

    internal EventSource(EventBus bus) => this.bus = bus;

    /// <summary>
    /// 执行一次事件，所有订阅者结束后返回。
    /// 订阅者失败时抛出 <see cref="RuntimeEventException"/>。
    /// </summary>
    public ValueTask RaiseAsync(TEvent e, CancellationToken cancellationToken = default) {
        ArgumentNullException.ThrowIfNull(e);
        return bus.RaiseAsync(e, owner: null, cancellationToken);
    }

}
