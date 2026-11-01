using Haytham.Runtime.Tasks;

namespace Haytham.Runtime.Events;

public sealed class Registry {
    private bool open = true;

    internal Registry(ModuleId module) => Module = module;

    internal ModuleId Module { get; }
    internal List<TaskRegistration> Tasks { get; } = [];
    internal List<EventRegistration> Events { get; } = [];
    internal List<EventSubscription> Subscriptions { get; } = [];

    public void RegisterTask(RuntimeTask task) {
        ArgumentNullException.ThrowIfNull(task);
        RequireOpen();
        Tasks.Add(new TaskRegistration(task, Module, task.ExitPolicy));
    }

    public void RegisterEvent<TEvent>(EventDefinition<TEvent> definition) where TEvent : RuntimeEvent {
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentException.ThrowIfNullOrWhiteSpace(definition.Name);
        if (!Enum.IsDefined(definition.Mode)) {
            throw new RuntimeContractException("事件派发方式无效。");
        }

        RequireOpen();
        Events.Add(new EventRegistration(typeof(TEvent), Module, definition.Name, definition.Mode));
    }

    public void Subscribe<TEvent>(RuntimeEventHandler<TEvent> handler) where TEvent : RuntimeEvent {
        ArgumentNullException.ThrowIfNull(handler);
        RequireOpen();
        Subscriptions.Add(EventSubscription.Create(Module, handler));
    }

    internal void Close() => open = false;

    private void RequireOpen() {
        if (!open) {
            throw new RuntimeContractException("Registry 的注册窗口已关闭。");
        }
    }
}

internal sealed record TaskRegistration(RuntimeTask Task, ModuleId Module, TaskExitPolicy ExitPolicy);
internal sealed record EventRegistration(Type Type, ModuleId? Module, string Name, EventDispatchMode Mode);

internal sealed record EventSubscription(Type Type, ModuleId Module, Func<RuntimeEvent, EventContext, ValueTask> Invoke) {
    internal static EventSubscription Create<TEvent>(ModuleId module, RuntimeEventHandler<TEvent> handler)
        where TEvent : RuntimeEvent => new(typeof(TEvent), module, (e, context) => handler((TEvent)e, context));
}
