namespace Haytham.Runtime.Events;

/// <summary>
/// 可以被订阅者否决的事件。
/// </summary>
/// <remarks>
/// 否决只记录结果，不中断派发：所有订阅者照常执行完，再由派发方读取 <see cref="IsCanceled"/> 决定下一步。
/// 这样每个订阅者都能看到这次判定，结果也不取决于订阅顺序。
/// </remarks>
public interface ICancelableEvent {

    bool IsCanceled { get; }

    void Cancel();

}

/// <summary>
/// 模块定义可否决事件时继承的基类。
/// </summary>
public abstract class CancelableEvent : RuntimeEvent, ICancelableEvent {

    public bool IsCanceled { get; private set; }

    public void Cancel() => IsCanceled = true;

}
