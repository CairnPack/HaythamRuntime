using Haytham.Runtime.Work;

namespace Haytham.Runtime.Events;

public abstract class LifecycleEvent : RuntimeEvent {
    private protected LifecycleEvent(ModuleId targetModule) => TargetModule = targetModule;

    public ModuleId TargetModule { get; }
}

public abstract class CancelableLifecycleEvent : LifecycleEvent {
    private protected CancelableLifecycleEvent(ModuleId targetModule) : base(targetModule) { }

    public bool IsCanceled { get; private set; }
    public void Cancel() => IsCanceled = true;
}

public static class RuntimeEvents {
    public sealed class OnRegistering : LifecycleEvent {
        internal OnRegistering(ModuleId module, Registry registry) : base(module) => Registry = registry;
        public Registry Registry { get; }
    }

    public sealed class OnLoading : LifecycleEvent {
        internal OnLoading(ModuleId module) : base(module) { }
    }

    public sealed class OnStarting : LifecycleEvent {
        internal OnStarting(ModuleId module, ModuleWorkScope continuousWork) : base(module) => ContinuousWork = continuousWork;
        public ModuleWorkScope ContinuousWork { get; }
    }

    public sealed class OnStopRequested : CancelableLifecycleEvent {
        internal OnStopRequested(ModuleId module) : base(module) { }
    }

    public sealed class OnStopping : LifecycleEvent {
        internal OnStopping(ModuleId module) : base(module) { }
    }

    public sealed class OnUnloading : LifecycleEvent {
        internal OnUnloading(ModuleId module) : base(module) { }
    }
}
