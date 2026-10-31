namespace Haytham.Runtime.Events;

/// <summary>
/// Runtime 创建模块后交给 <see cref="Modules.IRuntimeModule.Subscribe"/> 的订阅入口，订阅自动记在该模块名下。
/// </summary>
public sealed class EventSubscriber {

    private readonly EventBus bus;

    internal EventSubscriber(string moduleId, EventBus bus) {
        ModuleId = moduleId;
        this.bus = bus;
    }

    public string ModuleId { get; }

    /// <summary>
    /// 订阅事件。接线阶段只能订阅 <see cref="RegisterEvent"/>。
    /// </summary>
    public void Subscribe<TEvent>(RuntimeEventHandler<TEvent> handler) where TEvent : RuntimeEvent =>
        bus.AddWiringSubscription(ModuleId, handler);

}
