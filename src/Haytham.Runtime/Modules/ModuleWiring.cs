using Haytham.Runtime.Events;

namespace Haytham.Runtime.Modules;

public sealed class ModuleWiring {
    private readonly EventDispatcher events;
    private readonly ModuleId module;
    private bool open = true;
    private bool subscribed;

    internal ModuleWiring(RuntimeHost host, ModuleId module, EventDispatcher events) {
        this.module = module;
        this.events = events;
        RootWork = new ModuleWorkPort(host, module);
    }

    public ModuleWorkPort RootWork { get; }

    public void SubscribeRegister(RuntimeEventHandler<RuntimeEvents.OnRegistering> handler) {
        ArgumentNullException.ThrowIfNull(handler);
        if (!open || subscribed) {
            throw new RuntimeContractException("接线窗口已关闭，或已经接上注册处理器。");
        }

        events.AddRegisterHandler(module, handler);
        subscribed = true;
    }

    internal void Close() => open = false;
}
