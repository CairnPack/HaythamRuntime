namespace Haytham.Runtime.Events;

/// <summary>
/// 基元 Event。Runtime 内置事件和模块定义的事件都从它继承。
/// </summary>
/// <remarks>
/// 一个事件对象代表一次执行：派发时 new 一个新对象，订阅者在它上面读写本次执行的数据，
/// 所以同一个事件类型的两次派发不会共用数据。
/// 业务由订阅者实现，Runtime 负责按 <see cref="Execution"/> 执行订阅者、等待它们结束并收集错误。
/// </remarks>
public abstract class RuntimeEvent {

    /// <summary>
    /// 订阅者的执行方式。默认按订阅顺序依次执行，派生类可以改成并行。
    /// </summary>
    public virtual EventExecution Execution => EventExecution.Sequential;

}

/// <summary>
/// 一次事件派发怎样执行订阅者。
/// </summary>
public enum EventExecution {

    /// <summary>
    /// 按订阅顺序（即模块的依赖顺序）逐个执行，前一个结束后才开始下一个；有一个失败就停止，后面的不再执行。
    /// 适合订阅者依次修改同一份数据的事件。
    /// </summary>
    Sequential,

    /// <summary>
    /// 同时开始所有订阅者，全部结束后事件才完成；失败的订阅者全部收集后一起报告。
    /// 适合相互独立的处理。
    /// </summary>
    Parallel

}
