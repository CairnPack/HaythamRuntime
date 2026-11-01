using Haytham.Runtime.Tasks;
using Haytham.Runtime.Work;

namespace Haytham.Runtime.Events;

public sealed class Registry {
    private bool open;
    private bool accepted;
    private WorkNode? execution;

    internal Registry(EventDispatcher owner, ModuleId module) {
        Owner = owner;
        Module = module;
    }

    internal EventDispatcher Owner { get; }
    internal ModuleId Module { get; }
    internal bool IsAccepted => accepted && !open;
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

    internal void Open(WorkNode node) {
        execution = node;
        open = true;
    }

    internal void Close() => open = false;

    internal void Accept() => accepted = true;

    private void RequireOpen() {
        if (!open) {
            throw new RuntimeContractException("Registry 的注册窗口已关闭。");
        }

        if (execution!.Host.CurrentWork.Value != execution) {
            throw new RuntimeContractException("Registry 只能由所属注册处理器修改，不能由必要子工作修改。");
        }
    }
}

internal sealed record TaskRegistration(RuntimeTask Task, ModuleId Module, TaskExitPolicy ExitPolicy);
internal sealed record EventRegistration(Type Type, ModuleId? Module, string Name, EventDispatchMode Mode);

internal sealed record EventSubscription(Type Type, ModuleId Module, Func<RuntimeEvent, EventContext, ValueTask> Invoke) {
    internal static EventSubscription Create<TEvent>(ModuleId module, RuntimeEventHandler<TEvent> handler)
        where TEvent : RuntimeEvent => new(typeof(TEvent), module, (e, context) => handler((TEvent)e, context));
}
