namespace Haytham.Runtime.Events;

public abstract class RuntimeEvent {
    protected RuntimeEvent() { }

    internal virtual bool IsBuiltIn => false;
}

public delegate ValueTask RuntimeEventHandler<TEvent>(TEvent e, EventContext context)
    where TEvent : RuntimeEvent;

public sealed record EventDefinition<TEvent>(string Name, EventDispatchMode Mode)
    where TEvent : RuntimeEvent;

public enum EventDispatchMode {
    SerialFailFast,
    SerialCollect,
    ParallelJoin,
    ParallelFailFast
}