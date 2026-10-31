namespace Haytham.Runtime.Events;

/// <summary>
/// Runtime 在生命周期各阶段派发的事件。只有 Runtime 能派生和派发它们，模块只能订阅。
/// </summary>
public abstract class LifecycleEvent : RuntimeEvent {

    private protected LifecycleEvent() { }

}

/// <summary>
/// 可以被否决的生命周期事件。
/// </summary>
public abstract class CancelableLifecycleEvent : LifecycleEvent, ICancelableEvent {

    private protected CancelableLifecycleEvent() { }

    public bool IsCanceled { get; private set; }

    public void Cancel() => IsCanceled = true;

}

/// <summary>
/// Runtime 的生命周期事件。这里只是类型的命名容器，不保存订阅。
/// </summary>
/// <remarks>
/// 带 <c>ModuleId</c> 的事件只派发给该模块自己的订阅者，Runtime 按模块的依赖顺序逐个派发。
/// 模块没有订阅某个事件时，Runtime 不会替它调用任何默认的初始化或清理方法。
/// </remarks>
public static class RuntimeEvents {

    /// <summary>
    /// 加载模块：模块在这里取得并初始化自己的资源。它依赖的模块此时都已加载成功。
    /// </summary>
    public sealed class OnLoad : LifecycleEvent {

        internal OnLoad(string moduleId) => ModuleId = moduleId;

        public string ModuleId { get; }

    }

    /// <summary>
    /// 启动模块：所有模块加载成功后，按依赖顺序让模块进入可执行状态。
    /// </summary>
    public sealed class OnStart : LifecycleEvent {

        internal OnStart(string moduleId) => ModuleId = moduleId;

        public string ModuleId { get; }

    }

    /// <summary>
    /// 调用方请求停止时派发给所有模块。任一订阅者调用 <see cref="CancelableLifecycleEvent.Cancel"/> 就否决这次停止，
    /// Runtime 恢复接收新的 Task 并继续运行。派发前 Runtime 已经暂停接收新的 Task。
    /// </summary>
    public sealed class OnStopRequested : CancelableLifecycleEvent {

        internal OnStopRequested() { }

    }

    /// <summary>
    /// 不可逆的停止阶段：已接纳的 Task 都已结束，模块在这里结束自己的持续工作并等待它们真正结束。
    /// 按依赖的反序逐个派发，先停使用方，再停提供方。
    /// </summary>
    public sealed class OnStopping : LifecycleEvent {

        internal OnStopping(string moduleId) => ModuleId = moduleId;

        public string ModuleId { get; }

    }

    /// <summary>
    /// 卸载模块：按实际加载成功的反序逐个派发，模块只释放自己拥有的资源。
    /// </summary>
    public sealed class OnUnload : LifecycleEvent {

        internal OnUnload(string moduleId) => ModuleId = moduleId;

        public string ModuleId { get; }

    }

}
