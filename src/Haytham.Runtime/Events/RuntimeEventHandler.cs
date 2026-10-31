namespace Haytham.Runtime.Events;

/// <summary>
/// 事件订阅者。返回的 <see cref="ValueTask"/> 结束之前，这次事件不算完成。
/// </summary>
public delegate ValueTask RuntimeEventHandler<in TEvent>(TEvent e, CancellationToken cancellationToken)
    where TEvent : RuntimeEvent;
