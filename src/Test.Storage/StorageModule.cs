using Haytham.Runtime.Events;
using Haytham.Runtime.Modules;
using Haytham.Runtime.Tasks;

namespace Test.Storage;

/// <summary>
/// 保存一个值。Storage 模块为它登记 Task，结果表示是否覆盖了旧值。
/// </summary>
public sealed record SaveValue(string Key, string Value);

/// <summary>
/// 值被保存后派发，订阅者之间相互独立，所以并行执行。
/// </summary>
public sealed class StorageChangedEvent(string key) : RuntimeEvent {

    public string Key { get; } = key;

    public override EventExecution Execution => EventExecution.Parallel;

}

/// <summary>
/// 手动验证用的模块：加载时打开一个模拟的存储资源，卸载时释放它。
/// </summary>
[RuntimeModule]
internal sealed class StorageModule(TextWriter log) : IRuntimeModule {

    private Dictionary<string, string>? store;
    private EventSource<StorageChangedEvent>? changed;

    public void Subscribe(EventSubscriber events) => events.Subscribe<RegisterEvent>(OnRegister);

    private ValueTask OnRegister(RegisterEvent e, CancellationToken cancellationToken) {
        changed = e.Registry.DefineEvent<StorageChangedEvent>();
        e.Registry.Subscribe<RuntimeEvents.OnLoad>(OnLoad);
        e.Registry.Subscribe<RuntimeEvents.OnUnload>(OnUnload);
        e.Registry.AddTask<SaveValue, bool>(OnSave);
        log.WriteLine($"[{e.ModuleId}] registered");
        return ValueTask.CompletedTask;
    }

    private ValueTask OnLoad(RuntimeEvents.OnLoad e, CancellationToken cancellationToken) {
        store = new Dictionary<string, string>();
        log.WriteLine($"[{e.ModuleId}] load: storage opened");
        return ValueTask.CompletedTask;
    }

    private ValueTask OnUnload(RuntimeEvents.OnUnload e, CancellationToken cancellationToken) {
        store = null;
        log.WriteLine($"[{e.ModuleId}] unload: storage released");
        return ValueTask.CompletedTask;
    }

    private async ValueTask OnSave(TaskEvent<SaveValue, bool> e, CancellationToken cancellationToken) {
        var replaced = store!.ContainsKey(e.Input.Key);
        store[e.Input.Key] = e.Input.Value;
        await changed!.RaiseAsync(new StorageChangedEvent(e.Input.Key), cancellationToken);
        e.SetResult(replaced);
    }

}
